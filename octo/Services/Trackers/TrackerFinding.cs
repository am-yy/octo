using System.Text.Json.Serialization;
using Octo.Services.Common;

namespace Octo.Services.Trackers;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackerDecision
{
    Unknown,
    CandidateNewGroup,
    CandidateExistingGroup,
    IgnoreSameMediumFlac,
}

public sealed record TrackerClassification(
    TrackerDecision Decision,
    string UploadMode,
    int? SelectedGroupId,
    string Reason)
{
    public string Status => Decision switch
    {
        TrackerDecision.CandidateNewGroup or TrackerDecision.CandidateExistingGroup => "candidate",
        TrackerDecision.IgnoreSameMediumFlac => "ignored",
        _ => "unknown",
    };
}

public sealed class TrackerFinding
{
    public string Status { get; set; } = "unknown";
    public TrackerDecision Decision { get; set; } = TrackerDecision.Unknown;
    public string SourceMedium { get; set; } = "unknown";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceHash { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceManifestRevision { get; set; }
    public string UploadMode { get; set; } = "none";
    public int? SelectedGroupId { get; set; }
    public long IdentityVersion { get; set; }
    public DateTime? CheckedUtc { get; set; }
    public bool SearchComplete { get; set; }
    public List<TrackerMatch> Matches { get; set; } = [];
    public List<TrackerQueryCoverage> QueryCoverage { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    public string? Error { get; set; }

    public void AddDiagnostic(string? value)
    {
        if (Diagnostics.Count >= 20 || string.IsNullOrWhiteSpace(value)) return;
        var bounded = value.Length > 200 ? value[..200] : value;
        if (!Diagnostics.Contains(bounded, StringComparer.Ordinal)) Diagnostics.Add(bounded);
    }

    public void Apply(TrackerClassification classification)
    {
        Decision = classification.Decision;
        Status = classification.Status;
        UploadMode = classification.UploadMode;
        SelectedGroupId = classification.SelectedGroupId;
        if (classification.Decision == TrackerDecision.Unknown) AddDiagnostic(classification.Reason);
    }
}

public sealed record TrackerQueryCoverage(string Query, int Pages, int Results, bool Complete);

public sealed record TrackerMatch(
    int GroupId,
    string Artist,
    string Title,
    int Seeders,
    string Url,
    string ReleaseType = "",
    List<TrackerTorrent>? Torrents = null,
    bool InventoryComplete = false,
    bool RequiresExceptionReview = false);

public sealed record TrackerTorrent(
    int Id,
    string Medium,
    string Format,
    string Encoding,
    int Seeders,
    bool Scene);

public sealed class TrackerSourceLookup
{
    public string Status { get; set; } = "unknown";
    public string Tracker { get; set; } = "";
    public string Hash { get; set; } = "";
    public int? GroupId { get; set; }
    public int? TorrentId { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? ReleaseType { get; set; }
    public string? SourceMedium { get; set; }
    public string? Format { get; set; }
    public string? Url { get; set; }
    public string? Error { get; set; }
    public bool Verified => Status == "verified";
}

/// <summary>Shared conservative destination classifier for discovery, review, and submission.</summary>
public static class TrackerFindingClassifier
{
    public static TrackerClassification Classify(TrackerFinding finding, string sourceMedium,
        string releaseType, string? requestedMode = null, int? requestedGroupId = null)
    {
        var medium = TrackerCatalogClient.NormalizeMedium(sourceMedium);
        var type = TrackerCatalogClient.NormalizeReleaseType(releaseType);
        var matches = finding.Matches;
        // One fully verified same-medium FLAC is positive exclusion evidence; it remains decisive
        // even if another search page or group is incomplete.
        var existingFlac = matches.FirstOrDefault(m => m.InventoryComplete &&
            (m.Torrents ?? []).Any(t => t.Medium == medium && t.Format == "FLAC"));
        if (medium is "WEB" or "CD" && existingFlac is not null)
            return new(TrackerDecision.IgnoreSameMediumFlac, "none", existingFlac.GroupId,
                "Same-medium FLAC already exists.");
        if (!finding.SearchComplete || finding.Error is not null)
            return Unknown("Destination search or inventory is incomplete.");
        if (medium is not ("WEB" or "CD") || type is not ("album" or "ep"))
            return Unknown("Source medium or release type needs review.");
        if (matches.Count > 1) return Unknown("More than one matching group needs review.");
        if (matches.Count == 0)
            return CheckRequested(new(TrackerDecision.CandidateNewGroup, "new-group", null, "No matching group in complete searches."), requestedMode, requestedGroupId);

        var match = matches[0];
        if (!match.InventoryComplete || match.Torrents is null || match.Torrents.Count == 0)
            return Unknown("Matched group inventory is incomplete.");
        if (!string.Equals(TrackerCatalogClient.NormalizeReleaseType(match.ReleaseType), type, StringComparison.Ordinal))
            return Unknown("Matched group release type differs from source identity.");
        if (match.RequiresExceptionReview || match.Torrents.Any(t => t.Medium == medium && t.Scene))
            return Unknown("Matched release needs exception or trump review.");
        return CheckRequested(new(TrackerDecision.CandidateExistingGroup, "existing-group", match.GroupId,
            "One verified group lacks same-medium FLAC."), requestedMode, requestedGroupId);
    }

    private static TrackerClassification CheckRequested(TrackerClassification result, string? requestedMode, int? requestedGroupId)
    {
        if (!string.IsNullOrWhiteSpace(requestedMode) &&
            !string.Equals(requestedMode, result.UploadMode, StringComparison.OrdinalIgnoreCase))
            return Unknown("Destination upload mode changed.");
        if (requestedGroupId is int expected && expected != result.SelectedGroupId)
            return Unknown("Destination group changed.");
        if (requestedMode is not null && result.Decision == TrackerDecision.CandidateNewGroup && requestedGroupId is not null)
            return Unknown("New-group destination cannot select a group.");
        return result;
    }

    private static TrackerClassification Unknown(string reason) => new(TrackerDecision.Unknown, "none", null, reason);
}
