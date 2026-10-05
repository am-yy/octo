using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Octo.Models.Settings;
using Octo.Services.Trackers;

namespace Octo.Tests;

public sealed class SalmonJobTests
{
    [Fact]
    public async Task ChangedPayloadOrCancelledApprovalNeverSubmits()
    {
        using var f = new Fixture();
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        Assert.Equal(0, f.Uploads); // Review alone is not approval.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, "wrong-revision", default));
        await File.AppendAllTextAsync(f.FrozenFile, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal(0, f.Uploads);
    }

    [Fact]
    public async Task FreshDuplicateStopsPreviouslyReviewedUpload()
    {
        using var f = new Fixture();
        await f.PrepareAsync();
        var review = JsonSerializer.SerializeToNode(await f.Jobs.ReviewAsync(f.Id, default));
        Assert.True(review!["canSubmit"]!.GetValue<bool>());
        f.Duplicate = true;
        var state = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal("prepared", state!["Status"]!.GetValue<string>());
        Assert.Equal(0, f.Uploads);
    }

    [Fact]
    public async Task AmbiguousPostPersistsAndNeverBlindlyRetries()
    {
        using var f = new Fixture { AmbiguousUpload = true };
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        await f.Jobs.SubmitAsync(f.Id, f.Revision, default);
        Assert.Equal(1, f.Uploads);
        Assert.Equal("submitting", f.StateAtPost);
        f.Restart();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        await f.Jobs.ReconcileAsync(f.Id, default);
        Assert.Equal(1, f.Uploads);
        var status = JsonSerializer.SerializeToNode(await f.Jobs.GetAsync(f.Id, default));
        Assert.Equal("uploaded", status!["Status"]!.GetValue<string>());
        Assert.Equal(f.Torrent, await f.Jobs.TorrentAsync(f.Id, default));
        var reviewJson = JsonSerializer.Serialize(await f.Jobs.ReviewAsync(f.Id, default));
        Assert.DoesNotContain(Fixture.Passkey, reviewJson);
        Assert.DoesNotContain("torrentBase64", reviewJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailureBeforeDispatchRequiresFreshReviewButCanRetry(bool cancelled)
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        var searches = 0;
        f.AfterBrowse = () =>
        {
            if (++searches != 2) return; // Interrupt the upload's cooldown after fresh duplicate searches.
            f.Clock.OnDelay = () =>
            {
                f.Clock.OnDelay = null;
                if (cancelled) cancellation.Cancel();
                else File.AppendAllText(f.FrozenFile, "changed while queued");
            };
        };
        var status = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, cancellation.Token));
        Assert.Equal("prepared", status!["Status"]!.GetValue<string>());
        Assert.Contains("not sent", status["Error"]!.GetValue<string>());
        Assert.Equal(0, f.Uploads);
        f.AfterBrowse = null;
        if (!cancelled) await File.WriteAllBytesAsync(f.FrozenFile, f.Audio);
        f.Restart();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        await f.Jobs.ReviewAsync(f.Id, default);
        await f.Jobs.SubmitAsync(f.Id, f.Revision, default);
        Assert.Equal(1, f.Uploads);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(429)]
    public async Task ExplicitRejectionAllowsOnlyNewReviewedAttempt(int httpStatus)
    {
        using var f = new Fixture { RejectedUpload = (HttpStatusCode)httpStatus };
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        var status = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal("prepared", status!["Status"]!.GetValue<string>());
        Assert.Contains("rejected", status["Error"]!.GetValue<string>());
        f.Restart();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        f.RejectedUpload = null;
        await f.Jobs.ReviewAsync(f.Id, default);
        await f.Jobs.SubmitAsync(f.Id, f.Revision, default);
        Assert.Equal(2, f.Uploads);
    }

