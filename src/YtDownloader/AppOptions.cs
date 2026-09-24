namespace YtDownloader;

/// <summary>
/// Settings read once at startup from configuration (environment variables on Railway).
/// </summary>
public sealed record AppOptions
{
    public required string Password { get; init; }
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxVideoHeight { get; init; } = 1080;
    public int RateLimitPermits { get; init; } = 10;
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(15);
    public string TempDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "ytdl");
    public string FFmpegPath { get; init; } = "ffmpeg";

    /// <summary>
    /// Builds options from configuration. Throws when APP_PASSWORD is missing so the app refuses to start.
    /// </summary>
    public static AppOptions FromConfiguration(IConfiguration config)
    {
        var password = config["APP_PASSWORD"];
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "APP_PASSWORD is not set. Refusing to start without a password."
            );

        var defaults = new AppOptions { Password = password };

        return defaults with
        {
            MaxDuration = TimeSpan.FromMinutes(
                ReadPositiveInt(config, "MAX_DURATION_MINUTES", (int)defaults.MaxDuration.TotalMinutes)
            ),
            MaxVideoHeight = ReadPositiveInt(config, "MAX_VIDEO_HEIGHT", defaults.MaxVideoHeight),
            RateLimitPermits = ReadPositiveInt(config, "RATE_LIMIT_PERMITS", defaults.RateLimitPermits),
            RateLimitWindow = TimeSpan.FromMinutes(
                ReadPositiveInt(config, "RATE_LIMIT_WINDOW_MINUTES", (int)defaults.RateLimitWindow.TotalMinutes)
            ),
            TempDirectory = NonEmpty(config["TEMP_DIR"]) ?? defaults.TempDirectory,
            FFmpegPath = NonEmpty(config["FFMPEG_PATH"]) ?? defaults.FFmpegPath,
        };
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int ReadPositiveInt(IConfiguration config, string key, int fallback)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (int.TryParse(raw, out var value) && value > 0)
            return value;

        throw new InvalidOperationException($"{key} must be a positive integer.");
    }
}
