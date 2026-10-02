using System.Security.Cryptography;
using System.Text;
using Octo.Services.Deezer;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Octo.Tests;

public class DeezerDecryptedStreamTests
{
    private const int StripeSize = 2048;
    private const string TrackId = "1234567890";
    private const string BlowfishSecret = "g4el58wc0zvf9na1";
    private static readonly byte[] Iv = [0, 1, 2, 3, 4, 5, 6, 7];

    [Fact]
    public async Task DecryptsEncryptedStripesAcrossFragmentedReadsAndLeavesShortTailPlaintext()
    {
        var plaintext = MakePlaintext(5 * StripeSize + 317);
        var ciphertext = EncryptStripes(plaintext, TrackId);
        var source = new FragmentingStream(ciphertext, [1, 13, 777, 5, 2031, 89]);
        await using var stream = new DeezerDecryptedStream(source, TrackId);

        var actual = await DrainAsync(stream, destinationSize: 37);

        Assert.Equal(plaintext, actual);
        Assert.Equal(ciphertext.Length, source.BytesConsumed);
    }

    [Fact]
    public async Task ZeroLengthReadConsumesNothing()
    {
        var plaintext = MakePlaintext(3 * StripeSize + 73);
        var ciphertext = EncryptStripes(plaintext, TrackId);
        var source = new FragmentingStream(ciphertext, [17, 1024]);
        await using var stream = new DeezerDecryptedStream(source, TrackId);

        var callsBefore = source.ReadCalls;
        var bytesRead = await stream.ReadAsync(Memory<byte>.Empty);

        Assert.Equal(0, bytesRead);
        Assert.Equal(0, source.BytesConsumed);
        Assert.Equal(callsBefore, source.ReadCalls);
        Assert.Equal(plaintext, await DrainAsync(stream, destinationSize: 113));
    }

    [Fact]
    public async Task RangeUsesAbsoluteStripeIndexThenSkipsAndCapsPlaintext()
    {
        const int startChunk = 3;
        const int skip = 73;
        const int length = 431;
        var plaintext = MakePlaintext(6 * StripeSize + 91);
        var ciphertext = EncryptStripes(plaintext, TrackId);
        var rangeOffset = startChunk * StripeSize;
        var rangeBytes = ciphertext[rangeOffset..];
        var source = new FragmentingStream(rangeBytes, [19, 2047, 43]);
        await using var stream = new DeezerDecryptedStream(source, TrackId, startChunk, skip, length);

        var actual = await DrainAsync(stream, destinationSize: 29);
        var expected = plaintext[(rangeOffset + skip)..(rangeOffset + skip + length)];

        Assert.Equal(expected, actual);
        Assert.Equal(length, actual.Length);
    }

    [Fact]
    public async Task DisposalClosesUnderlyingSourceForSyncAndAsyncDispose()
    {
        var syncSource = new FragmentingStream([], [1]);
        new DeezerDecryptedStream(syncSource, TrackId).Dispose();
        Assert.True(syncSource.IsDisposed);

        var asyncSource = new FragmentingStream([], [1]);
        var asyncStream = new DeezerDecryptedStream(asyncSource, TrackId);
        await asyncStream.DisposeAsync();
        Assert.True(asyncSource.IsDisposed);
    }

    private static byte[] MakePlaintext(int length)
        => Enumerable.Range(0, length)
            .Select(index => (byte)((index * 73 + index / 17 + 19) & 0xff))
            .ToArray();

    internal static byte[] EncryptStripes(byte[] plaintext, string trackId)
    {
        var key = GetTrackKey(trackId);
        // Fixed vector guards the fixture key derivation independently of stream output.
        Assert.Equal("3b6e376b623172616075773a696c6a31", Convert.ToHexString(key).ToLowerInvariant());

        var encoded = new byte[plaintext.Length];
        var chunkIndex = 0;
        for (var offset = 0; offset < plaintext.Length; offset += StripeSize, chunkIndex++)
        {
            var count = Math.Min(StripeSize, plaintext.Length - offset);
            var chunk = plaintext.AsSpan(offset, count).ToArray();
            if (chunkIndex % 3 == 0 && count == StripeSize)
            {
                chunk = TransformBlowfishCbc(chunk, key, encrypt: true);
            }
            chunk.CopyTo(encoded, offset);
        }

        return encoded;
    }

    private static byte[] GetTrackKey(string trackId)
    {
        var hashHex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(trackId))).ToLowerInvariant();
        var key = new byte[16];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(hashHex[i] ^ hashHex[i + 16] ^ BlowfishSecret[i]);
        }
        return key;
    }

    private static byte[] TransformBlowfishCbc(byte[] input, byte[] key, bool encrypt)
    {
        var cipher = new CbcBlockCipher(new BlowfishEngine());
        cipher.Init(encrypt, new ParametersWithIV(new KeyParameter(key), Iv));

        var output = new byte[input.Length];
        for (var offset = 0; offset < input.Length; offset += cipher.GetBlockSize())
        {
            cipher.ProcessBlock(input, offset, output, offset);
        }
        return output;
    }

    private static async Task<byte[]> DrainAsync(Stream stream, int destinationSize)
    {
        using var output = new MemoryStream();
        var buffer = new byte[destinationSize];
        while (true)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0)
            {
                return output.ToArray();
            }
            output.Write(buffer, 0, count);
        }
    }

    private sealed class FragmentingStream(byte[] bytes, int[] fragments) : Stream
    {
        private int _position;
        private int _nextFragment;

        public int BytesConsumed => _position;
        public int ReadCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            return CopyFragment(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            return ValueTask.FromResult(CopyFragment(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int CopyFragment(Span<byte> destination)
        {
            if (destination.Length == 0 || _position == bytes.Length)
            {
                return 0;
            }

            var fragmentSize = fragments[_nextFragment++ % fragments.Length];
            var count = Math.Min(Math.Min(destination.Length, fragmentSize), bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return base.DisposeAsync();
        }
    }
}
