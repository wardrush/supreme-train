using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using YoutubeExplode.Exceptions;

namespace YtDownloader.Tests;

public class YoutubeUrlValidatorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&list=PL123&t=42s")]
    [InlineData("http://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ")]
    public void Accepts_youtube_video_urls(string url)
    {
        Assert.True(YoutubeUrlValidator.TryGetVideoId(url, out var id));
        Assert.Equal("dQw4w9WgXcQ", id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dQw4w9WgXcQ")] // bare ID: not a URL
    [InlineData("https://evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("ftp://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("javascript:alert(1)//youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://user:pw@youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com:8443/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/playlist?list=PLa1F2ddGya_-UvuAqHAksYnB0qL9yWDO6")]
    public void Rejects_everything_else(string? url)
    {
        Assert.False(YoutubeUrlValidator.TryGetVideoId(url, out _));
    }
}

public class DurationPolicyTests
{
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(30);

    [Fact]
    public void Allows_video_at_the_limit() => Assert.Null(DurationPolicy.Check(TimeSpan.FromMinutes(30), Max));

    [Fact]
    public void Allows_short_video() => Assert.Null(DurationPolicy.Check(TimeSpan.FromSeconds(95), Max));

    [Fact]
    public void Rejects_video_over_the_limit()
    {
        var reason = DurationPolicy.Check(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1), Max);
        Assert.NotNull(reason);
        Assert.Contains("limit", reason);
    }

    [Fact]
    public void Rejects_live_streams() => Assert.NotNull(DurationPolicy.Check(null, Max));

    [Fact]
    public void Limit_comes_from_MAX_DURATION_MINUTES()
    {
        var options = AppOptions.FromConfiguration(Config(("APP_PASSWORD", "x"), ("MAX_DURATION_MINUTES", "5")));
        Assert.Equal(TimeSpan.FromMinutes(5), options.MaxDuration);
        Assert.NotNull(DurationPolicy.Check(TimeSpan.FromMinutes(6), options.MaxDuration));
    }

    [Fact]
    public void Limit_defaults_to_30_minutes()
    {
        var options = AppOptions.FromConfiguration(Config(("APP_PASSWORD", "x")));
        Assert.Equal(TimeSpan.FromMinutes(30), options.MaxDuration);
    }

    internal static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, v.Value)))
            .Build();
}

public class AppOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_password_refuses_to_start(string? password)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppOptions.FromConfiguration(DurationPolicyTests.Config(("APP_PASSWORD", password)))
        );
        Assert.Contains("APP_PASSWORD", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void Bad_duration_refuses_to_start(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AppOptions.FromConfiguration(
                DurationPolicyTests.Config(("APP_PASSWORD", "x"), ("MAX_DURATION_MINUTES", value))
            )
        );
    }
}

public class PasswordMatchTests
{
    private static PasswordAuthMiddleware Middleware(string password) =>
        new(_ => Task.CompletedTask, new AppOptions { Password = password });

    [Fact]
    public void Matches_exact_password() => Assert.True(Middleware("correct horse").IsMatch("correct horse"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("correct hors")]
    [InlineData("correct horse ")]
    [InlineData("Correct horse")]
    public void Rejects_anything_else(string? supplied) => Assert.False(Middleware("correct horse").IsMatch(supplied));
}

public class ErrorClassifierTests
{
    [Theory]
    [InlineData("Video 'x' is unplayable. Reason: 'Sign in to confirm you're not a bot'.", "bot_check", 503)]
    [InlineData("Video 'x' is unplayable. Reason: 'Sign in to confirm your age'.", "age_restricted", 403)]
    [InlineData("Video 'x' is unplayable. Reason: 'The uploader has not made this video available in your country'.", "region_blocked", 451)]
    [InlineData("Video 'x' is unplayable. Reason: 'Something new'.", "unplayable", 422)]
    public void Unplayable_reasons_map_to_distinct_codes(string message, string code, int status)
    {
        var result = ErrorClassifier.Classify(new VideoUnplayableException(message));
        Assert.Equal(code, result.Code);
        Assert.Equal(status, result.Status);
    }

    [Fact]
    public void Unavailable_is_distinct() =>
        Assert.Equal("unavailable", ErrorClassifier.Classify(new VideoUnavailableException("gone")).Code);

    [Fact]
    public void YouTube_429_is_reported_as_blocked() =>
        Assert.Equal("blocked", ErrorClassifier.Classify(new RequestLimitExceededException("429")).Code);

    [Fact]
    public void Http_403_is_reported_as_blocked() =>
        Assert.Equal(
            "blocked",
            ErrorClassifier.Classify(new HttpRequestException("x", null, System.Net.HttpStatusCode.Forbidden)).Code
        );

    [Fact]
    public void Unknown_errors_do_not_leak_details()
    {
        var result = ErrorClassifier.Classify(new InvalidOperationException("secret internal detail"));
        Assert.Equal(500, result.Status);
        Assert.DoesNotContain("secret", result.Message);
    }

    [Fact]
    public void All_messages_are_distinct()
    {
        var messages = new Exception[]
        {
            new VideoUnavailableException("a"),
            new VideoUnplayableException("Reason: 'Sign in to confirm your age'."),
            new VideoUnplayableException("Reason: 'not available in your country'."),
            new VideoUnplayableException("Reason: 'Sign in to confirm you're not a bot'."),
        }.Select(e => ErrorClassifier.Classify(e).Message);

        Assert.Equal(4, messages.Distinct().Count());
    }
}

public class ConversionGateTests
{
    [Fact]
    public void Allows_only_one_conversion_at_a_time()
    {
        var gate = new ConversionGate();
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        gate.Exit();
        Assert.True(gate.TryEnter());
    }
}

public class TempFilesTests
{
    [Fact]
    public void Startup_sweep_and_job_delete_remove_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "ytdl-test-" + Guid.NewGuid().ToString("N"));
        var temp = new TempFiles(new AppOptions { Password = "x", TempDirectory = root }, NullLogger<TempFiles>.Instance);

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "stale.mp4"), "x");
        Directory.CreateDirectory(Path.Combine(root, "stale-dir"));
        Assert.Equal(2, temp.SweepAll());
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));

        var job = temp.NewJobPath("mp4");
        File.WriteAllText(job, "x");
        File.WriteAllText(job + ".stream-0.tmp", "x");
        var other = temp.NewJobPath("mp4");
        File.WriteAllText(other, "x");

        temp.DeleteJob(job);
        Assert.Equal([other], Directory.GetFiles(root));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Safe_file_names()
    {
        Assert.Equal("a_b_c.mp3", FileNames.Safe("a/b:c", "mp3"));
        Assert.Equal("download.mp4", FileNames.Safe("   ", "mp4"));
        Assert.Equal(104, FileNames.Safe(new string('x', 300), "mp4").Length);
    }
}
