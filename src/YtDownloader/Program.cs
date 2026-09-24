using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using YoutubeExplode;
using YtDownloader;

var builder = WebApplication.CreateBuilder(args);

// Missing APP_PASSWORD (or a malformed limit) stops the process before it listens.
AppOptions options;
try
{
    options = AppOptions.FromConfiguration(builder.Configuration);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Startup refused: {ex.Message}");
    return 1;
}

// Railway injects PORT; fall back to the image default (8080) when running elsewhere.
var port = builder.Configuration["PORT"];
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<YoutubeClient>();
builder.Services.AddSingleton<TempFiles>();
builder.Services.AddSingleton<ConversionGate>();
builder.Services.AddSingleton<ConversionService>();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Railway's edge proxy sits in front of the container. ForwardLimit = 1 takes only the
    // right-most X-Forwarded-For entry (the one the edge appended), so clients cannot spoof it.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests from this IP. Wait a few minutes and try again." },
            ct
        );

    // Applies to every /api request, including failed password attempts, so the
    // limiter also slows password guessing.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        PasswordAuthMiddleware.RequiresAuth(ctx.Request.Path)
            ? RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RateLimitPermits,
                    Window = options.RateLimitWindow,
                    QueueLimit = 0,
                }
            )
            : RateLimitPartition.GetNoLimiter("unlimited")
    );
});

var app = builder.Build();

app.Services.GetRequiredService<TempFiles>().SweepAll();

app.UseForwardedHeaders();

app.Use(
    async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Robots-Tag"] = "noindex, nofollow";
        h["Content-Security-Policy"] =
            "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
        await next();
    }
);

app.UseRateLimiter();
app.UseMiddleware<PasswordAuthMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapPost(
    "/api/convert",
    async (
        ConvertRequest body,
        HttpContext ctx,
        ConversionGate gate,
        ConversionService converter,
        TempFiles tempFiles,
        ILogger<Program> logger
    ) =>
    {
        if (!YoutubeUrlValidator.TryGetVideoId(body.Url, out var videoId))
            return Error(StatusCodes.Status400BadRequest, "invalid_url", "That is not a YouTube video URL.");

        var format = body.Format?.Trim().ToLowerInvariant() switch
        {
            "mp4" => OutputFormat.Mp4,
            "mp3" => OutputFormat.Mp3,
            _ => (OutputFormat?)null,
        };
        if (format is null)
            return Error(StatusCodes.Status400BadRequest, "invalid_format", "Format must be mp4 or mp3.");

        if (!gate.TryEnter())
            return Error(
                StatusCodes.Status429TooManyRequests,
                "busy",
                "Another conversion is running. Try again when it finishes."
            );

        ConversionResult result;
        try
        {
            result = await converter.ConvertAsync(videoId, format.Value, ctx.RequestAborted);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Client disconnected during conversion of {VideoId}", videoId.Value);
            return Results.Empty;
        }
        catch (PolicyRejectedException ex)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "rejected", ex.Message);
        }
        catch (Exception ex)
        {
            var error = ErrorClassifier.Classify(ex);
            logger.LogWarning(ex, "Conversion of {VideoId} failed as {Code}", videoId.Value, error.Code);
            return Error(error.Status, error.Code, error.Message);
        }
        finally
        {
            gate.Exit();
        }

        // DeleteOnClose removes the file when the response finishes streaming (or aborts);
        // OnCompleted is a backstop in case the stream is never opened by the result.
        ctx.Response.OnCompleted(() =>
        {
            tempFiles.DeleteJob(result.FilePath);
            return Task.CompletedTask;
        });

        var stream = new FileStream(
            result.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
        );

        return Results.File(stream, result.ContentType, result.DownloadName);
    }
);

app.Run();
return 0;

static IResult Error(int status, string code, string message) =>
    Results.Json(new { error = message, code }, statusCode: status);

public sealed record ConvertRequest(string? Url, string? Format);

public partial class Program;
