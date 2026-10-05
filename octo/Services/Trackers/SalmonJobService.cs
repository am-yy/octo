using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Octo.Services.Trackers;

/// <summary>Durable review and API submission. Discovery acquisition closure never removes these jobs.</summary>
public sealed class SalmonJobService(TrackerDirectQueue queue, SalmonMediaHandoff handoff, IConfiguration config, TimeProvider? time = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly TrackerCatalogClient _catalog = new(queue, time);
    private string Root => config["Salmon:Root"] ?? "/hangar/torrent-downloads";

    public async Task<object> ContextAsync(string target, CancellationToken ct)
    {
        var index = await IndexAsync(target, ct);
        return new { announce = Announce(target, index) };
    }

    public async Task<object> SourceAsync(string hash, CancellationToken ct)
    {
        if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$")) throw new InvalidDataException("Invalid source infohash.");
        var bytes = await handoff.GetVerifiedSourceTorrentAsync(hash.ToLowerInvariant(), ct);
        return new { complete = bytes is not null, infoHash = hash.ToLowerInvariant(), torrentBase64 = bytes is null ? null : Convert.ToBase64String(bytes) };
    }

    public async Task<object> CreateAsync(string id, SalmonSubmission submission, CancellationToken ct)
    {
        ValidateId(id);
        SalmonPayload.Validate(submission);
        var bytes = Convert.FromBase64String(submission.TorrentBase64);
        var torrent = new SalmonTorrent(bytes, submission.Target);
        torrent.Match(submission);
        var revision = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(submission, Json)));
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(StatePath(id)))
            {
                var prior = Load(id);
                if (prior.Revision != revision) throw new InvalidOperationException("Preparation ID already belongs to a different payload revision.");
                return new { prior.Id, prior.Revision };
            }
            if (submission.SourceEvidence.Kind == "tracker-download"
                && !await handoff.VerifySourceAsync(submission.SourceTorrentHash!.ToLowerInvariant(), ct))
                throw new InvalidOperationException("Source torrent must complete a full successful recheck before preparation.");
            PrivateDirectory(StateDirectory(id));
            await PrivateBytesAsync(Path.Combine(StateDirectory(id), "payload.torrent"), bytes, ct);
            submission.TorrentBase64 = ""; // Passkey-bearing bytes stay in the private artifact, never a response model.
            var job = new SalmonJob { Id = id, Revision = revision, Submission = submission, InfoHash = torrent.InfoHash };
            Save(job);
            return new { job.Id, job.Revision };
        }
        finally { gate.Release(); }
    }

    public async Task PutFileAsync(string id, int index, Stream input, CancellationToken ct)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var job = Load(id);
            if (job.Status != "preparing") throw new InvalidOperationException("Frozen jobs cannot be edited. Prepare a new revision.");
            if (index < 0 || index >= job.Submission.Files.Count) throw new InvalidDataException("Unknown manifest file index.");
            var file = job.Submission.Files[index];
            var directory = Staging(job);
            var path = Path.Combine(directory, file.Path);
            RequireUnlinkedPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Keep interrupted transfer bytes outside the music tree and publish only after verification.
            var temporary = Path.Combine(StateDirectory(id), "transfer-" + index + ".tmp");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                PrivateFile(temporary);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > file.Size) throw new InvalidDataException("Transferred file exceeds manifest size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                output.Flush(true);
            }
            if (size != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                throw new InvalidDataException("Transferred file differs from manifest.");
            if (File.Exists(path))
            {
                await using var existing = File.OpenRead(path);
                if (new FileInfo(path).Length != file.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, ct)) != file.Sha256)
                    throw new InvalidOperationException("Conflicting staged content; no overwrite performed.");
                File.Delete(temporary);
            }
            else File.Move(temporary, path);
        }
        finally { gate.Release(); }
    }

    public async Task<object> ReviewAsync(string id, CancellationToken ct)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var job = Load(id);
            if (job.Status == "preparing")
            {
                // A crash after rename and before Save resumes by verifying the already published directory.
                var directory = Directory.Exists(Frozen(job)) ? Frozen(job) : Staging(job);
                await VerifyAsync(job, directory, true, ct);
                if (directory != Frozen(job))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Frozen(job))!);
                    Directory.Move(directory, Frozen(job));
                }
                job.Status = "prepared";
                Save(job);
            }
            await VerifyAsync(job, Frozen(job), false, ct);
            var duplicates = await DuplicatesAsync(job, ct);
            var canSubmit = job.Status is "prepared" or "ready" && duplicates.Status == "missing";
            if (job.Status is "prepared" or "ready") { job.Status = canSubmit ? "ready" : "prepared"; Save(job); }
            return new
            {
                job.Id, job.Revision, target = job.Submission.Target, canSubmit,
                submission = ReviewSubmission(job.Submission), freshDuplicates = duplicates,
                job.Status, job.SeedingStatus, job.ImportStatus,
            };
        }
        finally { gate.Release(); }
    }

    public async Task<object> SubmitAsync(string id, string revision, CancellationToken ct)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var job = Load(id);
            if (revision != job.Revision) throw new InvalidOperationException("Approval belongs to another payload revision.");
            if (job.Status is "submitting" or "ambiguous")
                throw new InvalidOperationException("Submission outcome needs reconciliation. Upload will not be repeated.");
            if (job.TorrentId is not null) return Summary(job);
            if (job.Status != "ready") throw new InvalidOperationException("Review this prepared payload before approving submission.");
            await VerifyAsync(job, Frozen(job), true, ct);
            var index = await IndexAsync(job.Submission.Target, ct);
            var preparedTorrent = new SalmonTorrent(await File.ReadAllBytesAsync(Artifact(job, "payload.torrent"), ct), job.Submission.Target);
            if (preparedTorrent.Announce != Announce(job.Submission.Target, index))
                throw new InvalidOperationException("Tracker passkey changed; prepare and approve a new torrent.");
            var duplicates = await DuplicatesAsync(job, ct);
            if (duplicates.Status != "missing")
            {
                job.Status = "prepared"; job.Error = "Fresh destination search found a release or could not establish absence.";
                Save(job); return Summary(job);
            }
            using var form = new MultipartFormDataContent();
            foreach (var (key, value) in job.Submission.Fields)
            {
                if (value is JsonArray values) foreach (var item in values) Add(key, item);
                else Add(key, value);
            }
            var auth = index["authkey"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(auth)) throw new InvalidDataException("Tracker did not provide API upload authorization.");
            form.Add(new StringContent(auth), "auth");
            form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(Artifact(job, "payload.torrent"), ct)), "file_input", job.Submission.RootName + ".torrent");
            foreach (var log in job.Submission.OriginalLogs.Keys)
                form.Add(new StreamContent(File.OpenRead(Path.Combine(Frozen(job), log))), "logfiles[]", Path.GetFileName(log));
            var dispatched = false;
            try
            {
                var reply = await queue.SendAsync(job.Submission.Target, "upload", HttpMethod.Post,
                    new Dictionary<string, string>(), form, ct, async token =>
                    {
                        await VerifyAsync(job, Frozen(job), false, token);
                        return true;
                    }, dispatching: () =>
                    {
                        job.Status = "submitting"; job.AttemptUtc = DateTime.UtcNow; job.Error = null;
                        Save(job); // A crash from this point onward requires reconciliation, never a blind retry.
                        dispatched = true;
                    });
                var body = TrackerDirectQueue.Parse(reply);
                job.TorrentId = PositiveId(body, "torrentId", "torrentid");
                job.GroupId = PositiveId(body, "groupId", "groupid");
                job.Status = "uploaded";
                Save(job);
            }
            catch (Exception ex)
            {
                if (!dispatched || ex is TrackerRequestRejectedException)
                {
                    job.Status = "prepared";
                    job.Error = dispatched ? "Tracker rejected upload. Review and approve again before retrying."
                        : "Upload was not sent. Review and approve again before retrying.";
                }
                else
                {
                    job.Status = "ambiguous";
                    job.Error = "Upload outcome requires reconciliation; submission will not be repeated.";
                }
                Save(job);
                return Summary(job);
            }
            await RetrieveAsync(job, ct);
            return Summary(job);
            void Add(string key, JsonNode? value)
            {
                if (value is null) return;
                var text = value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean)
                    ? (boolean ? "1" : "0") : value.ToString();
                form.Add(new StringContent(text), key);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<object> ReconcileAsync(string id, CancellationToken ct)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var job = Load(id);
            if (job.Status is "submitting" or "ambiguous")
            {
                try
                {
                    var result = await queue.GetAsync(job.Submission.Target, "torrent",
                        new Dictionary<string, string> { ["hash"] = job.InfoHash.ToUpperInvariant() }, ct);
                    var torrent = result["torrent"] as JsonObject ?? throw new InvalidDataException();
                    var group = result["group"] as JsonObject ?? throw new InvalidDataException();
                    var foundHash = torrent["infoHash"]?.ToString() ?? torrent["info_hash"]?.ToString();
                    if (!string.Equals(foundHash, job.InfoHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                    job.TorrentId = PositiveId(torrent, "id", "torrentId");
                    job.GroupId = PositiveId(group, "id", "groupId");
                    job.Status = "uploaded"; job.Error = null; Save(job);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    job.Error = "No matching upload confirmed. Reconcile again or inspect tracker; upload remains blocked.";
                    Save(job); return Summary(job);
                }
            }
            if (job.TorrentId is not null) await RetrieveAsync(job, ct);
            return Summary(job);
        }
        finally { gate.Release(); }
    }

    public async Task<object> HandoffAsync(string id, CancellationToken ct)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var job = Load(id);
            if (job.TorrentId is null) throw new InvalidOperationException("Confirmed upload required before handoff.");
            await VerifyAsync(job, Frozen(job), false, ct);
            await RetrieveAsync(job, ct);
            if (!File.Exists(Artifact(job, "official.torrent"))) return Summary(job);
            // The handoff service owns private, durable intent/receipt records for both external systems.
            var seeded = await handoff.SeedAsync(id, job.Submission.Target, job.Submission.RootName, job.InfoHash,
                await File.ReadAllBytesAsync(Artifact(job, "official.torrent"), ct), job.Submission.Files, ct);
            job.SeedingStatus = seeded.State; job.Error = seeded.Error; Save(job);
            if (seeded.State == "seeding")
            {
                var imported = await handoff.ImportAsync(id, job.Submission.RootName,
                    string.Join(" & ", SalmonPayload.Artists(job.Submission, mainOnly: true)), SalmonPayload.Field(job.Submission, "title"),
                    job.ImportReleaseId ?? job.Submission.ReleaseId, ct);
                job.ImportStatus = imported.State; job.Error = imported.Error; Save(job);
            }
            return Summary(job);
        }
        finally { gate.Release(); }
    }

    public async Task<object> SelectImportReleaseAsync(string id, string releaseId, CancellationToken ct)
    {
        if (!Guid.TryParse(releaseId, out var mbid)) throw new InvalidDataException("Select a MusicBrainz release UUID.");
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var job = Load(id);
            if (job.TorrentId is null) throw new InvalidOperationException("Import selection requires a confirmed upload.");
            var selected = mbid.ToString();
            if (job.ImportReleaseId == selected) return Summary(job);
            var handoffPath = Artifact(job, "handoff.json");
            if (File.Exists(handoffPath) && JsonNode.Parse(await File.ReadAllTextAsync(handoffPath, ct))?["import"] is not null)
                throw new InvalidOperationException("An import command already has a durable intent; reconcile it before changing selection.");
            // This changes only the independent listening import, never the approved upload payload.
            job.ImportReleaseId = selected; job.ImportStatus = "pending"; Save(job);
            return Summary(job);
        }
        finally { gate.Release(); }
    }

    public async Task<byte[]> TorrentAsync(string id, CancellationToken ct)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try
        {
            var job = Load(id);
            if (job.TorrentId is null) throw new InvalidOperationException("No confirmed destination torrent.");
            await RetrieveAsync(job, ct);
            if (!File.Exists(Artifact(job, "official.torrent"))) throw new InvalidOperationException("Official torrent retrieval pending.");
            return await File.ReadAllBytesAsync(Artifact(job, "official.torrent"), ct);
        }
        finally { gate.Release(); }
    }

    public async Task<object> GetAsync(string id, CancellationToken ct)
    {
        var gate = Gate(id); await gate.WaitAsync(ct);
        try { return Summary(Load(id)); } finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken ct)
    {
        var root = Path.Combine(Root, "salmon-state");
        if (!Directory.Exists(root)) return [];
        var jobs = new List<object>();
        foreach (var dir in Directory.EnumerateDirectories(root).Where(d => Regex.IsMatch(Path.GetFileName(d), "^[a-f0-9]{32}$")))
            if (File.Exists(Path.Combine(dir, "job.json"))) jobs.Add(await GetAsync(Path.GetFileName(dir), ct));
        return jobs;
    }

    private async Task RetrieveAsync(SalmonJob job, CancellationToken ct)
    {
        if (job.TorrentId is null || File.Exists(Artifact(job, "official.torrent"))) return;
        try
        {
            var reply = await queue.SendAsync(job.Submission.Target, "download", HttpMethod.Get,
                new Dictionary<string, string> { ["id"] = job.TorrentId.Value.ToString() }, null, ct);
            if ((int)reply.Status != 200) throw new InvalidDataException();
            var torrent = new SalmonTorrent(reply.Body, job.Submission.Target);
            if (torrent.InfoHash != job.InfoHash) throw new InvalidDataException();
            torrent.Match(job.Submission);
            await torrent.VerifyPiecesAsync(Frozen(job), ct);
            await PrivateBytesAsync(Artifact(job, "official.torrent"), reply.Body, ct);
            job.Error = null; Save(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Error = "Upload confirmed; official destination torrent retrieval or verification pending.";
            Save(job);
        }
    }

    private async Task VerifyAsync(SalmonJob job, string directory, bool audio, CancellationToken ct)
    {
        await SalmonPayload.VerifyFilesAsync(directory, job.Submission, audio, ct);
        var torrent = new SalmonTorrent(await File.ReadAllBytesAsync(Artifact(job, "payload.torrent"), ct), job.Submission.Target);
        if (torrent.InfoHash != job.InfoHash) throw new InvalidDataException("Prepared torrent changed.");
        torrent.Match(job.Submission);
        await torrent.VerifyPiecesAsync(directory, ct);
    }

    private async Task<TrackerFinding> DuplicatesAsync(SalmonJob job, CancellationToken ct) =>
        await _catalog.SearchAsync(job.Submission.Target,
            SalmonPayload.Artists(job.Submission).Select(a => new ReleaseName(a, SalmonPayload.Field(job.Submission, "title"))), ct)
        ?? new TrackerFinding { Error = "Destination duplicate check incomplete" };

    private Task<JsonObject> IndexAsync(string target, CancellationToken ct) =>
        queue.GetAsync(target, "index", new Dictionary<string, string>(), ct);

    private static string Announce(string target, JsonObject index)
    {
        var passkey = index["passkey"]?.ToString();
        if (target is not ("red" or "ops") || !Regex.IsMatch(passkey ?? "", "^[a-zA-Z0-9]{20,64}$"))
            throw new InvalidDataException("Tracker did not provide preparation context.");
        return (target == "red" ? "https://flacsfor.me/" : "https://home.opsfet.ch/") + passkey + "/announce";
    }
    private static int PositiveId(JsonObject value, string first, string second) =>
        int.TryParse((value[first] ?? value[second])?.ToString(), out var id) && id > 0 ? id
            : throw new InvalidDataException("Tracker upload receipt incomplete.");
    private static object Summary(SalmonJob job) => new
    {
        job.Id, job.Revision, target = job.Submission.Target, job.Status, job.SeedingStatus, job.ImportStatus, job.ImportReleaseId,
        job.TorrentId, job.GroupId, job.Error, releaseId = job.Submission.ReleaseId,
        artist = string.Join(" & ", SalmonPayload.Artists(job.Submission)), album = SalmonPayload.Field(job.Submission, "title"),
    };
    private static object ReviewSubmission(SalmonSubmission s) => new
    {
        s.RootName, s.ReleaseId, s.Source, s.SourceEvidence, s.Tracks, s.Files, s.OriginalLogs, s.Fields, s.Checks, s.SourceTorrentHash,
    };
    private SemaphoreSlim Gate(string id) { ValidateId(id); return _gates.GetOrAdd(id, _ => new(1, 1)); }
    private static void ValidateId(string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new InvalidDataException("Job ID must be 32 lowercase hexadecimal characters.");
    }
    private string StateDirectory(string id) => Path.Combine(Root, "salmon-state", id);
    private string StatePath(string id) => Path.Combine(StateDirectory(id), "job.json");
    private string Artifact(SalmonJob job, string name) => Path.Combine(StateDirectory(job.Id), name);
    private string Staging(SalmonJob job) => Path.Combine(Root, "music-prepared", ".incoming", job.Id, job.Submission.RootName);
    private string Frozen(SalmonJob job) => Path.Combine(Root, "music-prepared", job.Id, job.Submission.RootName);
    private SalmonJob Load(string id)
    {
        ValidateId(id);
        if (!File.Exists(StatePath(id))) throw new KeyNotFoundException("Unknown Salmon job.");
        return JsonSerializer.Deserialize<SalmonJob>(File.ReadAllText(StatePath(id)), Json)
            ?? throw new InvalidDataException("Job state invalid.");
    }
    private void Save(SalmonJob job)
    {
        PrivateDirectory(StateDirectory(job.Id));
        var temp = StatePath(job.Id) + ".tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            PrivateFile(temp); JsonSerializer.Serialize(output, job, Json); output.Flush(true);
        }
        File.Move(temp, StatePath(job.Id), true);
    }
    private void RequireUnlinkedPath(string path)
    {
        var boundary = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(path);
        if (!current.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Job path escapes storage root.");
        while (current.Length > boundary.Length)
        {
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked job paths are forbidden.");
            current = Path.GetDirectoryName(current)!;
        }
    }
    private void PrivateDirectory(string path)
    {
        RequireUnlinkedPath(path);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void PrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static async Task PrivateBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            PrivateFile(temp); await output.WriteAsync(bytes, ct); output.Flush(true);
        }
        File.Move(temp, path, true);
    }
}
