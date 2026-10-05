using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Octo.Services.Common;

namespace Octo.Services.Trackers;

public static partial class SalmonPayload
{
    // First pass deliberately excludes specialist formats and exception-based uploads.
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "type", "artists[]", "importance[]", "title", "year", "releasetype", "record_label", "catalogue_number",
        "remaster", "remaster_year", "remaster_title", "remaster_record_label", "remaster_catalogue_number",
        "format", "bitrate", "media", "tags", "album_desc", "release_desc", "unknown", "submit", "vbr", "image",
    };

    public static void Validate(SalmonSubmission s)
    {
        if (s.Target is not ("red" or "ops") || s.Source is not ("WEB" or "CD"))
            throw new InvalidDataException("Only RED/OPS ordinary WEB/CD releases are supported.");
        ValidatePath(s.RootName, root: true);
        if (s.RootName.Length < 5 || s.RootName.All(char.IsDigit)
            || s.RootName is "Music" or "music" or "Album" or "album")
            throw new InvalidDataException("Use a meaningful release directory.");
        if (string.IsNullOrWhiteSpace(s.ReleaseId) || s.ReleaseId.Length > 512
            || string.IsNullOrWhiteSpace(s.SourceEvidence.Description)
            || s.SourceEvidence.Kind is not ("purchased-download" or "official-free-download" or "cd-rip" or "tracker-download"))
            throw new InvalidDataException("Official release identity and source evidence are required.");
        if (s.Source == "WEB" && (!Uri.TryCreate(s.SourceEvidence.Url, UriKind.Absolute, out var sourceUrl)
                || sourceUrl.Scheme is not ("https" or "http") || sourceUrl.UserInfo.Length != 0))
            throw new InvalidDataException("WEB release requires an official source URL.");
        if (s.Source == "WEB" && s.SourceEvidence.Kind == "cd-rip"
            || s.Source == "CD" && s.SourceEvidence.Kind is not ("cd-rip" or "tracker-download"))
            throw new InvalidDataException("Source evidence disagrees with media.");
        if (s.SourceEvidence.Kind == "tracker-download" && !Hash40().IsMatch(s.SourceTorrentHash ?? ""))
            throw new InvalidDataException("Tracker input requires a verified source infohash.");
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
        var logs = s.Files.Where(f => f.Path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).ToList();
        if (logs.Count != s.OriginalLogs.Count || logs.Any(f => !s.OriginalLogs.TryGetValue(f.Path, out var hash) || hash != f.Sha256))
            throw new InvalidDataException("Original rip log hashes do not match payload.");
        if (s.Fields.Keys.Any(k => !Fields.Contains(k)) || Field(s, "format") != "FLAC"
            || Field(s, "media") != s.Source || Field(s, "bitrate") is not ("Lossless" or "24bit Lossless")
            || Field(s, "type") != "0" || string.IsNullOrWhiteSpace(Field(s, "title"))
            || !int.TryParse(Field(s, "year"), out var year) || year < 1000 || year > DateTime.UtcNow.Year + 1
            || !int.TryParse(Field(s, "releasetype"), out var releaseType) || releaseType <= 0
            || string.IsNullOrWhiteSpace(Field(s, "tags")) || Artists(s).Count == 0)
            throw new InvalidDataException("Upload metadata is incomplete or outside supported rules.");
        if (s.Fields.GetValueOrDefault("artists[]") is not System.Text.Json.Nodes.JsonArray artists
            || artists.Any(a => a is not System.Text.Json.Nodes.JsonValue value
                || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
            || s.Fields.GetValueOrDefault("importance[]") is not System.Text.Json.Nodes.JsonArray roles
            || roles.Count != artists.Count
            || roles.Any(r => !int.TryParse(r?.ToString(), out var role) || role is < 1 or > 8))
            throw new InvalidDataException("Artist credits require one supported role per artist.");
        if (s.Source == "CD" && Field(s, "bitrate") != "Lossless")
            throw new InvalidDataException("CD input must be standard lossless PCM.");
        if (s.Source == "CD" && logs.Count == 0)
            throw new InvalidDataException("First-pass CD uploads require original checked rip logs.");
        if (s.Checks.GetValueOrDefault("integrity") != "passed" || s.Checks.GetValueOrDefault("trackManifest") != "passed"
            || s.SourceEvidence.Kind == "tracker-download" && s.Checks.GetValueOrDefault("sourceTorrent") != "passed"
            || s.Source == "CD" && s.Checks.GetValueOrDefault("cdLog") != "passed"
            || Field(s, "bitrate") == "24bit Lossless" && s.Checks.GetValueOrDefault("upconvert") != "passed"
            || s.Target == "ops" && s.Checks.GetValueOrDefault("mqa") != "passed")
            throw new InvalidDataException("Required preparation checks have not passed for this payload.");
        if (Field(s, "image") is { Length: > 0 } image
            && (!Uri.TryCreate(image, UriKind.Absolute, out var imageUrl) || imageUrl.Scheme != "https" || imageUrl.UserInfo.Length != 0))
            throw new InvalidDataException("Cover image must be an already hosted HTTPS URL.");
    }

    public static string Field(SalmonSubmission s, string name) => s.Fields.GetValueOrDefault(name)?.ToString() ?? "";
    public static List<string> Artists(SalmonSubmission s, bool mainOnly = false)
    {
        if (s.Fields.GetValueOrDefault("artists[]") is not System.Text.Json.Nodes.JsonArray artists) return [];
        var roles = s.Fields.GetValueOrDefault("importance[]") as System.Text.Json.Nodes.JsonArray;
        if (mainOnly && (roles is null || roles.Count != artists.Count)) return [];
        return artists.Where((_, i) => !mainOnly || int.TryParse(roles![i]?.ToString(), out var role) && role == 1)
            .Select(v => v?.ToString() ?? "").Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
    }

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
            || SongIdentity.Key(tag.Tag.Album ?? "") != SongIdentity.Key(Field(submission, "title"))
            || tag.Tag.Performers.Length == 0 || tag.Tag.Performers.Any(string.IsNullOrWhiteSpace)
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

    [GeneratedRegex("^[a-f0-9]{64}$")] private static partial Regex Hash64();
    [GeneratedRegex("^[a-fA-F0-9]{40}$")] private static partial Regex Hash40();
}
