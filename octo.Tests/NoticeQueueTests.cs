using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Fingerprint;
using Octo.Services.Library;

namespace Octo.Tests;

/// <summary>
/// What Octo has asked each person, and what they answered (#47). The rules that matter are the
/// ones a person would notice: a settled question never comes back, an answer settles it for
/// everyone asked about the same file, and one answer is one AcoustID submission.
/// </summary>
public class NoticeQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "octo-notices-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static Song Song(string title = "Teardrop") =>
        new() { Artist = "Massive Attack", Title = title, Album = "Mezzanine" };

    private static VerificationResult Verdict(InconclusiveReason reason = InconclusiveReason.NoEntry,
        string? fingerprint = "AQADtEqk", string? candidate = null) =>
        new() { Reason = reason, Fingerprint = fingerprint, DurationSeconds = 330, CandidateRecordingId = candidate };

    private static NoticeEntry Only(NoticeQueue queue, string user) => queue.ForUser(user, NoticeKind.Review).Single();

    private static string Asked(NoticeQueue queue, string user, string path, string id, VerificationResult? verdict = null)
    {
        queue.AddReview(user, path, Song(), verdict ?? Verdict());
        var key = NoticeQueue.ReviewKey(user, path);
        queue.SetNavidromeId(key, id);
        queue.MarkQueued([key]);
        return key;
    }

    [Theory]
    [InlineData(InconclusiveReason.NoEntry, "AcoustID has never heard this recording")]
    [InlineData(InconclusiveReason.BelowThreshold, "AcoustID was not sure what this is")]
    public void AddReview_SaysWhyOctoIsAsking(InconclusiveReason reason, string expected)
    {
        var queue = new NoticeQueue();

        Assert.True(queue.AddReview("alice", "/music/a.flac", Song(), Verdict(reason)));

        var entry = Only(queue, "alice");
        Assert.Equal(expected, entry.Reason);
        Assert.Equal(NoticeState.Waiting, entry.State);
        Assert.Equal("flac", entry.FileFormat);
    }

    [Fact]
    public void AddReview_ASourceDisagreement_NamesWhatAcoustIdHeard()
    {
        var queue = new NoticeQueue();
        var verdict = Verdict(InconclusiveReason.SourceDisagreed) with { MatchedArtist = "Elizabeth Fraser", MatchedTitle = "Song to the Siren" };

        queue.AddReview("alice", "/music/a.mp3", Song(), verdict);

        Assert.Equal("AcoustID thinks this is 'Elizabeth Fraser - Song to the Siren'", Only(queue, "alice").Reason);
    }

    /// <summary>A dismissal would otherwise be undone by the next download of the same file.</summary>
    [Fact]
    public void AddReview_AFileAlreadyAskedAbout_IsNeverAskedAgainInAnyState()
    {
        var queue = new NoticeQueue();
        var key = Asked(queue, "alice", "/music/a.flac", "nd1");
        queue.Resolve(key, NoticeState.Dismissed);

        Assert.False(queue.AddReview("Alice", "/music/a.flac", Song(), Verdict()));
        Assert.Equal(NoticeState.Dismissed, Only(queue, "alice").State);
    }

    [Fact]
    public void IsQueued_IsPerPersonAndOnlyWhileAsked()
    {
        var queue = new NoticeQueue();
        var key = Asked(queue, "alice", "/music/a.flac", "nd1");

        Assert.True(queue.IsQueued("ALICE", "nd1"));
        Assert.False(queue.IsQueued("bob", "nd1"));
        Assert.False(queue.IsQueued("alice", "nd2"));

        queue.Resolve(key, NoticeState.Dismissed);
        Assert.False(queue.IsQueued("alice", "nd1"));
    }

    [Fact]
    public void MarkKept_NotAskedAboutThisTrack_IsNull()
    {
        var queue = new NoticeQueue();
        Asked(queue, "alice", "/music/a.flac", "nd1");

        Assert.Null(queue.MarkKept("alice", "nd2"));
        Assert.Null(queue.MarkKept("bob", "nd1"));
        Assert.Equal(NoticeState.Queued, Only(queue, "alice").State);
    }

    /// <summary>
    /// A download nobody requested is asked of every allowed user. The question was about the
    /// file, so one person's Keep answers it for all of them, and only their entry keeps the
    /// fingerprint: the same fingerprint sent twice would count as two confirmations.
    /// </summary>
    [Fact]
    public void MarkKept_AnswersEveryoneAskedAboutTheFile_ButSubmitsOnce()
    {
        var queue = new NoticeQueue();
        Asked(queue, "alice", "/music/a.flac", "nd1");
        Asked(queue, "bob", "/music/a.flac", "nd1");

        var kept = queue.MarkKept("bob", "nd1");

        Assert.Equal("bob", kept?.Username);
        Assert.Equal(NoticeState.Kept, Only(queue, "alice").State);
        Assert.Equal(NoticeState.Kept, Only(queue, "bob").State);
        Assert.Equal("bob", Assert.Single(queue.AwaitingSubmission()).Username);
    }

    [Fact]
    public void Resolve_AnythingButKeep_DropsTheFingerprint()
    {
        var queue = new NoticeQueue();
        var key = Asked(queue, "alice", "/music/a.flac", "nd1");

        queue.Resolve(key, NoticeState.Dismissed);

        Assert.Null(Only(queue, "alice").Fingerprint);
        Assert.Empty(queue.AwaitingSubmission());
    }

    [Fact]
    public void MarkActed_SettlesOpenQuestionsForEveryone()
    {
        var queue = new NoticeQueue();
        Asked(queue, "alice", "/music/a.flac", "nd1");
        Asked(queue, "bob", "/music/a.flac", "nd1");

        queue.MarkActed("nd1");

        Assert.Equal(NoticeState.Acted, Only(queue, "alice").State);
        Assert.Equal(NoticeState.Acted, Only(queue, "bob").State);
        Assert.Empty(queue.AwaitingSubmission());
    }

    /// <summary>
    /// Taking a track out of Review and dropping it into Delete is one answer, Delete, even
    /// though the sweep saw the removal first.
    /// </summary>
    [Fact]
    public void MarkActed_ShortlyAfterADismissal_RecordsTheAction()
    {
        var queue = new NoticeQueue();
        var key = Asked(queue, "alice", "/music/a.flac", "nd1");
        queue.Resolve(key, NoticeState.Dismissed);

        queue.MarkActed("nd1");

        Assert.Equal(NoticeState.Acted, Only(queue, "alice").State);
    }

    [Fact]
    public void MarkSubmitted_SentOrRefused_EitherWayTheFingerprintGoes()
    {
        var queue = new NoticeQueue();
        Asked(queue, "alice", "/music/a.flac", "nd1");
        Asked(queue, "bob", "/music/b.flac", "nd2");
        queue.MarkKept("alice", "nd1");
        queue.MarkKept("bob", "nd2");

        queue.MarkSubmitted([NoticeQueue.ReviewKey("alice", "/music/a.flac")], sent: true);
        queue.MarkSubmitted([NoticeQueue.ReviewKey("bob", "/music/b.flac")], sent: false);

        Assert.True(Only(queue, "alice").Submitted);
        Assert.False(Only(queue, "bob").Submitted);
        Assert.Null(Only(queue, "alice").Fingerprint);
        Assert.Null(Only(queue, "bob").Fingerprint);
        Assert.Empty(queue.AwaitingSubmission());
    }

    [Fact]
    public void DeferLookup_BacksOffAndDueForLookupWaitsForIt()
    {
        var queue = new NoticeQueue();
        queue.AddReview("alice", "/music/a.flac", Song(), Verdict());
        var key = NoticeQueue.ReviewKey("alice", "/music/a.flac");
        var now = DateTime.UtcNow.AddSeconds(1);

        Assert.Single(queue.DueForLookup(now, 10));
        queue.DeferLookup(key, now);
        Assert.Empty(queue.DueForLookup(now, 10));
        Assert.Single(queue.DueForLookup(now.AddMinutes(1), 10));

        queue.DeferLookup(key, now);
        Assert.Empty(queue.DueForLookup(now.AddMinutes(1), 10));
        Assert.Single(queue.DueForLookup(now.AddMinutes(2), 10));

        queue.SetNavidromeId(key, "nd1");
        Assert.Empty(queue.DueForLookup(now.AddDays(1), 10));
    }

    /// <summary>The file is bounded, but an open question is never the thing forgotten.</summary>
    [Fact]
    public void Trim_DropsTheOldestSettledEntries_NeverAnOpenOne()
    {
        var queue = new NoticeQueue();
        for (var i = 0; i < NoticeQueue.MaxEntries; i++)
        {
            queue.AddReview("alice", $"/music/{i}.flac", Song(), Verdict());
            if (i >= 10) queue.Resolve(NoticeQueue.ReviewKey("alice", $"/music/{i}.flac"), NoticeState.Dismissed);
        }

        queue.AddReview("alice", "/music/new.flac", Song(), Verdict());

        var entries = queue.ForUser("alice", NoticeKind.Review);
        Assert.Equal(NoticeQueue.MaxEntries, entries.Count);
        Assert.Equal(11, entries.Count(entry => entry.IsOpen));
        Assert.DoesNotContain(entries, entry => entry.LocalPath == "/music/10.flac");
    }

    [Fact]
    public void Entries_SurviveARestart()
    {
        var path = Path.Combine(_dir, "notice-queue.json");
        using (var queue = new NoticeQueue(path))
        {
            Asked(queue, "alice", "/music/a.flac", "nd1");
            Assert.True(queue.Flush());
        }

        using var reopened = new NoticeQueue(path);
        var entry = Only(reopened, "alice");
        Assert.Equal(NoticeState.Queued, entry.State);
        Assert.Equal("nd1", entry.NavidromeId);
        Assert.Equal(InconclusiveReason.NoEntry, entry.Cause);
        Assert.True(reopened.IsQueued("alice", "nd1"));
    }

    /// <summary>The file holds what people already answered, so it is set aside, never overwritten.</summary>
    [Fact]
    public void Load_ACorruptFile_IsKeptAsideAndTheQueueStartsEmpty()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "notice-queue.json");
        File.WriteAllText(path, "{ not json");

        using var queue = new NoticeQueue(path);

        Assert.Empty(queue.Recent());
        Assert.Single(Directory.GetFiles(_dir, "notice-queue.json.corrupt-*"));
    }
}

