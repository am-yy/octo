using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Octo.Services.Local;
using Octo.Services.Subsonic;

namespace Octo.Tests;

public class LidarrWebhookTests
{
    private static WebApplicationFactory<Program> Factory(Mock<ILocalLibraryService> library, string? secret) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subsonic:Url"] = "http://navidrome.invalid",
                ["Library:DownloadPath"] = Path.GetTempPath(),
                ["Deezer:CacheEnabled"] = "false",
                ["Lidarr:WebhookSecret"] = secret,
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ILocalLibraryService>();
                services.AddSingleton(library.Object);
            });
        });

    private static async Task<HttpStatusCode> Send(HttpClient client, string eventType, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/lidarr/webhook")
        {
            Content = new StringContent("{\"eventType\":\"" + eventType + "\",\"album\":{\"id\":7}}",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("lidarr:" + password)));
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task WebhookIsOffWithoutASecret()
    {
        var library = new Mock<ILocalLibraryService>();
        await using var factory = Factory(library, null);

        Assert.Equal(HttpStatusCode.NotFound, await Send(factory.CreateClient(), "Download", ""));
        library.Verify(l => l.TriggerLibraryScanAsync(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task OnlyTheSecretStartsAScanAndAReconcileBurst()
    {
        var library = new Mock<ILocalLibraryService>();
        library.Setup(l => l.TriggerLibraryScanAsync(true)).ReturnsAsync(true);
        await using var factory = Factory(library, "s3cret");
        var client = factory.CreateClient();
        var reconciler = factory.Services.GetRequiredService<ExternalSaveReconciler>();

        Assert.Equal(HttpStatusCode.Unauthorized, await Send(client, "Download", "wrong"));
        Assert.Equal(HttpStatusCode.OK, await Send(client, "Test", "s3cret"));
        library.Verify(l => l.TriggerLibraryScanAsync(It.IsAny<bool>()), Times.Never);
        Assert.False(reconciler.Bursting);

        Assert.Equal(HttpStatusCode.OK, await Send(client, "Download", "s3cret"));
        library.Verify(l => l.TriggerLibraryScanAsync(true), Times.Once);
        Assert.True(reconciler.Bursting);
    }
}
