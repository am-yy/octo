namespace Octo.Services.Deezer;

/// <summary>Selected source metadata for HEAD/probe and delivery negotiation.</summary>
public sealed class DeezerSourceInfo
{
    internal DeezerSourceInfo(string contentType, long? expectedLength, string fingerprint,
        DateTimeOffset? modifiedUtc, string? path = null, DeezerMediaSource? media = null,
        string? etag = null)
    {
        ContentType = contentType;
        ExpectedLength = expectedLength;
        Fingerprint = fingerprint;
        ModifiedUtc = modifiedUtc;
        Path = path;
        Media = media;
        ETag = etag;
    }

    public string ContentType { get; }
    public long? ExpectedLength { get; }
    public string Fingerprint { get; }
    public DateTimeOffset? ModifiedUtc { get; }
    public string? Path { get; }
    public string? ETag { get; }
    internal DeezerMediaSource? Media { get; }
}

/// <summary>In-process source capability. Keep CDN URL inside resolver/cache services.</summary>
internal sealed record DeezerMediaSource(string TrackId, string Quality, string Format,
    string Url, string Account, string Fingerprint, long? ExpectedLength,
    string? StrongETag, DateTimeOffset? ModifiedUtc);
