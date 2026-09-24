using System.Net;
using YoutubeExplode.Exceptions;

namespace YtDownloader;

public sealed record ClassifiedError(int Status, string Code, string Message);

/// <summary>
/// Maps YoutubeExplode and conversion failures to distinct, readable messages.
/// YouTube's playability reason text is the only signal for age, region and bot checks,
/// so matching is by substring and may need updating when YouTube rewords it.
/// </summary>
public static class ErrorClassifier
{
    public const string BotCheckMessage =
        "YouTube blocked this server with a bot check (\"Sign in to confirm you're not a bot\"). "
        + "This is common on cloud/datacenter IPs. See README \"Known risks\".";

    // A bare 403/429 carries no reason text, so it is reported as a block, not as a confirmed bot check.
    public static string BlockedMessage(int status) =>
        $"YouTube refused the request (HTTP {status}). On cloud/datacenter IPs this usually means "
        + "YouTube is blocking the server's IP. See README \"Known risks\".";

    public static ClassifiedError Classify(Exception ex)
    {
        var text = ex.Message;

        if (LooksLikeBotCheck(text))
            return new(StatusCodes.Status503ServiceUnavailable, "bot_check", BotCheckMessage);

        return ex switch
        {
            RequestLimitExceededException => new(
                StatusCodes.Status503ServiceUnavailable,
                "blocked",
                BlockedMessage(429)
            ),
            VideoRequiresPurchaseException => new(
                StatusCodes.Status403Forbidden,
                "requires_purchase",
                "This video requires purchase and cannot be downloaded."
            ),
            VideoUnavailableException => new(
                StatusCodes.Status404NotFound,
                "unavailable",
                "Video unavailable: it may be private, removed, or the link is wrong."
            ),
            VideoUnplayableException when LooksAgeRestricted(text) => new(
                StatusCodes.Status403Forbidden,
                "age_restricted",
                "This video is age-restricted and needs a signed-in account, which this tool does not use."
            ),
            VideoUnplayableException when LooksRegionBlocked(text) => new(
                StatusCodes.Status451UnavailableForLegalReasons,
                "region_blocked",
                "This video is blocked in the server's region."
            ),
            VideoUnplayableException => new(
                StatusCodes.Status422UnprocessableEntity,
                "unplayable",
                "YouTube reports this video as unplayable. " + ExtractReason(text)
            ),
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } http => new(
                StatusCodes.Status503ServiceUnavailable,
                "blocked",
                BlockedMessage((int)http.StatusCode!)
            ),
            HttpRequestException => new(
                StatusCodes.Status502BadGateway,
                "upstream",
                "Could not reach YouTube. Try again shortly."
            ),
            _ when IsFfmpegFailure(ex) => new(
                StatusCodes.Status500InternalServerError,
                "conversion_failed",
                "FFmpeg failed while converting the file."
            ),
            YoutubeExplodeException => new(
                StatusCodes.Status502BadGateway,
                "youtube_changed",
                "YoutubeExplode could not process YouTube's response. The library may need an update (see README)."
            ),
            _ => new(StatusCodes.Status500InternalServerError, "internal", "Unexpected error. Check the server logs."),
        };
    }

    private static bool LooksLikeBotCheck(string text) =>
        Contains(text, "not a bot") || Contains(text, "confirm you're not") || Contains(text, "confirm you’re not");

    private static bool LooksAgeRestricted(string text) =>
        Contains(text, "confirm your age") || Contains(text, "age-restricted") || Contains(text, "inappropriate for some users");

    private static bool LooksRegionBlocked(string text) =>
        Contains(text, "available in your country")
        || Contains(text, "in your region")
        || Contains(text, "on copyright grounds")
        || Contains(text, "has blocked it in your country");

    // CliWrap's CommandExecutionException comes through as a transitive dependency of the Converter.
    private static bool IsFfmpegFailure(Exception ex) =>
        ex.GetType().FullName == "CliWrap.Exceptions.CommandExecutionException"
        || ex is System.ComponentModel.Win32Exception;

    private static string ExtractReason(string text)
    {
        const string marker = "Reason: '";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return "YouTube gave no reason.";

        start += marker.Length;
        var end = text.LastIndexOf('\'');
        return end > start ? $"Reason: {text[start..end]}" : "YouTube gave no reason.";
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