    [Fact]
    public async Task ServerFailureStillRequiresReconciliation()
    {
        using var f = new Fixture { RejectedUpload = HttpStatusCode.InternalServerError };
        await f.PrepareAsync();
        await f.Jobs.ReviewAsync(f.Id, default);
        var status = JsonSerializer.SerializeToNode(await f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal("ambiguous", status!["Status"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SubmitAsync(f.Id, f.Revision, default));
        Assert.Equal(1, f.Uploads);
    }

    [Fact]
    public async Task IdempotentPreparationAndInterruptedTransferNeverPublishPartialPayload()
    {
        using var f = new Fixture();
        var first = JsonSerializer.Serialize(await f.Jobs.CreateAsync(f.Id, f.Submission(), default));
        Assert.Equal(first, JsonSerializer.Serialize(await f.Jobs.CreateAsync(f.Id, f.Submission(), default)));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Jobs.PutFileAsync(f.Id, 0, new MemoryStream(f.Audio[..10]), default));
        Assert.False(File.Exists(f.FrozenFile));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Jobs.ReviewAsync(f.Id, default));
        Assert.False(File.Exists(f.FrozenFile));
        var changed = f.Submission(); changed.SourceEvidence = changed.SourceEvidence with { Description = "Changed evidence" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.CreateAsync(f.Id, changed, default));
    }

