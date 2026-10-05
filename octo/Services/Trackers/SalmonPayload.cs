using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Octo.Services.Common;

namespace Octo.Services.Trackers;

public static partial class SalmonPayload
{
    // First pass deliberately excludes specialist formats and exception-based uploads.
    private static readonly HashSet<string> NewGroupFields = new(StringComparer.Ordinal)
    {
        "type", "artists[]", "importance[]", "title", "year", "releasetype", "record_label", "catalogue_number",
        "remaster", "remaster_year", "remaster_title", "remaster_record_label", "remaster_catalogue_number",
        "format", "bitrate", "media", "tags", "album_desc", "release_desc", "submit", "vbr", "image",
    };
    private static readonly HashSet<string> ExistingGroupFields = new(StringComparer.Ordinal)
    {
        "submit", "type", "groupid", "remaster", "remaster_year", "remaster_title", "remaster_record_label",
        "remaster_catalogue_number", "format", "bitrate", "vbr", "media", "release_desc",
    };

    public static string UploadMode(SalmonSubmission s) => s.UploadMode ?? "new-group";

    public static void Validate(SalmonSubmission s)
    {
        if (s.Target is not ("red" or "ops") || s.Source is not ("WEB" or "CD"))
            throw new InvalidDataException("Only RED/OPS ordinary WEB/CD releases are supported.");
        var mode = UploadMode(s);
        if (mode is not ("new-group" or "existing-group")
            || mode == "new-group" && s.GroupId is not null
            || mode == "existing-group" && s.GroupId is not > 0)
            throw new InvalidDataException("Upload mode requires a valid selected group for existing-group uploads.");
        ValidatePath(s.RootName, root: true);
        if (s.RootName.Length < 5 || s.RootName.All(char.IsDigit)
            || s.RootName is "Music" or "music" or "Album" or "album")
            throw new InvalidDataException("Use a meaningful release directory.");
        if (string.IsNullOrWhiteSpace(s.ReleaseId) || s.ReleaseId.Length > 512
            || string.IsNullOrWhiteSpace(s.SourceEvidence.Description)
            || s.SourceEvidence.Kind is not ("purchased-download" or "official-free-download" or "cd-rip" or "tracker-download" or "deezer-download"))
            throw new InvalidDataException("Official release identity and source evidence are required.");
        if (s.Source == "WEB" && (!Uri.TryCreate(s.SourceEvidence.Url, UriKind.Absolute, out var sourceUrl)
                || sourceUrl.Scheme is not ("https" or "http") || sourceUrl.UserInfo.Length != 0))
            throw new InvalidDataException("WEB release requires an official source URL.");
        if (s.SourceEvidence.Kind == "deezer-download"
            && (s.Source != "WEB" || s.DeezerDownload is null
                || !Uri.TryCreate(s.SourceEvidence.Url, UriKind.Absolute, out var deezerUrl)
                || deezerUrl.Host is not ("deezer.com" or "www.deezer.com")
                || !deezerUrl.AbsolutePath.Equals("/album/" + s.DeezerDownload.AlbumId, StringComparison.Ordinal)))
            throw new InvalidDataException("Deezer download evidence must link to its exact album source.");
        if (s.Source == "WEB" && s.SourceEvidence.Kind == "cd-rip"
            || s.Source == "CD" && s.SourceEvidence.Kind is not ("cd-rip" or "tracker-download"))
            throw new InvalidDataException("Source evidence disagrees with media.");
        if (s.SourceEvidence.Kind == "tracker-download" && !Hash40().IsMatch(s.SourceTorrentHash ?? ""))
            throw new InvalidDataException("Tracker input requires a verified source infohash.");
        if (s.SourceEvidence.Kind != "tracker-download" && s.SourceTorrentHash is not null)
            throw new InvalidDataException("Source infohash is only valid for tracker-download evidence.");
        if (s.Files.Count is 0 or > 2000 || s.Tracks.Count is 0 or > 1000
            || s.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != s.Files.Count)
            throw new InvalidDataException("Invalid or duplicate release manifest.");
        foreach (var file in s.Files)
        {
            ValidatePath(file.Path);
            if ((s.RootName + "/" + file.Path).EnumerateRunes().Count() > 180 || file.Size is <= 0 or > 20L * 1024 * 1024 * 1024
                || !Hash64().IsMatch(file.Sha256)
                || Path.GetExtension(file.Path).ToLowerInvariant() is not (".flac" or ".jpg" or ".jpeg" or ".png" or ".log" or ".cue"))
                throw new InvalidDataException("Unsupported file, size, hash, or path length.");
        }
        if (s.Tracks.Select(t => t.Path).Distinct().Count() != s.Tracks.Count
            || !s.Tracks.SequenceEqual(s.Tracks.OrderBy(t => t.Disc).ThenBy(t => t.Track))
            || !s.Tracks.Select(t => t.Disc).Distinct().SequenceEqual(Enumerable.Range(1, s.Tracks.Max(t => t.Disc))))
            throw new InvalidDataException("Manifest must contain ordered, contiguous discs and tracks.");
        foreach (var disc in s.Tracks.GroupBy(t => t.Disc))
            if (!disc.Select(t => t.Track).SequenceEqual(Enumerable.Range(1, disc.Count())))
                throw new InvalidDataException("Track manifest has gaps or duplicates.");
        if (s.Tracks.Any(t => string.IsNullOrWhiteSpace(t.Title) || !s.Files.Any(f => f.Path == t.Path)
                || !t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            || s.Files.Count(f => f.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) != s.Tracks.Count)
            throw new InvalidDataException("Every required track must have exactly one FLAC file.");
        ValidateReleaseMetadata(s, mode);
        ValidateDeezerDownload(s);
        var logs = s.Files.Where(f => f.Path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).ToList();
        if (logs.Count != s.OriginalLogs.Count || logs.Any(f => !s.OriginalLogs.TryGetValue(f.Path, out var hash) || hash != f.Sha256))
            throw new InvalidDataException("Original rip log hashes do not match payload.");
        var allowed = mode == "new-group" ? NewGroupFields : ExistingGroupFields;
        if (s.Fields.Keys.Any(k => !allowed.Contains(k)) || Field(s, "format") != "FLAC"
            || Field(s, "media") != s.Source || Field(s, "bitrate") is not ("Lossless" or "24bit Lossless")
            || Field(s, "type") != "0" || !IsTrue(Field(s, "remaster"))
            || !int.TryParse(Field(s, "remaster_year"), out var remasterYear) || remasterYear is < 1000 or > 9999
            || string.IsNullOrWhiteSpace(Field(s, "release_desc")))
            throw new InvalidDataException("Upload metadata is incomplete or outside supported rules.");
        if (mode == "new-group")
        {
            if (string.IsNullOrWhiteSpace(Field(s, "title"))
                || !int.TryParse(Field(s, "year"), out var groupYear) || groupYear is < 1000 or > 9999
                || !int.TryParse(Field(s, "releasetype"), out var releaseType) || releaseType <= 0
                || string.IsNullOrWhiteSpace(Field(s, "tags")) || Artists(s).Count == 0
                || s.Fields.GetValueOrDefault("artists[]") is not System.Text.Json.Nodes.JsonArray artists
                || artists.Any(a => a is not System.Text.Json.Nodes.JsonValue value
                    || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
                || s.Fields.GetValueOrDefault("importance[]") is not System.Text.Json.Nodes.JsonArray roles
                || roles.Count != artists.Count
                || roles.Any(r => !int.TryParse(r?.ToString(), out var role) || role is < 1 or > 8))
                throw new InvalidDataException("New-group metadata requires title, artists, roles, type, year, and tags.");
            if (s.ReleaseMetadata is not null && (Field(s, "title") != s.ReleaseMetadata.Title
                || !int.TryParse(Field(s, "year"), out groupYear) || groupYear != s.ReleaseMetadata.GroupYear
                || !int.TryParse(Field(s, "releasetype"), out var typedReleaseType)
                || typedReleaseType != (s.ReleaseMetadata.ReleaseType == "EP" ? 5 : 1)
                || !ArtistFieldsMatch(s)))
                throw new InvalidDataException("Posted group fields differ from revision-bound release metadata.");
        }
        else if (Field(s, "groupid") != s.GroupId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || s.ReleaseMetadata is null)
            throw new InvalidDataException("Existing-group release fields require selected group and typed release metadata.");
        if (s.ReleaseMetadata is not null
            && (!int.TryParse(Field(s, "remaster_year"), out remasterYear) || remasterYear != s.ReleaseMetadata.Year))
            throw new InvalidDataException("Posted release year differs from revision-bound release metadata.");
        if (s.Source == "CD" && Field(s, "bitrate") != "Lossless")
            throw new InvalidDataException("CD input must be standard lossless PCM.");
        if (s.Source == "CD" && logs.Count == 0)
            throw new InvalidDataException("First-pass CD uploads require original checked rip logs.");
        if (s.Checks.GetValueOrDefault("integrity") != "passed" || s.Checks.GetValueOrDefault("trackManifest") != "passed"
            || s.SourceEvidence.Kind == "tracker-download" && s.Checks.GetValueOrDefault("sourceTorrent") != "passed"
            || s.Source == "CD" && s.Checks.GetValueOrDefault("cdLog") != "passed"
            || Field(s, "bitrate") == "24bit Lossless" && s.Checks.GetValueOrDefault("upconvert") != "passed"
            || (s.Target == "ops" || s.ReleaseMetadata is not null) && s.Checks.GetValueOrDefault("mqa") != "passed")
            throw new InvalidDataException("Required preparation checks have not passed for this payload.");
        if (Field(s, "image") is { Length: > 0 } image
            && (!Uri.TryCreate(image, UriKind.Absolute, out var imageUrl) || imageUrl.Scheme != "https" || imageUrl.UserInfo.Length != 0))
            throw new InvalidDataException("Cover image must be an already hosted HTTPS URL.");
    }

    public static string Field(SalmonSubmission s, string name) => s.Fields.GetValueOrDefault(name)?.ToString() ?? "";
    public static string Title(SalmonSubmission s) => s.ReleaseMetadata?.Title ?? Field(s, "title");
    public static string ReleaseType(SalmonSubmission s) => s.ReleaseMetadata?.ReleaseType.ToLowerInvariant() ?? "album";
    public static List<string> Artists(SalmonSubmission s, bool mainOnly = false)
    {
        if (s.ReleaseMetadata is { } release)
            return release.Artists.Where(a => !mainOnly || a.Role == 1).Select(a => a.Name).ToList();
        if (s.Fields.GetValueOrDefault("artists[]") is not System.Text.Json.Nodes.JsonArray artists) return [];
        var roles = s.Fields.GetValueOrDefault("importance[]") as System.Text.Json.Nodes.JsonArray;
        if (mainOnly && (roles is null || roles.Count != artists.Count)) return [];
        return artists.Where((_, i) => !mainOnly || int.TryParse(roles![i]?.ToString(), out var role) && role == 1)
            .Select(v => v?.ToString() ?? "").Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
    }

    private static void ValidateReleaseMetadata(SalmonSubmission s, string mode)
    {
        if (s.IdentityVersion is < 0) throw new InvalidDataException("Identity version must be nonnegative.");
        if (s.ReleaseMetadata is not { } release)
        {
            if (mode != "new-group") throw new InvalidDataException("Existing-group jobs require revision-bound release metadata.");
            return; // Legacy new-group payloads remain replayable with their original revisions.
        }
        if (release.Title.Length is 0 or > 512 || release.ReleaseId != s.ReleaseId
            || release.ReleaseType is not ("Album" or "EP")
            || release.GroupYear is < 1000 or > 9999 || release.Year is < 1000 or > 9999
            || release.Artists.Count is 0 or > 50
            || release.Artists.Any(a => string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 512 || a.Role is < 1 or > 8)
            || release.Artists.Select(a => (a.Name, a.Role)).Distinct().Count() != release.Artists.Count)
            throw new InvalidDataException("Revision-bound release metadata is incomplete or unsupported.");
        if (s.SourceEvidence.Kind == "deezer-download" && release.ReleaseId != s.DeezerDownload?.AlbumId)
            throw new InvalidDataException("Deezer album identity differs from release identity.");
    }

    private static void ValidateDeezerDownload(SalmonSubmission s)
    {
        if (s.SourceEvidence.Kind != "deezer-download")
        {
            if (s.DeezerDownload is not null) throw new InvalidDataException("Deezer manifest evidence requires deezer-download source evidence.");
            return;
        }
        var source = s.DeezerDownload;
        if (s.Source != "WEB" || source is null || s.ReleaseMetadata is null
            || source.AlbumId.Length is 0 or > 32 || !source.AlbumId.All(char.IsAsciiDigit)
            || !Hash64().IsMatch(source.ManifestRevision)
            || string.IsNullOrWhiteSpace(source.AcquisitionDescription) || source.AcquisitionDescription.Length > 2048
            || source.AcquisitionDescription != s.SourceEvidence.Description
            || source.Tracks.Count != s.Tracks.Count || source.Tracks.Count == 0
            || source.Tracks.Select(t => t.TrackId).Distinct(StringComparer.Ordinal).Count() != source.Tracks.Count
            || !source.Tracks.SequenceEqual(source.Tracks.OrderBy(t => t.Disc).ThenBy(t => t.Track)))
            throw new InvalidDataException("Deezer download evidence requires a complete, revision-bound source manifest.");
        for (var i = 0; i < source.Tracks.Count; i++)
        {
            var mapped = source.Tracks[i];
            var track = s.Tracks[i];
            if (string.IsNullOrWhiteSpace(mapped.TrackId) || !mapped.TrackId.All(char.IsAsciiDigit)
                || mapped.Disc != track.Disc || mapped.Track != track.Track
                || SongIdentity.Key(mapped.Title) != SongIdentity.Key(track.Title)
                || mapped.Path != track.Path)
                throw new InvalidDataException("Deezer track-to-file mapping differs from the ordered payload manifest.");
        }
    }

    private static bool ArtistFieldsMatch(SalmonSubmission s)
    {
        if (s.ReleaseMetadata is not { } release
            || s.Fields.GetValueOrDefault("artists[]") is not System.Text.Json.Nodes.JsonArray artists
            || s.Fields.GetValueOrDefault("importance[]") is not System.Text.Json.Nodes.JsonArray roles
            || artists.Count != release.Artists.Count || roles.Count != release.Artists.Count) return false;
        return release.Artists.Select((artist, index) =>
            artists[index]?.ToString() == artist.Name
            && int.TryParse(roles[index]?.ToString(), out var role) && role == artist.Role).All(x => x);
    }

    private static bool IsTrue(string value) => value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    public static void ValidatePath(string value, bool root = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || Path.IsPathRooted(value)
            || value.Any(c => char.IsControl(c) || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            || value.Split('/').Any(c => c is "" or "." or ".." || c.Trim() != c || c.EndsWith('.'))
            || root && value.Contains('/')) throw new InvalidDataException("Invalid relative payload path.");
    }

    public static async Task VerifyFilesAsync(string directory, SalmonSubmission submission, bool audio, CancellationToken ct)
    {
        Validate(submission);
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Payload directory is missing or linked.");
        var actual = new List<string>();
        Walk(directory);
        if (!actual.Order().SequenceEqual(submission.Files.Select(f => f.Path).Order()))
            throw new InvalidDataException("Payload has missing or unlisted files.");
        foreach (var file in submission.Files)
        {
            var path = Path.Combine(directory, file.Path);
            if (new FileInfo(path).Length != file.Size) throw new InvalidDataException("Payload size changed.");
            await using var input = File.OpenRead(path);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)) != file.Sha256)
                throw new InvalidDataException("Payload hash changed; new preparation and approval required.");
            if (audio && file.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                await VerifyFlacAsync(path, submission.Tracks.Single(t => t.Path == file.Path), submission, ct);
        }
        void Walk(string dir)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Payload links are forbidden.");
                if ((attributes & FileAttributes.Directory) != 0) Walk(entry);
                else actual.Add(Path.GetRelativePath(directory, entry).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
    }

    private static async Task VerifyFlacAsync(string path, SalmonTrack track, SalmonSubmission submission, CancellationToken ct)
    {
        using var tag = TagLib.File.Create(path);
        if (tag is not TagLib.Flac.File || tag.Tag.Track != track.Track
            || tag.Tag.Disc != track.Disc && !(track.Disc == 1 && submission.Tracks.All(t => t.Disc == 1) && tag.Tag.Disc == 0)
            || SongIdentity.Key(tag.Tag.Title ?? "") != SongIdentity.Key(track.Title)
            || SongIdentity.Key(tag.Tag.Album ?? "") != SongIdentity.Key(Title(submission))
            || tag.Tag.Performers.Length == 0 || tag.Tag.Performers.Any(string.IsNullOrWhiteSpace)
            || !AlbumArtistMatches(submission, tag.Tag)
            || tag.Tag.Genres.Any(g => g.Replace(" ", "").ToLowerInvariant() is
                "classical" or "baroque" or "chambermusic" or "choral" or "modernclassical" or "orchestral" or "opera")
                && (tag.Tag.Composers.Length == 0 || tag.Tag.Composers.All(string.IsNullOrWhiteSpace))
            || tag.Properties.BitsPerSample is not (16 or 24) || tag.Properties.AudioChannels is not (1 or 2)
            || tag.Properties.AudioSampleRate is not (44100 or 48000 or 88200 or 96000 or 176400 or 192000)
            || tag.Properties.Duration <= TimeSpan.Zero
            || submission.Source == "CD" && (tag.Properties.BitsPerSample != 16 || tag.Properties.AudioSampleRate != 44100)
            || (tag.Properties.BitsPerSample == 24) != (Field(submission, "bitrate") == "24bit Lossless"))
            throw new InvalidDataException("FLAC tags, track order, or audio attributes are invalid.");
        using (var input = File.OpenRead(path))
        {
            var magic = new byte[4]; input.ReadExactly(magic);
            if (!magic.AsSpan().SequenceEqual("fLaC"u8)) throw new InvalidDataException("FLAC header invalid.");
            long artwork = 0;
            var last = false;
            for (var block = 0; !last; block++)
            {
                if (block > 10000) throw new InvalidDataException("Too many FLAC metadata blocks.");
                input.ReadExactly(magic);
                last = (magic[0] & 128) != 0;
                var type = magic[0] & 127;
                var size = magic[1] << 16 | magic[2] << 8 | magic[3];
                if (type is 1 or 6) artwork += size;
                if (artwork > 1024 * 1024 || input.Position + size > input.Length)
                    throw new InvalidDataException("FLAC embedded artwork plus padding exceeds 1024 KiB, or metadata is truncated.");
                input.Seek(size, SeekOrigin.Current);
            }
        }
        // The reference decoder verifies frame CRCs, total samples, and STREAMINFO MD5.
        using var process = new Process { StartInfo = new ProcessStartInfo("flac")
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
        }};
        foreach (var arg in new[] { "--test", "--silent", "--", path })
            process.StartInfo.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            if (!process.Start()) throw new InvalidDataException("FLAC integrity decoder unavailable.");
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(errors, output);
            if (process.ExitCode != 0)
                throw new InvalidDataException("FLAC integrity check failed.");
        }
        finally { if (process.Id > 0 && !process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private static bool AlbumArtistMatches(SalmonSubmission submission, TagLib.Tag tag)
    {
        if (submission.ReleaseMetadata is null) return true;
        var main = Artists(submission, mainOnly: true).Order(StringComparer.Ordinal).ToList();
        if (main.Count == 0) return true;
        var tagged = tag.AlbumArtists.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        if (tagged.Count == 0) return false;
        var separator = main.Count > 2 || main.Any(name => name.Contains('&')) ? ", " : " & ";
        var expected = SongIdentity.Key(string.Join(separator, main));
        var actual = SongIdentity.Key(string.Join(separator, tagged.Order(StringComparer.Ordinal)));
        return actual == expected || main.Count > 1
            && tagged.Any(name => SongIdentity.Key(name) == SongIdentity.Key("Various Artists"));
    }

    [GeneratedRegex("^[a-f0-9]{64}$")] private static partial Regex Hash64();
    [GeneratedRegex("^[a-fA-F0-9]{40}$")] private static partial Regex Hash40();
}
