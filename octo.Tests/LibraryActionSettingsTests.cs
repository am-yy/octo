using Octo.Models.Settings;

namespace Octo.Tests;

/// <summary>
/// Every part of library actions is configurable, which means the sanitising has to be too.
/// The defaults are the cautious reading of every choice, because the feature removes files
/// Octo did not create.
/// </summary>
public class LibraryActionSettingsTests
{
    [Fact]
    public void Defaults_AreTheCautiousReadingOfEveryChoice()
    {
        var settings = new LibraryActionSettings();

        Assert.False(settings.Enabled);
        Assert.False(settings.RatingsEnabled);
        Assert.True(settings.DryRun);
        Assert.Empty(settings.AllowedUsers);
    }

    /// <summary>
    /// Fail-open on a feature that removes files is not a defensible default, so an empty
    /// allowlist has to mean nobody rather than everybody.
    /// </summary>
    [Fact]
    public void IsAllowed_EmptyList_IsFalseForEveryone()
    {
        var settings = new LibraryActionSettings();

        Assert.False(settings.IsAllowed("alice"));
        Assert.False(settings.IsAllowed(""));
        Assert.False(settings.IsAllowed(null));
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData("ALICE", true)]
    [InlineData(" alice ", true)]
    [InlineData("bob", false)]
    public void IsAllowed_MatchesNavidromeUsernamesCaseInsensitively(string username, bool expected)
        => Assert.Equal(expected,
            new LibraryActionSettings { AllowedUsers = ["Alice"] }.IsAllowed(username));

    /// <summary>
    /// Disabling one action must not delete the names chosen for the others, and a config
    /// naming only some of them must not leave the rest undefined.
    /// </summary>
    [Fact]
    public void EffectiveActions_BackFillsEveryActionAndKeepsConfiguredNames()
    {
        var settings = new LibraryActionSettings
        {
            Actions = [new() { Action = LibraryAction.Delete, Name = "Bin it", Enabled = true, Rating = 1 }],
        };

        var actions = settings.EffectiveActions();

        Assert.Equal(5, actions.Count);
        var delete = actions.Single(action => action.Action == LibraryAction.Delete);
        Assert.Equal("Bin it", delete.Name);
        Assert.True(delete.Enabled);
        // The rest come back with their defaults, disabled.
        Assert.All(actions.Where(action => action.Action != LibraryAction.Delete),
            action => Assert.False(action.Enabled));
    }

    /// <summary>
    /// Two playlists with the same effective title are indistinguishable to the sweep, and one
    /// of them would apply the wrong action.
    /// </summary>
    [Fact]
    public void EffectiveActions_DropsADuplicateEffectiveTitle()
    {
        var settings = new LibraryActionSettings
        {
            Actions =
            [
                new() { Action = LibraryAction.Delete, Name = "Fix it" },
                new() { Action = LibraryAction.WrongSong, Name = "Fix it" },
            ],
        };

        Assert.Equal(4, settings.EffectiveActions().Count);
    }

    [Fact]
    public void EffectiveActions_BlankOrOverlongName_FallsBackToTheBuiltIn()
    {
        var settings = new LibraryActionSettings
        {
            Actions =
            [
                new() { Action = LibraryAction.Delete, Name = "   " },
                new() { Action = LibraryAction.WrongSong, Name = new string('x', 200) },
            ],
        };

        var actions = settings.EffectiveActions();
        Assert.Equal("Delete", actions.Single(a => a.Action == LibraryAction.Delete).Name);
        Assert.Equal("Wrong song", actions.Single(a => a.Action == LibraryAction.WrongSong).Name);
    }

    /// <summary>A rating can only mean one thing, so a collision unmaps the later one.</summary>
    [Fact]
    public void EffectiveActions_TwoActionsOnTheSameRating_UnmapsTheSecond()
    {
        var settings = new LibraryActionSettings
        {
            Actions =
            [
                new() { Action = LibraryAction.Delete, Name = "Delete", Enabled = true, Rating = 1 },
                new() { Action = LibraryAction.WrongSong, Name = "Wrong song", Enabled = true, Rating = 1 },
            ],
        };

        var actions = settings.EffectiveActions();
        Assert.Equal(1, actions.Single(a => a.Action == LibraryAction.Delete).Rating);
        Assert.Equal(0, actions.Single(a => a.Action == LibraryAction.WrongSong).Rating);
    }

    /// <summary>
    /// Five stars is Keep by default, which removes nothing, so the top of the scale is never
    /// destructive and an enthusiastic rating cannot remove a file.
    /// </summary>
    [Fact]
    public void ActionForRating_FiveStarsMeansKeep()
    {
        var settings = new LibraryActionSettings
        {
            Actions = Enum.GetValues<LibraryAction>()
                .Select(action => new LibraryActionDefinition { Action = action, Enabled = true })
                .ToList(),
        };

        Assert.Equal(LibraryAction.Keep, settings.ActionForRating(5)?.Action);
        Assert.Equal(LibraryAction.Delete, settings.ActionForRating(1)?.Action);
    }

