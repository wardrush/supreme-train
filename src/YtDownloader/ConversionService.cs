using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace YtDownloader;

public enum OutputFormat
{
    Mp4,
    Mp3,
}

public sealed record ConversionResult(string FilePath, string DownloadName, string ContentType);

/// <summary>Thrown for policy rejections that happen after the video's metadata is known.</summary>
public sealed class PolicyRejectedException(string message) : Exception(message);

/// <summary>
/// Allows one conversion at a time. A second request is rejected (429) rather than queued,
/// so a phone user gets an immediate answer instead of a hanging request.
/// </summary>
public sealed class ConversionGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool TryEnter() => _semaphore.Wait(0);

    public void Exit() => _semaphore.Release();
}

public sealed class ConversionService(
    YoutubeClient youtube,
    AppOptions options,
    TempFiles tempFiles,
    ILogger<ConversionService> logger
)
{
    public async Task<ConversionResult> ConvertAsync(
        VideoId videoId,
        OutputFormat format,
        CancellationToken cancellationToken
    )
    {
        var video = await youtube.Videos.GetAsync(videoId, cancellationToken);

        var rejection = DurationPolicy.Check(video.Duration, options.MaxDuration);
        if (rejection is not null)
            throw new PolicyRejectedException(rejection);

        var manifest = await youtube.Videos.Streams.GetManifestAsync(videoId, cancellationToken);
        var streams = StreamSelector.Select(manifest, format, options.MaxVideoHeight);

        var container = format == OutputFormat.Mp3 ? Container.Mp3 : Container.Mp4;
        var path = tempFiles.NewJobPath(container.Name);

        try
        {
            var request = new ConversionRequestBuilder(path)
                .SetContainer(container)
                .SetFFmpegPath(options.FFmpegPath)
                // Only matters if a stream must be re-encoded; StreamSelector tries to avoid that.
                .SetPreset(ConversionPreset.VeryFast)
                .Build();

            logger.LogInformation(
                "Converting {VideoId} to {Format} using {Streams}",
                videoId.Value,
                container.Name,
                string.Join(", ", streams.Select(Describe))
            );

            await youtube.Videos.DownloadAsync(streams, request, progress: null, cancellationToken);
        }
        catch
        {
            tempFiles.DeleteJob(path);
            throw;
        }

        var contentType = format == OutputFormat.Mp3 ? "audio/mpeg" : "video/mp4";
        return new ConversionResult(path, FileNames.Safe(video.Title, container.Name), contentType);
    }

    private static string Describe(IStreamInfo s) =>
        s switch
        {
            IVideoStreamInfo v => $"{v.VideoCodec} {v.VideoQuality.Label} {s.Container.Name}",
            IAudioStreamInfo a => $"{a.AudioCodec} {s.Bitrate} {s.Container.Name}",
            _ => s.Container.Name,
        };
}

public static class StreamSelector
{
    /// <summary>
    /// Picks streams that FFmpeg can copy into the target container without re-encoding where possible:
    /// H.264 video at or below the height cap plus AAC (mp4) audio. MP3 always needs an audio transcode.
    /// </summary>
    public static IReadOnlyList<IStreamInfo> Select(StreamManifest manifest, OutputFormat format, int maxHeight)
    {
        var audio = manifest.GetAudioOnlyStreams().ToList();
        var video = manifest.GetVideoOnlyStreams().Where(s => s.VideoQuality.MaxHeight <= maxHeight).ToList();

        if (format == OutputFormat.Mp3)
        {
            var bestAudio = audio.MaxBy(s => s.Bitrate);
            if (bestAudio is not null)
                return [bestAudio];
        }
        else if (audio.Count > 0 && video.Count > 0)
        {
            var bestAudio = audio
                .OrderByDescending(s => s.Container == Container.Mp4)
                .ThenByDescending(s => s.Bitrate)
                .First();

            var bestVideo = video
                .OrderByDescending(s => s.VideoCodec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.Container == Container.Mp4)
                .ThenByDescending(s => s.VideoQuality)
                .First();

            return [bestAudio, bestVideo];
        }

        // Fall back to a muxed stream when adaptive streams are missing.
        var muxed = manifest.GetMuxedStreams().OrderByDescending(s => s.VideoQuality).FirstOrDefault();
        if (muxed is not null)
            return [muxed];

        throw new PolicyRejectedException("No downloadable streams were found for this video.");
    }
}

public static class FileNames
{
    public static string Safe(string title, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['"', '\'', '\\', '/', ':', '*', '?', '<', '>', '|']).ToHashSet();
        var cleaned = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();

        if (cleaned.Length > 100)
            cleaned = cleaned[..100].TrimEnd();
        if (cleaned.Length == 0)
            cleaned = "download";

        return $"{cleaned}.{extension}";
    }
}
