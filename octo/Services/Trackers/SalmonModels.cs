using System.Text.Json.Nodes;

namespace Octo.Services.Trackers;

public sealed record SalmonFile(string Path, long Size, string Sha256);
public sealed record SalmonTrack(int Disc, int Track, string Title, string Path);
public sealed record SalmonSourceEvidence(string Kind, string? Url, string Description);

public sealed class SalmonSubmission
{
    public string Target { get; set; } = "";
    public string RootName { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public string Source { get; set; } = "";
    public SalmonSourceEvidence SourceEvidence { get; set; } = new("", null, "");
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
