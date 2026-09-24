using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace YtDownloader.Tests;

public class TestApp : WebApplicationFactory<Program>
{
    public const string Password = "test-password-123";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ytdl-test-" + Guid.NewGuid().ToString("N"));
    protected virtual int Permits => 1000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("APP_PASSWORD", Password);
        builder.UseSetting("TEMP_DIR", _tempDir);
        builder.UseSetting("RATE_LIMIT_PERMITS", Permits.ToString());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}

public sealed class StrictTestApp : TestApp
{
    protected override int Permits => 3;
}

public class HttpTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;

    public HttpTests(TestApp app) => _app = app;

    private static HttpRequestMessage Convert(string url, string format = "mp4", string? password = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/convert")
        {
            Content = JsonContent.Create(new { url, format }),
        };
        if (password is not null)
            request.Headers.Add(PasswordAuthMiddleware.HeaderName, password);
        return request;
    }

    [Fact]
    public async Task Healthz_needs_no_auth()
    {
        var res = await _app.CreateClient().GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Page_is_served_without_auth()
    {
        var res = await _app.CreateClient().GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("X-App-Password", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Api_without_password_is_401()
    {
        var res = await _app.CreateClient().SendAsync(Convert("https://youtu.be/dQw4w9WgXcQ"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Api_with_wrong_password_is_401()
    {
        var res = await _app.CreateClient()
            .SendAsync(Convert("https://youtu.be/dQw4w9WgXcQ", password: "nope"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_path_is_401_not_404_without_password()
    {
        var res = await _app.CreateClient().GetAsync("/api/anything", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Correct_password_passes_auth_and_bad_url_is_rejected_before_youtube()
    {
        var res = await _app.CreateClient()
            .SendAsync(Convert("https://evil.example/watch?v=dQw4w9WgXcQ", password: TestApp.Password), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("invalid_url", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cookie_auth_is_accepted()
    {
        var request = Convert("not a url");
        request.Headers.Add("Cookie", $"{PasswordAuthMiddleware.CookieName}={TestApp.Password}");
        var res = await _app.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Bad_format_is_rejected()
    {
        var res = await _app.CreateClient()
            .SendAsync(Convert("https://youtu.be/dQw4w9WgXcQ", "wav", TestApp.Password), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("invalid_format", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

public class RateLimitTests
{
    [Fact]
    public async Task Api_is_rate_limited_per_ip_including_failed_logins()
    {
        using var app = new StrictTestApp();
        var client = app.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var ok = await client.GetAsync("/api/convert", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);
        }

        var limited = await client.GetAsync("/api/convert", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        var health = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
