using Microsoft.Win32.SafeHandles;

namespace Octo.Services.Deezer;

/// <summary>Flushed growing file with independent readers that wait for newly published bytes.</summary>
public sealed class ProgressiveFile : IDisposable, IAsyncDisposable
{
    private FileStream? _writer;
    private readonly object _sync = new();
    private string _path;
    private TaskCompletionSource _changed = NewSignal();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _length;
    private bool _finished;
    private Exception? _failure;

    public ProgressiveFile(string path)
    {
        _path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 81920,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    public string Path { get { lock (_sync) return _path; } }
    /// <summary>Number of bytes flushed and available to readers.</summary>
    public long Length { get { lock (_sync) return _length; } }
    public Task Completion => _completion.Task;

    public async Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (bytes.IsEmpty) return;
        var writer = _writer ?? throw new InvalidOperationException("Progressive file is published.");
        await writer.WriteAsync(bytes, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        lock (_sync)
        {
            if (_finished) throw new InvalidOperationException("Progressive file already finished.");
            _length += bytes.Length;
            Pulse();
        }
    }

    /// <summary>Mark clean producer EOF. Existing readers return EOF after consuming all bytes.</summary>
    public void Complete()
    {
        lock (_sync)
        {
            if (_finished) return;
            _writer?.Dispose();
            _writer = null;
            _finished = true;
            Pulse();
            _completion.TrySetResult();
        }
    }

    /// <summary>Expose already flushed bytes, then throw failure when reader reaches producer EOF.</summary>
    public void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_sync)
        {
            if (_finished) return;
            _failure = error;
            _writer?.Dispose();
            _writer = null;
            _finished = true;
            Pulse();
            _completion.TrySetException(error);
        }
    }

    /// <summary>Atomically move growing file to final path while existing readers keep their open handles.</summary>
    public void Publish(string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        lock (_sync)
        {
            if (_writer is null) throw new InvalidOperationException("Progressive file already published.");
            _writer.Dispose();
            _writer = null;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(finalPath)!);
            File.Move(_path, finalPath, overwrite: true);
            _path = finalPath;
        }
    }

    /// <summary>Open independent reader. A bounded reader ends at its range without waiting for validation.</summary>
    public Stream OpenRead(long offset = 0, long? length = null)
    {
        if (offset < 0 || length < 0 || length is long size && size > long.MaxValue - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
        lock (_sync)
        {
            var reader = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            return new ProgressiveReadStream(this, reader, offset, length);
        }
    }

    internal async ValueTask<int> ReadAtAsync(SafeFileHandle handle, long position, Memory<byte> destination,
        long? endPosition, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty) return 0;
        while (true)
        {
            Task? wait = null;
            var count = 0;
            lock (_sync)
            {
                var available = _length - position;
                if (available > 0)
                {
                    count = (int)Math.Min(destination.Length, available);
                    if (endPosition is long end) count = (int)Math.Min(count, Math.Max(0, end - position));
                }
                if (count > 0) { }
                else
                {
                if (endPosition is long limit && position >= limit) return 0;
                if (_finished)
                {
                    if (_failure is not null) throw new IOException("Progressive source failed.", _failure);
                    return 0;
                }
                wait = _changed.Task;
                }
            }
            if (wait is not null)
            {
                await wait.WaitAsync(cancellationToken);
                continue;
            }

            var bytes = await RandomAccess.ReadAsync(handle, destination[..count], position,
                cancellationToken);
            if (bytes == 0)
            {
                // FileSystem flush and notification should make every published byte readable.
                // Retry briefly on platforms where concurrent FileStream handles lag notification.
                await Task.Yield();
                continue;
            }
            return bytes;
        }
    }

    private void Pulse()
    {
        var changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        _writer?.Dispose();
        if (!_completion.Task.IsCompleted) Fail(new ObjectDisposedException(nameof(ProgressiveFile)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) await _writer.DisposeAsync();
        if (!_completion.Task.IsCompleted) Fail(new ObjectDisposedException(nameof(ProgressiveFile)));
    }

    private sealed class ProgressiveReadStream : Stream
    {
        private readonly ProgressiveFile _owner;
        private readonly FileStream _reader;
        private readonly long _baseOffset;
        private readonly long? _length;
        private long _position;
        private long? _remaining;

        public ProgressiveReadStream(ProgressiveFile owner, FileStream reader, long offset, long? length)
        {
            _owner = owner;
            _reader = reader;
            _baseOffset = offset;
            _length = length;
            _position = offset;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length ?? Math.Max(0, _owner.Length - _baseOffset);
        public override long Position
        {
            get => _position - _baseOffset;
            set
            {
                if (value < 0 || _length is long maximum && value > maximum)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _position = _baseOffset + value;
                _remaining = _length is long total ? total - value : null;
            }
        }

        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, long? end, CancellationToken ct)
        {
            if (_remaining == 0) return 0;
            if (_remaining is long remaining) buffer = buffer[..(int)Math.Min(buffer.Length, remaining)];
            var read = await _owner.ReadAtAsync(_reader.SafeFileHandle, _position, buffer, end, ct);
            _position += read;
            if (_remaining is long count) _remaining = count - read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ReadCoreAsync(buffer, _length is long size ? _baseOffset + size : null, cancellationToken);

        public override long Seek(long value, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => value,
                SeekOrigin.Current => Position + value,
                SeekOrigin.End => Length + value,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _reader.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _reader.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