    [Fact]
    public async Task ImportSelectionCannotChangePayloadOrExistingImportIntent()
    {
        using var f = new Fixture();
        await f.PrepareAsync();
        var release = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SelectImportReleaseAsync(f.Id, release, default));
        await f.Jobs.ReviewAsync(f.Id, default);
        await f.Jobs.SubmitAsync(f.Id, f.Revision, default);
        var selected = JsonSerializer.SerializeToNode(await f.Jobs.SelectImportReleaseAsync(f.Id, release, default));
        Assert.Equal(f.Revision, selected!["Revision"]!.GetValue<string>());
        Assert.Equal(release, selected["ImportReleaseId"]!.GetValue<string>());
        await File.WriteAllTextAsync(Path.Combine(f.Root, "salmon-state", f.Id, "handoff.json"), """{"import":{"phase":"unknown"}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Jobs.SelectImportReleaseAsync(f.Id, Guid.NewGuid().ToString(), default));
        Assert.Equal(1, f.Uploads);
    }

    [Theory]
    [InlineData("../escape.flac")]
    [InlineData("a/../../escape.flac")]
    [InlineData("/absolute.flac")]
    [InlineData("a\\escape.flac")]
    public void InvalidPathsCannotEnterJobs(string path) => Assert.Throws<InvalidDataException>(() => SalmonPayload.ValidatePath(path));

    [Fact]
    public void IncompleteReleaseAlteredLogsAndUnsupportedFormatsFailValidation()
    {
        using var f = new Fixture();
        var s = f.Submission(); s.Tracks.Add(new(1, 2, "Missing", "02.flac"));
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        s = f.Submission(); s.Source = "CD"; s.Fields["media"] = "CD"; s.SourceEvidence = new("cd-rip", null, "Owned CD");
        s.OriginalLogs["rip.log"] = new string('a', 64);
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        s = f.Submission(); s.Fields["format"] = "MP3";
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        s = f.Submission(); s.Checks["mqa"] = "unknown"; s.Target = "ops";
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
    }

    [Fact]
    public void MainArtistSelectionPreservesCreditRolesAndValidatesAlignment()
    {
        using var f = new Fixture();
        var s = f.Submission();
        s.Fields["artists[]"] = new JsonArray("Guest", "Artist", "Producer", "Partner", "Composer", "Remixer");
        s.Fields["importance[]"] = new JsonArray(2, 1, 7, "1", 4, 3);
        SalmonPayload.Validate(s);
        Assert.Equal(["Artist", "Partner"], SalmonPayload.Artists(s, mainOnly: true));
        Assert.Equal(["Guest", "Artist", "Producer", "Partner", "Composer", "Remixer"], SalmonPayload.Artists(s));

        s.Fields["importance[]"] = new JsonArray(1);
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        Assert.Empty(SalmonPayload.Artists(s, mainOnly: true));
        s.Fields["importance[]"] = new JsonArray(2, 1, 7, 1, 4, 99);
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
        s.Fields.Remove("importance[]");
        Assert.Throws<InvalidDataException>(() => SalmonPayload.Validate(s));
    }

    [Fact]
    public async Task OversizedFlacPaddingCannotFreeze()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.FrozenFile)!);
        using var output = new MemoryStream();
        output.Write(f.Audio.AsSpan(0, 42)); // STREAMINFO remains the first metadata block.
        output.Write(new byte[] { 1, 16, 0, 1 }); // Non-final padding, 1024 KiB + 1 byte.
        output.Write(new byte[1024 * 1024 + 1]);
        output.Write(f.Audio.AsSpan(42));
        var oversized = output.ToArray();
        await File.WriteAllBytesAsync(f.FrozenFile, oversized);
        var submission = f.Submission();
        submission.Files[0] = new("01.flac", oversized.Length, Convert.ToHexStringLower(SHA256.HashData(oversized)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SalmonPayload.VerifyFilesAsync(
            Path.GetDirectoryName(f.FrozenFile)!, submission, true, default));
        Assert.Contains("1024 KiB", error.Message);
    }

    [Theory]
    [InlineData("Classical")]
    [InlineData("Chamber Music")]
    [InlineData("Modern Classical")]
    public async Task ClassicalComposerIsVerifiedFromPayloadTags(string genre)
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.FrozenFile)!);
        await File.WriteAllBytesAsync(f.FrozenFile, f.Audio);
        using (var file = TagLib.File.Create(f.FrozenFile))
        {
            file.Tag.Genres = [genre]; file.Tag.Composers = []; file.Save();
        }
        var submission = f.Submission();
        async Task Verify()
        {
            var bytes = await File.ReadAllBytesAsync(f.FrozenFile);
            submission.Files[0] = new("01.flac", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            await SalmonPayload.VerifyFilesAsync(Path.GetDirectoryName(f.FrozenFile)!, submission, true, default);
        }
        await Assert.ThrowsAsync<InvalidDataException>(Verify);
        using (var file = TagLib.File.Create(f.FrozenFile))
        {
            file.Tag.Composers = ["Composer"]; file.Save();
        }
        await Verify();
    }

    private sealed class Fixture : HttpMessageHandler, IHttpClientFactory
    {
        public const string Passkey = "abcdef0123456789abcdef0123456789";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "octo-salmon-job-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public const string ReleaseRoot = "Artist - Album (2026)";
        public string FrozenFile => Path.Combine(Root, "music-prepared", Id, ReleaseRoot, "01.flac");
        public byte[] Audio { get; }
        public byte[] Torrent { get; }
        public string Revision { get; private set; } = "";
        public SalmonJobService Jobs { get; private set; } = null!;
        private readonly IConfiguration _config;
        public TrackerDiscoveryTests.AdvancingClock Clock { get; } = new();
        public int Uploads { get; private set; }
        public string? StateAtPost { get; private set; }
        public bool Duplicate { get; set; }
        public bool AmbiguousUpload { get; init; }
        public HttpStatusCode? RejectedUpload { get; set; }
        public Action? AfterBrowse { get; set; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var audioPath = Path.Combine(Root, "sample.flac");
            using var p = new Process { StartInfo = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true } };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", "0.1", "-sample_fmt", "s16", "-metadata", "artist=Artist", "-metadata", "album=Album", "-metadata", "title=Track", "-metadata", "track=1", "-metadata", "disc=1", audioPath }) p.StartInfo.ArgumentList.Add(arg);
            p.Start(); var error = p.StandardError.ReadToEnd(); p.WaitForExit();
            Assert.True(p.ExitCode == 0, error);
            Audio = File.ReadAllBytes(audioPath);
            Torrent = Bencode(new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["announce"] = "https://flacsfor.me/" + Passkey + "/announce",
                ["info"] = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["files"] = new object[] { new SortedDictionary<string, object>(StringComparer.Ordinal) { ["length"] = Audio.Length, ["path"] = new object[] { "01.flac" } } },
                    ["name"] = ReleaseRoot, ["piece length"] = 16384, ["pieces"] = SHA1.HashData(Audio), ["private"] = 1, ["source"] = "RED",
                },
            });
            _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Salmon:Root"] = Root, ["Trackers:red:ApiKey"] = "fake-key" }).Build();
            Restart();
        }
        public void Restart()
        {
            var queue = new TrackerDirectQueue(Path.Combine(Root, "cooldowns.json"), _config, this, Clock);
            var handoff = new SalmonMediaHandoff(this, _config, TestOptions.Monitor(new LidarrSettings()));
            Jobs = new SalmonJobService(queue, handoff, _config, Clock);
        }
        public SalmonSubmission Submission() => new()
        {
            Target = "red", RootName = ReleaseRoot, ReleaseId = "official-release-id", Source = "WEB",
            SourceEvidence = new("purchased-download", "https://label.example/release", "Receipt and complete official manifest reviewed"),
            Tracks = [new(1, 1, "Track", "01.flac")], Files = [new("01.flac", Audio.Length, Convert.ToHexStringLower(SHA256.HashData(Audio)))],
            TorrentBase64 = Convert.ToBase64String(Torrent),
            Fields = new() { ["type"] = 0, ["artists[]"] = new JsonArray("Artist"), ["importance[]"] = new JsonArray(1), ["title"] = "Album", ["year"] = 2026, ["releasetype"] = 1, ["format"] = "FLAC", ["bitrate"] = "Lossless", ["media"] = "WEB", ["tags"] = "ambient" },
            Checks = new() { ["integrity"] = "passed", ["trackManifest"] = "passed", ["mqa"] = "passed" },
        };
        public async Task PrepareAsync()
        {
            var result = JsonSerializer.SerializeToNode(await Jobs.CreateAsync(Id, Submission(), default));
            Revision = result!["Revision"]!.GetValue<string>();
            await Jobs.PutFileAsync(Id, 0, new MemoryStream(Audio), default);
        }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var action = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["action"];
            if (action == "upload")
            {
                Uploads++;
                StateAtPost = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "salmon-state", Id, "job.json")))!["status"]!.GetValue<string>();
                if (AmbiguousUpload) throw new HttpRequestException("Mock connection lost after POST");
                if (RejectedUpload is { } status) return Task.FromResult(new HttpResponseMessage(status)
                { Content = new StringContent("""{"status":"failure","error":"mock refusal"}""") });
            }
            if (action == "browse") AfterBrowse?.Invoke();
            if (action == "download") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Torrent) });
            var response = action switch
            {
                "index" => new JsonObject { ["passkey"] = Passkey, ["authkey"] = "fake-auth-key" },
                "upload" => new JsonObject { ["torrentId"] = 5, ["groupId"] = 6 },
                "torrent" => new JsonObject { ["torrent"] = new JsonObject { ["id"] = 5, ["infoHash"] = new SalmonTorrent(Torrent, "red").InfoHash }, ["group"] = new JsonObject { ["id"] = 6 } },
                _ => JsonNode.Parse(Duplicate ? """{"currentPage":1,"pages":1,"results":[{"groupId":6,"artist":"Artist","groupName":"Album","torrents":[{"seeders":0}]}]}""" : """{"results":[],"youMightLike":[]}""")!,
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["status"] = "success", ["response"] = response }.ToJsonString()) });
        }
        private static byte[] Bencode(object value)
        {
            using var s = new MemoryStream();
            void Write(object v)
            {
                switch (v)
                {
                    case string text: Write(Encoding.UTF8.GetBytes(text)); break;
                    case byte[] bytes: s.Write(Encoding.ASCII.GetBytes(bytes.Length + ":")); s.Write(bytes); break;
                    case int n: s.Write(Encoding.ASCII.GetBytes("i" + n + "e")); break;
                    case SortedDictionary<string, object> dict: s.WriteByte((byte)'d'); foreach (var (k, item) in dict) { Write(k); Write(item); } s.WriteByte((byte)'e'); break;
                    case object[] list: s.WriteByte((byte)'l'); foreach (var item in list) Write(item); s.WriteByte((byte)'e'); break;
                }
            }
            Write(value); return s.ToArray();
        }
        protected override void Dispose(bool disposing) { if (disposing) Directory.Delete(Root, true); base.Dispose(disposing); }
    }
}
