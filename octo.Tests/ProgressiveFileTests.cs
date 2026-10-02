using Octo.Services.Deezer;

namespace Octo.Tests;

public sealed class ProgressiveFileTests
{
    [Fact]
    public async Task ReaderWaitsForPublishedGrowthAndPublicationKeepsOpenReaderValid()
    {
        var directory = Path.Combine(Path.GetTempPath(), "octo-progressive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, "source.tmp");
        var final = Path.Combine(directory, "source.flac");
        await using var source = new ProgressiveFile(staging);
        await using var reader = source.OpenRead();
        var bytes = new byte[8];
        var pending = reader.ReadAsync(bytes).AsTask();
        Assert.False(pending.IsCompleted);

        await source.AppendAsync("abc"u8.ToArray());
        Assert.Equal(3, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(bytes, 0, 3));
        Assert.False(source.Completion.IsCompleted);

        await source.AppendAsync("def"u8.ToArray());
        source.Publish(final);
        source.Complete();
        Assert.True(File.Exists(final));
        Assert.Equal(final, source.Path);
        Assert.Equal(3, await reader.ReadAsync(bytes));
        Assert.Equal("def", System.Text.Encoding.ASCII.GetString(bytes, 0, 3));
        Assert.Equal(0, await reader.ReadAsync(bytes));
        await source.Completion;
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task BoundedReaderEndsAtRequestedRangeBeforeProducerValidation()
    {
        var path = Path.Combine(Path.GetTempPath(), "octo-progressive-" + Guid.NewGuid().ToString("N"));
        await using var source = new ProgressiveFile(path);
        await source.AppendAsync("abcdef"u8.ToArray());
        await using var reader = source.OpenRead(offset: 2, length: 3);
        using var output = new MemoryStream();
        await reader.CopyToAsync(output);
        Assert.Equal("cde", System.Text.Encoding.ASCII.GetString(output.ToArray()));
        Assert.False(source.Completion.IsCompleted);
        source.Fail(new InvalidDataException("validation failed"));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.Completion);
        File.Delete(path);
    }

    [Fact]
    public async Task FailureAfterPublishedBytesSurfacesOnlyWhenReaderReachesEnd()
    {
        var path = Path.Combine(Path.GetTempPath(), "octo-progressive-" + Guid.NewGuid().ToString("N"));
        await using var source = new ProgressiveFile(path);
        await using var reader = source.OpenRead();
        await source.AppendAsync("part"u8.ToArray());
        var bytes = new byte[8];
        Assert.Equal(4, await reader.ReadAsync(bytes));
        source.Fail(new InvalidDataException("truncated source"));
        Assert.Throws<IOException>(() => reader.Read(bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.Completion);
        File.Delete(path);
    }
}
