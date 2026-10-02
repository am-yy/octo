namespace Octo.Services.Deezer;

/// <summary>One reader over a shared growing source fill.</summary>
public sealed class DeezerProgressiveLease : IDisposable, IAsyncDisposable
{
    private Action? _release;
    private readonly Func<long, long?, CancellationToken, Task<Stream>> _openRange;
    private readonly Func<string?> _readyPath;
    private readonly Func<long?> _expectedLength;
    private readonly Func<string> _fingerprint;

    internal DeezerProgressiveLease(Stream stream, Task completion, string contentType,
        Func<long?> expectedLength, Func<string> fingerprint, Func<string?> readyPath,
        Func<long, long?, CancellationToken, Task<Stream>> openRange, Action release)
    {
        Stream = stream;
        Completion = completion;
        ContentType = contentType;
        _expectedLength = expectedLength;
        _fingerprint = fingerprint;
        _readyPath = readyPath;
        _openRange = openRange;
        _release = release;
    }

    public Stream Stream { get; }
    public Task Completion { get; }
    public string ContentType { get; }
    public long? ExpectedLength => _expectedLength();
    public string Fingerprint => _fingerprint();
    public string? ReadyPath => _readyPath();

    /// <summary>Open source bytes at offset; local growth first, matching Deezer CDN range beyond it.</summary>
    public Task<Stream> OpenRangeAsync(long offset, long? length = null,
        CancellationToken cancellationToken = default) => _openRange(offset, length, cancellationToken);

    public void Dispose()
    {
        try { Stream.Dispose(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Stream.DisposeAsync(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }
}

/// <summary>Reader lease that keeps an encoded cache file out of eviction until response ends.</summary>
public sealed class DeezerEncodedLease : IDisposable, IAsyncDisposable
{
    private Action? _release;
    internal DeezerEncodedLease(string path, FileStream stream, string fingerprint, Action release)
    {
        Path = path;
        Stream = stream;
        Fingerprint = fingerprint;
        _release = release;
    }
    public string Path { get; }
    public FileStream Stream { get; }
    public string Fingerprint { get; }
    public void Dispose()
    {
        try { Stream.Dispose(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }
    public async ValueTask DisposeAsync()
    {
        try { await Stream.DisposeAsync(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }
}

/// <summary>Direct Deezer stream with transfer capacity held through the response lifetime.</summary>
public sealed class DeezerDirectLease : IDisposable, IAsyncDisposable
{
    private Action? _release;
    internal DeezerDirectLease(Stream stream, string contentType, long? contentLength,
        int statusCode, string? contentRange, Action release)
    {
        Stream = stream;
        ContentType = contentType;
        ContentLength = contentLength;
        StatusCode = statusCode;
        ContentRange = contentRange;
        _release = release;
    }
    public Stream Stream { get; }
    public string ContentType { get; }
    public long? ContentLength { get; }
    public int StatusCode { get; }
    public string? ContentRange { get; }
    public void Dispose()
    {
        try { Stream.Dispose(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }
    public async ValueTask DisposeAsync()
    {
        try { await Stream.DisposeAsync(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }
}
