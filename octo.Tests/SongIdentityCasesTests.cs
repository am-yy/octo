using System.Text.Json;
using Octo.Services.Common;

namespace Octo.Tests;

/// <summary>
/// Every case in docs/song-identity-cases.json, the file the Octo app runs too. A failure names
/// the case by its note, so a rule changed on one side shows up here before the two disagree.
/// </summary>
public class SongIdentityCasesTests
{
    private static readonly JsonElement Cases = Load();

    private static JsonElement Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "song-identity-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static IEnumerable<object[]> Compare() =>
        Cases.GetProperty("compare").EnumerateArray().Select((item, index) => new object[] { index, item.GetProperty("note").GetString()! });

    public static IEnumerable<object[]> Parse() =>
        Cases.GetProperty("parse").EnumerateArray().Select((item, index) => new object[] { index, item.GetProperty("note").GetString()! });

    public static IEnumerable<object[]> Queries() =>
        Cases.GetProperty("queries").EnumerateArray().Select((item, index) => new object[] { index, item.GetProperty("note").GetString()! });

    [Fact]
    public void TheFileHasEnoughCases()
    {
        Assert.True(Cases.GetProperty("compare").GetArrayLength() >= 120);
        Assert.True(Cases.GetProperty("parse").GetArrayLength() >= 30);
    }

    [Theory]
    [MemberData(nameof(Compare))]
    public void CompareCase(int index, string note)
    {
        var item = Cases.GetProperty("compare")[index];
        var a = Ref(item.GetProperty("a"));
        var b = Ref(item.GetProperty("b"));
        var options = Options(item);
        var expect = item.GetProperty("expect").GetString() switch
        {
            "same" => SongVerdict.Same,
            "version" => SongVerdict.SameSongDifferentVersion,
            "different" => SongVerdict.Different,
            var other => throw new InvalidOperationException($"unknown expectation '{other}'"),
        };

        var forward = SongIdentity.Same(a, b, options);
        var backward = SongIdentity.Same(b, a, options);
        Assert.True(expect == forward.Verdict, $"{note} expected {expect}, got {forward.Verdict} ({forward.Reason})");
        // A comparison reads the same from either side.
        Assert.True(expect == backward.Verdict, $"{note} (reversed) expected {expect}, got {backward.Verdict} ({backward.Reason})");
        Assert.InRange(forward.Confidence, 0, 1);
        Assert.False(string.IsNullOrWhiteSpace(forward.Reason));
    }

    [Theory]
    [MemberData(nameof(Parse))]
    public void ParseCase(int index, string note)
    {
        var item = Cases.GetProperty("parse")[index];
        var input = item.GetProperty("input");
        var title = input.GetProperty("title").GetString();
        var artist = input.GetProperty("artist").GetString();

        var parsed = SongIdentity.ParseTitle(title, artist);
        Assert.True(item.GetProperty("expectTitleKey").GetString() == parsed.Key, $"{note}: title key '{parsed.Key}'");
        Assert.True(item.GetProperty("expectLooseKey").GetString() == parsed.LooseKey, $"{note}: loose key '{parsed.LooseKey}'");
        Assert.Equal(Strings(item.GetProperty("expectVersions")), parsed.Versions);

        var credit = SongIdentity.ParseArtists(string.IsNullOrWhiteSpace(artist) ? parsed.ArtistFromTitle : artist);
        var artists = credit.Names.Concat(credit.Featured).Concat(parsed.Featured)
            .Select(SongIdentity.Key).Distinct().ToList();
        Assert.True(Strings(item.GetProperty("expectArtists")).SequenceEqual(artists),
            $"{note}: artists [{string.Join(", ", artists)}]");
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void QueryCase(int index, string note)
    {
        var item = Cases.GetProperty("queries")[index];
        var input = item.GetProperty("input");
        var queries = SongIdentity.QueryVariants(input.GetProperty("title").GetString(), input.GetProperty("artist").GetString())
            .Select(query => query.Text).ToList();
        Assert.True(Strings(item.GetProperty("expect")).SequenceEqual(queries), $"{note}: [{string.Join(" | ", queries)}]");
    }

    private static SongRef Ref(JsonElement side) => new(
        side.GetProperty("title").GetString(), side.GetProperty("artist").GetString(),
        side.TryGetProperty("seconds", out var seconds) && seconds.ValueKind == JsonValueKind.Number ? seconds.GetDouble() : null)
    {
        // One ISRC as a string, or several as a list. Absent in every case written before it.
        Isrcs = side.TryGetProperty("isrc", out var isrc)
            ? isrc.ValueKind == JsonValueKind.Array ? Strings(isrc) : [isrc.GetString()!]
            : [],
    };

    private static SongMatchOptions Options(JsonElement item)
    {
        if (!item.TryGetProperty("options", out var options)) return SongMatchOptions.Default;
        var result = SongMatchOptions.Default;
        if (options.TryGetProperty("lengthToleranceSeconds", out var tolerance))
            result = result with { LengthToleranceSeconds = tolerance.ValueKind == JsonValueKind.Number ? tolerance.GetInt32() : null };
        if (options.TryGetProperty("extrasMustAgree", out var extras))
            result = result with { ExtrasMustAgree = extras.GetBoolean() };
        if (options.TryGetProperty("alsoNeutral", out var neutral))
            result = result with { AlsoNeutral = Strings(neutral) };
        return result;
    }

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(value => value.GetString()!).ToList();
}
