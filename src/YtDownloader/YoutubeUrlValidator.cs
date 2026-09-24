using YoutubeExplode.Videos;

namespace YtDownloader;

/// <summary>
/// Accepts only http(s) URLs on known YouTube hosts that carry a parseable video ID.
/// Bare IDs and other hosts are rejected before any network call is made.
/// </summary>
public static class YoutubeUrlValidator
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "music.youtube.com",
        "youtu.be",
        "www.youtube-nocookie.com",
        "youtube-nocookie.com",
    };

    public static bool TryGetVideoId(string? input, out VideoId videoId)
    {
        videoId = default;

        if (string.IsNullOrWhiteSpace(input) || input.Length > 2048)
            return false;

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return false;

        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (!AllowedHosts.Contains(uri.Host))
            return false;

        var parsed = VideoId.TryParse(uri.AbsoluteUri);
        if (parsed is null)
            return false;

        videoId = parsed.Value;
        return true;
    }
}
