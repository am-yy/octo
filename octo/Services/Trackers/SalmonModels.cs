using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Octo.Services.Trackers;

public sealed record SalmonFile(string Path, long Size, string Sha256);
public sealed record SalmonTrack(int Disc, int Track, string Title, string Path);
public sealed record SalmonSourceEvidence(string Kind, string? Url, string Description);
public sealed record SalmonArtistCredit(string Name, int Role);
public sealed record SalmonDeezerTrackMapping(string TrackId, int Disc, int Track, string Title, string Path);

public sealed class SalmonReleaseMetadata
{
    public string Title { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public int GroupYear { get; set; }
    public int Year { get; set; }
    public string ReleaseType { get; set; } = "";
    public List<SalmonArtistCredit> Artists { get; set; } = [];
}

public sealed class SalmonDeezerDownload
{
    public string AlbumId { get; set; } = "";
    public string ManifestRevision { get; set; } = "";
    public string AcquisitionDescription { get; set; } = "";
    public List<SalmonDeezerTrackMapping> Tracks { get; set; } = [];
}

public sealed record SalmonPreparationContext(
    string SourceMedium,
    string Title,
    IReadOnlyList<string> Artists,
    string ReleaseType,
    string UploadMode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? GroupId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? IdentityVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OpportunityKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeezerAlbumId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeezerManifestRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceTorrentHash = null);

public sealed class SalmonSubmission
{
    public string Target { get; set; } = "";
    public string RootName { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public string Source { get; set; } = "";
    public SalmonSourceEvidence SourceEvidence { get; set; } = new("", null, "");
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? UploadMode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? GroupId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? IdentityVersion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OpportunityKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SalmonReleaseMetadata? ReleaseMetadata { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SalmonDeezerDownload? DeezerDownload { get; set; }
    public List<SalmonTrack> Tracks { get; set; } = [];
    public List<SalmonFile> Files { get; set; } = [];
    public Dictionary<string, string> OriginalLogs { get; set; } = [];
    public string TorrentBase64 { get; set; } = "";
    public Dictionary<string, JsonNode?> Fields { get; set; } = [];
    public Dictionary<string, string> Checks { get; set; } = [];
    public string? SourceTorrentHash { get; set; }
}

public sealed class SalmonJob
{
    public string Id { get; set; } = "";
    public string Revision { get; set; } = "";
    public SalmonSubmission Submission { get; set; } = new();
    public string InfoHash { get; set; } = "";
    public string Status { get; set; } = "preparing";
    public string SeedingStatus { get; set; } = "pending";
    public string ImportStatus { get; set; } = "pending";
    public string? ImportReleaseId { get; set; }
    public DateTime? AttemptUtc { get; set; }
    public int? TorrentId { get; set; }
    public int? GroupId { get; set; }
    public string? Error { get; set; }
}