/// <summary>
/// The reconcile decision for one person's notice playlist, driven directly: what the person
/// answered by removing a track, what to take off, and what fits.
/// </summary>
public class NoticeReconcileTests
{
    private static int _order;

    private static NoticeEntry Entry(string id, NoticeState state, string? group = null, int minutesAgo = 0) => new()
    {
        Key = "k-" + id,
        Username = "alice",
        NavidromeId = id,
        State = state,
        GroupKey = group,
        Order = Interlocked.Increment(ref _order),
        CreatedUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    private static HashSet<string> Present(params string[] ids) => new(ids, StringComparer.Ordinal);

    [Fact]
    public void Plan_AQueuedTrackGoneFromThePlaylist_IsADismissal()
    {
        var plan = NoticeReconcile.Plan([Entry("a", NoticeState.Queued), Entry("b", NoticeState.Queued)], Present("b"), 10);

        Assert.Equal(["k-a"], plan.Dismiss);
        Assert.Empty(plan.Remove);
        Assert.Empty(plan.Add);
    }

    /// <summary>A restart between adding a track and recording it must not add it twice.</summary>
    [Fact]
    public void Plan_AWaitingTrackAlreadyInThePlaylist_IsAdoptedNotAddedAgain()
    {
        var plan = NoticeReconcile.Plan([Entry("a", NoticeState.Waiting)], Present("a"), 10);

        Assert.Equal(["k-a"], plan.Adopt);
        Assert.Empty(plan.Add);
    }

    [Theory]
    [InlineData(NoticeState.Kept)]
    [InlineData(NoticeState.Acted)]
    [InlineData(NoticeState.Expired)]
    [InlineData(NoticeState.Dismissed)]
    public void Plan_ASettledTrackStillListed_IsTakenOff(NoticeState state)
    {
        var plan = NoticeReconcile.Plan([Entry("a", state)], Present("a"), 10);

        Assert.Equal(["a"], plan.Remove);
        Assert.Empty(plan.Dismiss);
    }

    [Fact]
    public void Plan_FillsOnlyTheRoomLeft_OldestFirst()
    {
        var entries = new[]
        {
            Entry("asked", NoticeState.Queued),
            Entry("new", NoticeState.Waiting, minutesAgo: 1),
            Entry("old", NoticeState.Waiting, minutesAgo: 30),
            Entry("older", NoticeState.Waiting, minutesAgo: 60),
        };

        var plan = NoticeReconcile.Plan(entries, Present("asked"), 3);

        Assert.Equal(["older", "old"], plan.Add.Select(entry => entry.NavidromeId));
    }

    [Fact]
    public void Plan_AFullPlaylist_AddsNothing()
    {
        var plan = NoticeReconcile.Plan([Entry("asked", NoticeState.Queued), Entry("new", NoticeState.Waiting)], Present("asked"), 1);

        Assert.Empty(plan.Add);
    }

    [Fact]
    public void Plan_AWaitingTrackWithoutANavidromeId_Waits()
    {
        var entry = Entry("a", NoticeState.Waiting) with { NavidromeId = null };

        Assert.Empty(NoticeReconcile.Plan([entry], Present(), 10).Add);
    }

    /// <summary>A duplicate pair only makes sense together: it goes in whole or waits.</summary>
    [Fact]
    public void Plan_AGroupThatDoesNotFit_WaitsWhileASingleTrackTakesTheRoom()
    {
        var entries = new[]
        {
            Entry("a1", NoticeState.Waiting, group: "g", minutesAgo: 60),
            Entry("a2", NoticeState.Waiting, group: "g", minutesAgo: 60),
            Entry("single", NoticeState.Waiting, minutesAgo: 1),
        };

        var plan = NoticeReconcile.Plan(entries, Present(), 1);

        Assert.Equal(["single"], plan.Add.Select(entry => entry.NavidromeId));
    }

    [Fact]
    public void Plan_AGroupThatFits_GoesInWholeInItsOrder()
    {
        var entries = new[]
        {
            Entry("a1", NoticeState.Waiting, group: "g"),
            Entry("a2", NoticeState.Waiting, group: "g"),
        };

        var plan = NoticeReconcile.Plan(entries, Present(), 2);

        Assert.Equal(["a1", "a2"], plan.Add.Select(entry => entry.NavidromeId));
    }
}