    /// <summary>
    /// Keep only answers Octo's questions, so it comes on by itself with Review. A choice the
    /// operator made either way still wins.
    /// </summary>
    [Fact]
    public void EffectiveActions_Keep_ComesOnWithReviewUnlessConfigured()
    {
        static bool KeepEnabled(LibraryActionSettings settings) =>
            settings.EffectiveActions().Single(action => action.Action == LibraryAction.Keep).Enabled;

        Assert.False(KeepEnabled(new LibraryActionSettings()));
        Assert.True(KeepEnabled(new LibraryActionSettings { ReviewEnabled = true }));
        Assert.False(KeepEnabled(new LibraryActionSettings
        {
            ReviewEnabled = true,
            Actions = [new() { Action = LibraryAction.Keep, Enabled = false }],
        }));
        Assert.True(KeepEnabled(new LibraryActionSettings
        {
            Actions = [new() { Action = LibraryAction.Keep, Enabled = true }],
        }));
    }

    /// <summary>
    /// Auto keeps the behaviour ratings had before the setting existed until Review is on, and
    /// then a star only counts on a track Octo asked about.
    /// </summary>
    [Fact]
    public void EffectiveRatingsScope_Auto_FollowsNotices()
    {
        Assert.Equal(LibraryRatingScope.Global, new LibraryActionSettings().EffectiveRatingsScope);
        Assert.Equal(LibraryRatingScope.NoticeOnly,
            new LibraryActionSettings { ReviewEnabled = true }.EffectiveRatingsScope);
    }

    [Theory]
    [InlineData(LibraryRatingScope.Global, true, LibraryRatingScope.Global)]
    [InlineData(LibraryRatingScope.NoticeOnly, false, LibraryRatingScope.NoticeOnly)]
    public void EffectiveRatingsScope_Explicit_Wins(LibraryRatingScope configured, bool review, LibraryRatingScope expected)
        => Assert.Equal(expected,
            new LibraryActionSettings { RatingsScope = configured, ReviewEnabled = review }.EffectiveRatingsScope);

    [Fact]
    public void NoticeTitle_UsesTheNoticePrefixAndFallsBackOnABlankName()
    {
        Assert.Equal("▸ Review", new LibraryActionSettings().NoticeTitle(NoticeKind.Review));
        Assert.Equal("? To check", new LibraryActionSettings { NoticePrefix = "? ", ReviewPlaylistName = " To check " }
            .NoticeTitle(NoticeKind.Review));
        Assert.Equal("Review", new LibraryActionSettings { NoticePrefix = "", ReviewPlaylistName = "  " }
            .NoticeTitle(NoticeKind.Review));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 100)]
    [InlineData(100000, 500)]
    public void EffectiveNoticeMaxTracks_IsClamped(int configured, int expected)
        => Assert.Equal(expected, new LibraryActionSettings { NoticeMaxTracks = configured }.EffectiveNoticeMaxTracks);

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void ActionForRating_OutOfRange_IsNothing(int rating)
        => Assert.Null(new LibraryActionSettings().ActionForRating(rating));

    [Fact]
    public void ActionForRating_DisabledAction_IsNotTriggered()
    {
        var settings = new LibraryActionSettings
        {
            Actions = [new() { Action = LibraryAction.Delete, Enabled = false, Rating = 1 }],
        };

        Assert.Null(settings.ActionForRating(1));
    }

    /// <summary>A directory traversal must not be typeable into a settings field.</summary>
    [Theory]
    [InlineData("../../etc", ".octo-trash")]
    [InlineData("..", ".octo-trash")]
    [InlineData(".", ".octo-trash")]
    [InlineData("", ".octo-trash")]
    [InlineData("trash/nested", "trash")]
    [InlineData("/.octo-trash/", ".octo-trash")]
    [InlineData("my-bin", "my-bin")]
    public void EffectiveQuarantineDirectory_IsOneSafeSegment(string configured, string expected)
        => Assert.Equal(expected,
            new LibraryActionSettings { QuarantineDirectory = configured }.EffectiveQuarantineDirectory);

    /// <summary>0 is a real choice meaning "never sweep", so it is not clamped upward.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(30, 30)]
    [InlineData(99999, 3650)]
    public void EffectiveQuarantineRetentionDays_TreatsZeroAsForever(int configured, int expected)
        => Assert.Equal(expected,
            new LibraryActionSettings { QuarantineRetentionDays = configured }.EffectiveQuarantineRetentionDays);

    [Theory]
    [InlineData(1, 15)]
    [InlineData(60, 60)]
    [InlineData(99999, 3600)]
    public void EffectivePollInterval_IsClamped(int configured, int expectedSeconds)
        => Assert.Equal(expectedSeconds,
            (int)new LibraryActionSettings { PollIntervalSeconds = configured }
                .EffectivePollInterval.TotalSeconds);

    [Fact]
    public void PlaylistTitle_UsesTheConfiguredPrefixAndToleratesNone()
    {
        var withPrefix = new LibraryActionSettings { PlaylistPrefix = ">> " };
        var definition = withPrefix.EffectiveActions().First(a => a.Action == LibraryAction.Delete);
        Assert.Equal(">> Delete", withPrefix.PlaylistTitle(definition));

        var none = new LibraryActionSettings { PlaylistPrefix = "" };
        Assert.Equal("Delete", none.PlaylistTitle(
            none.EffectiveActions().First(a => a.Action == LibraryAction.Delete)));
    }
}
