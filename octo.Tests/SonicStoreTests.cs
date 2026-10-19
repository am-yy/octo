using Microsoft.Extensions.Logging.Abstractions;
using Octo.Services.Sonic;

namespace Octo.Tests;

/// <summary>
/// What every song sounds like, on disk: bliss's own distance, the songs nearest a seed, and how
/// well a candidate's sound fits the seed against the seed's own spread.
/// </summary>
public sealed class SonicStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-sonic-store-" + Guid.NewGuid());
    private string StatePath => Path.Combine(_directory, "sonic-features.json");

    public SonicStoreTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }

    private SonicStore Store() => new(StatePath, NullLogger<SonicStore>.Instance);

    /// <summary>23 numbers, all zero but the first, which says how far along a line the song sits.</summary>
    private static float[] At(float x)
    {
        var f = new float[23];
        f[1] = x;
        return f;
    }

    private static SonicSong Song(float[] f, int version = 2, string title = "Song") =>
        new() { Stamp = "1:1", Title = title, Artist = "Artist", Version = version, F = f };

    [Fact]
    public void Distance_IsBlissWeighted_TempoAQuarter_HarmonyThreeThirteenths()
    {
        var a = new float[23];
        var tempo = (float[])a.Clone(); tempo[0] = 1;
        var timbre = (float[])a.Clone(); timbre[1] = 1;
        var chroma = (float[])a.Clone(); chroma[12] = 1;
        Assert.Equal(Math.Sqrt(0.25), SonicStore.Distance(a, tempo), 6);
        Assert.Equal(1.0, SonicStore.Distance(a, timbre), 6);
        Assert.Equal(Math.Sqrt(3.0 / 13), SonicStore.Distance(a, chroma), 6);
        // Another version's numbers are compared unweighted.
        Assert.Equal(1.0, SonicStore.Distance(a, tempo, version: 1), 6);
    }

    [Fact]
    public void Nearest_IsClosestFirst_AndOnlySongsOfTheSameVersion()
    {
        var store = Store();
        store.Write(s =>
        {
            s.Songs["seed"] = Song(At(0));
            s.Songs["far"] = Song(At(0.9f));
            s.Songs["near"] = Song(At(0.1f));
            s.Songs["mid"] = Song(At(0.5f));
            s.Songs["old"] = Song(At(0.05f), version: 1);
        });
        Assert.Equal(["near", "mid"], store.Nearest("seed", 2).Select(item => item.Id));
        Assert.Empty(store.Nearest("unknown", 5));
    }

    [Fact]
    public void FitFactors_LeanOnTheSeedsOwnSpread()
    {
        var store = Store();
        store.Write(s =>
        {
            s.Songs["seed"] = Song(At(0));
            for (var i = 1; i <= 20; i++) s.Songs[$"s{i}"] = Song(At(i / 20f));
        });
        var fit = store.FitFactors("seed", ["s1", "s20", "s10", "nobody"]);
        Assert.Equal(SonicStore.FitClose, fit["s1"]);
        Assert.Equal(SonicStore.FitFar, fit["s20"]);
        Assert.InRange(fit["s10"], SonicStore.FitFar, SonicStore.FitClose);
        Assert.False(fit.ContainsKey("nobody"));
    }

    [Fact]
    public void FitFactors_NeedAnAnalysedSeedAndEnoughSongs()
    {
        var store = Store();
        store.Write(s =>
        {
            s.Songs["seed"] = Song(At(0));
            for (var i = 1; i <= 5; i++) s.Songs[$"s{i}"] = Song(At(i / 5f));
        });
        Assert.Empty(store.FitFactors("seed", ["s1"]));
        Assert.Empty(store.FitFactors("unanalysed", ["s1"]));
    }

    [Fact]
    public void WhatWasSaved_IsThereAfterARestart_AndABrokenFileStartsOver()
    {
        var store = Store();
        store.Write(s => { s.Songs["a"] = Song(At(0.3f), title: "Roads"); s.Pass = 2; });
        store.Flush();
        var again = Store();
        Assert.Equal("Roads", again.Read(s => s.Songs["a"].Title));
        Assert.Equal(0.3f, again.Read(s => s.Songs["a"].F[1]));
        Assert.Equal(2, again.Read(s => s.Pass));

        File.WriteAllText(StatePath, "{ not json");
        Assert.Empty(Store().Read(s => s.Songs));
    }

    [Fact]
    public void AFileWithNulls_IsReadWithoutThem()
    {
        File.WriteAllText(StatePath, """
            {"Pass":3,"Songs":{"a":{"Stamp":"1:1","Version":2,"F":[0.1]},"b":null,"c":{"Stamp":"1:1","F":null}},"Failed":{"x":null},"Paused":false}
            """);
        var store = Store();
        Assert.Equal(["a"], store.Read(s => s.Songs.Keys.ToList()));
        Assert.Empty(store.Read(s => s.Failed));
        Assert.Empty(store.Nearest("a", 5));
    }

    private SonicStore WithSounds(params (string Id, float At)[] songs)
    {
        var store = Store();
        store.Write(state => { foreach (var (id, at) in songs) state.Songs[id] = Song(At(at)); });
        return store;
    }

    [Fact]
    public void Taste_NeedsTenAnalyzedLovedSongs_AndIsTheirAverage()
    {
        var store = WithSounds(Enumerable.Range(0, 10).Select(i => ("loved-" + i, i / 10f)).ToArray());
        Assert.Null(store.Taste(Enumerable.Range(0, 9).Select(i => "loved-" + i).Append("not-analyzed")));
        var taste = store.Taste(Enumerable.Range(0, 10).Select(i => "loved-" + i))!;
        Assert.Equal(0.45, taste[1], 4);
    }

    [Fact]
    public void NearTaste_LeavesOutTheFarthestAnalyzedSongs_KeepsTheRestInOrder()
    {
        var store = WithSounds(("near", 0), ("mid", 0.4f), ("far", 0.9f), ("farther", 1f), ("x", 0.2f));
        string[] pool = ["far", "near", "new", "farther", "mid", "x"];
        var kept = store.NearTaste(pool, id => id, At(0), keepAtLeast: 0);
        // Five analyzed: the closest three stay (60%, rounded up); "new" has no sound yet, so it stays.
        Assert.Equal(["near", "new", "mid", "x"], kept);
        // Never fewer than the list needs.
        Assert.Equal(5, store.NearTaste(pool, id => id, At(0), keepAtLeast: 5).Count);
        Assert.Same(pool, store.NearTaste(pool, id => id, null, keepAtLeast: 0));
    }

    [Fact]
    public void Flow_PutsTheClosestNext_NeverTheSameArtist_AndLeavesOtherSongsInPlace()
    {
        var store = WithSounds(("a", 0), ("b", 0.9f), ("c", 0.1f), ("d", 0.5f), ("e", 0.05f));
        var artists = new Dictionary<string, string> { ["a"] = "One", ["b"] = "Two", ["c"] = "Three", ["d"] = "Four", ["e"] = "One", ["out"] = "Five" };
        string[] items = ["a", "out", "b", "c", "d", "e"];
        var flowed = store.Flow(items, id => id == "out" ? null : id, id => artists[id], window: 4);
        // After "a": "e" sounds closest but is One again, so "c". Then "e" from "c".
        Assert.Equal(["a", "out", "c", "e", "d", "b"], flowed);
        Assert.Equal(flowed, store.Flow(items, id => id == "out" ? null : id, id => artists[id], window: 4));
    }
}
