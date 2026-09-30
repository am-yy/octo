using Octo.Services.Soulseek;

namespace Octo.Tests;

public class ExternalIdRegistryTests
{
    private readonly ExternalIdRegistry _registry;

    public ExternalIdRegistryTests()
    {
        _registry = new ExternalIdRegistry();
    }

    [Fact]
    public void Register_SameRouting_ProducesSameId()
    {
        // Arrange
        var a = new SoulseekRouting { Kind = RoutingKind.Song, Artist = "Radiohead", Title = "Nude", Duration = 255 };
        var b = new SoulseekRouting { Kind = RoutingKind.Song, Artist = "Radiohead", Title = "Nude", Duration = 255 };

        // Act
        var idA = _registry.Register(a);
        var idB = _registry.Register(b);

        // Assert
        Assert.Equal(idA, idB);
    }

    [Fact]
    public void Register_DifferentKindsSameNames_ProduceDifferentIds()
    {
        // The Kind prefix keeps a song id distinct from its album and artist ids,
        // otherwise getCoverArt would return the wrong scope's artwork.
        var songId = _registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song, Artist = "Radiohead", Title = "In Rainbows"
        });
        var albumId = _registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Album, Artist = "Radiohead", Album = "In Rainbows"
        });
        var artistId = _registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Artist, Artist = "Radiohead"
        });

        Assert.NotEqual(songId, albumId);
        Assert.NotEqual(albumId, artistId);
        Assert.NotEqual(songId, artistId);
    }

    [Fact]
    public void Register_AlbumWithoutDeezerId_DoesNotClobberKnownExternalAlbumId()
    {
        // Arrange: an album search registers the precise Deezer id.
        var fromSearch = new SoulseekRouting
        {
            Kind = RoutingKind.Album,
            Artist = "Radiohead",
            Album = "In Rainbows",
            ExternalAlbumId = "14880659",
        };
        var id = _registry.Register(fromSearch);

        // Act: a song row later mints the same artist+album with no Deezer id. It hashes
        // to the same key, so a naive overwrite would drop the id we already resolved.
        var fromSongRow = new SoulseekRouting
        {
            Kind = RoutingKind.Album,
            Artist = "Radiohead",
            Album = "In Rainbows",
        };
        var sameId = _registry.Register(fromSongRow);

        // Assert
        Assert.Equal(id, sameId);
        Assert.Equal("14880659", _registry.Lookup(id)!.ExternalAlbumId);
    }

    [Fact]
    public void Register_AlbumWithDeezerId_OverwritesAnEarlierUnknownId()
    {
        // The preserve must only fill blanks, never block a real update.
        var id = _registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Album, Artist = "Radiohead", Album = "In Rainbows"
        });
        Assert.Null(_registry.Lookup(id)!.ExternalAlbumId);

        _registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Album,
            Artist = "Radiohead",
            Album = "In Rainbows",
            ExternalAlbumId = "14880659",
        });

        Assert.Equal("14880659", _registry.Lookup(id)!.ExternalAlbumId);
    }

    [Fact]
    public void Lookup_UnknownId_ReturnsNull()
    {
        Assert.Null(_registry.Lookup("nonexistent"));
    }
    // ---- Persistence --------------------------------------------------------
    // The registry is the only thing that knows an id is ours, so losing it on restart
    // made every id a client still held look local. Those were relayed to Navidrome,
    // which answers error 70 "data not found" for media it does not have, and the client
    // showed that on every play and every poll.

    [Fact]
    public void Register_SurvivesARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"octo-ids-{Guid.NewGuid():N}.json");
        try
        {
            string id;
            using (var first = new ExternalIdRegistry(path))
            {
                id = first.Register(new SoulseekRouting
                {
                    Artist = "Radiohead", Title = "Reckoner", Duration = 290,
                });
            }

            using var reopened = new ExternalIdRegistry(path);
            var routing = reopened.Lookup(id);

            Assert.NotNull(routing);
            Assert.Equal("Radiohead", routing!.Artist);
            Assert.Equal("Reckoner", routing.Title);
            Assert.Equal(290, routing.Duration);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Register_WithNoPath_KeepsWorkingInMemory()
    {
        // The parameterless form is still a valid registry, just not a durable one.
        using var registry = new ExternalIdRegistry();
        var id = registry.Register(new SoulseekRouting { Artist = "A", Title = "B" });
        Assert.NotNull(registry.Lookup(id));
    }

    [Fact]
    public void Load_UnreadableFile_StartsEmptyRatherThanThrowing()
    {
        // A registry that will not parse is a cold start, not a failure to boot.
        var path = Path.Combine(Path.GetTempPath(), $"octo-ids-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not the file you are looking for");
        try
        {
            using var registry = new ExternalIdRegistry(path);
            Assert.Null(registry.Lookup("anything"));

            // ...and it must still be usable afterwards.
            var id = registry.Register(new SoulseekRouting { Artist = "A", Title = "B" });
            Assert.NotNull(registry.Lookup(id));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Register_ArtistByNameAlone_KeepsTheCatalogArtistAlreadyChosen()
    {
        // Two artists can share a name, and the page settled on one of them. Every album row
        // mints its artist again by name alone, which must not undo that choice.
        var id = _registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Artist, Artist = "Nirvana", ExternalArtistId = "415",
        });

        _registry.Register(new SoulseekRouting { Kind = RoutingKind.Artist, Artist = "Nirvana" });

        Assert.Equal("415", _registry.Lookup(id)!.ExternalArtistId);
    }
}
