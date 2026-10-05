using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Metadata;
using Octo.Services.Trackers;

namespace Octo.Tests;

public sealed class DeezerDiscoveryTests
{
    [Theory]
    [InlineData(2, 2, false, false, true)]
    [InlineData(0, 2, false, false, false)]
    [InlineData(3, 2, false, false, false)]
    [InlineData(2, 3, false, false, false)]
    [InlineData(2, 2, true, false, false)]
    [InlineData(2, 2, false, true, false)]
    public async Task CompletenessAndRevisionSurviveCache(int declared, int total, bool duplicate, bool truncated, bool expected)
    {
        using var f = new Catalog { Declared = declared, Total = total, Duplicate = duplicate, Truncated = truncated };
        using var service = f.Service();
        var first = await service.GetAlbumDetailAsync("5"); var second = await service.GetAlbumDetailAsync("5");
        Assert.NotNull(first); Assert.Equal(expected, first.Complete); Assert.Equal(first.Complete, second!.Complete);
        Assert.Equal(first.DeclaredTrackCount, second.DeclaredTrackCount); Assert.Equal(first.ReturnedTotal, second.ReturnedTotal);
        var manifest = DeezerAlbumManifest.From(first);
        Assert.Equal(expected, manifest is not null);
        if (expected) Assert.Equal(manifest!.Revision, DeezerAlbumManifest.From(second)!.Revision);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, null)]
    [InlineData(3, 1)]
    [InlineData(1, 3)]
    public void MissingAndNoncontiguousPositionsNeverQualify(int? disc, int? track)
    {
        var album = new DeezerMetadataService.AlbumDetail("5","Crisps","Artist",null,null,null,null,
            [new("One","Artist",180,1,1,null,"42"),new("Two","Artist",180,track,disc,null,"43")],"album",2,2,true);
        Assert.Null(DeezerAlbumManifest.From(album));
    }

    [Fact]
    public async Task ParentSearchInspectsLaterCandidatesAndKeepsMeaningfulSubtitles()
    {
        using var f = new Catalog { MembershipType = "single", Parents = [5,6] };
        using var service = f.Service(); var result = await service.ResolveDiscoveryAlbumAsync(Song());
        Assert.Equal(DiscoveryResolution.Resolved,result.Status); Assert.Equal(2,result.CandidatesInspected);
        Assert.Equal("Crisps (Vol. 2)",result.Manifest!.Title); Assert.Contains("/album/6",f.Paths);
    }

    [Theory]
    [InlineData(26, false, DiscoveryResolution.Unresolved)]
    [InlineData(2, true, DiscoveryResolution.Ambiguous)]
    public async Task TruncatedAndCompetingParentsNeverResolve(int total, bool competing, DiscoveryResolution expected)
    {
        using var f = new Catalog { MembershipType = "single", Parents = [5,6], SearchTotal = total, Competing = competing };
        using var service = f.Service(); var result = await service.ResolveDiscoveryAlbumAsync(Song()); Assert.Equal(expected,result.Status);
    }

    [Fact]
    public async Task UnavailableCandidateDetailPreventsResolutionDespiteKnownMatch()
    {
        using var f = new Catalog { MembershipType = "single", Parents = [5,6], MissingDetail = 5 };
        using var service = f.Service(); var result = await service.ResolveDiscoveryAlbumAsync(Song());
        Assert.Equal(DiscoveryResolution.Unresolved,result.Status); Assert.False(result.SearchComplete);
    }

    [Fact]
    public void IsrcDoesNotOverrideIncompatibleVersionOrDuration()
    {
        var song = Song();
        Assert.False(DeezerMetadataService.DiscoveryRecordingMatches(song,new(song.Title+" (Live)","Artist",180,1,1,song.Isrc,"42")));
        Assert.False(DeezerMetadataService.DiscoveryRecordingMatches(song,new(song.Title,"Artist",240,1,1,song.Isrc,"42")));
        song.Isrc=null; song.Duration=null;
        Assert.False(DeezerMetadataService.DiscoveryRecordingMatches(song,new(song.Title,"Artist",180,1,1,null,"42")));
    }

    private static Song Song() => new() {Artist="Artist",Album="Get Back Jamie",Title="Get Back Jamie",Duration=180,Isrc="GBAAA2400001",DeezerId="42"};
    private sealed class Catalog : HttpMessageHandler,IHttpClientFactory
    {
        public int Declared {get;init;}=2;
        public int Total {get;init;}=2;
        public bool Duplicate {get;init;}
        public bool Truncated {get;init;}
        public string MembershipType {get;init;}="album";
        public int[] Parents {get;init;}=[5];
        public int? SearchTotal {get;init;}
        public bool Competing {get;init;}
        public int? MissingDetail {get;init;}
        public List<string> Paths {get;}=[];
        public DeezerMetadataService Service()=>new(this,TestOptions.Monitor(new MetadataSettings()),NullLogger<DeezerMetadataService>.Instance);
        public HttpClient CreateClient(string name)=>new(this,false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath; Paths.Add(path);
            object body;
            if(path=="/track/42") body=new{id=42,title="Get Back Jamie",duration=180,isrc="GBAAA2400001",artist=new{name="Artist"},album=new{id=4}};
            else if(path=="/search/album") body=new{total=SearchTotal??Parents.Length,data=Parents.Select(id=>new{id,title="Candidate",artist=new{name="Artist"}})};
            else
            {
                var id=int.Parse(request.RequestUri.Segments[2].TrimEnd('/'));
                if(id==MissingDetail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if(path.EndsWith("/tracks")) body=new {total=Total,next=Truncated?"https://api.deezer.com/next":null,data=new[]{
                    new{id=42,title=id==5&&MembershipType=="single"&&!Competing?"Different":"Get Back Jamie",duration=180,isrc=id==5&&MembershipType=="single"&&!Competing?"GBAAA2400099":"GBAAA2400001",track_position=1,disk_number=1,artist=new{name="Artist"}},
                    new{id=Duplicate?42:43,title="Two",duration=181,isrc="GBAAA2400002",track_position=2,disk_number=1,artist=new{name="Artist"}}}};
                else body=new{id,title=id==6?"Crisps (Vol. 2)":"Crisps",nb_tracks=Declared,record_type=id==4?MembershipType:"album",artist=new{name="Artist"}};
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(body))});
        }
    }
}
