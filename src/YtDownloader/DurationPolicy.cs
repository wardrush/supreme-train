namespace YtDownloader;

public static class DurationPolicy
{
    /// <summary>
    /// Returns null when the video may be converted, otherwise a readable reason.
    /// A null duration means a live stream or premiere, which is always rejected.
    /// </summary>
    public static string? Check(TimeSpan? duration, TimeSpan max)
    {
        if (duration is null)
            return "Live streams and premieres are not supported.";

        if (duration.Value > max)
            return $"Video is {Format(duration.Value)} long; the limit is {Format(max)}.";

        return null;
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{t.Minutes}m {t.Seconds:D2}s";
}
