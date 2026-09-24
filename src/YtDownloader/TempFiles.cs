namespace YtDownloader;

/// <summary>
/// Owns the temp directory. All job files live flat in it, prefixed by a job ID,
/// so a single prefix delete removes the output plus the Converter's .stream-N.tmp files.
/// </summary>
public sealed class TempFiles(AppOptions options, ILogger<TempFiles> logger)
{
    public string Root => options.TempDirectory;

    /// <summary>
    /// Runs at startup. A fresh single-instance process has no jobs in flight,
    /// so anything already in the directory is left over from a crash or restart.
    /// </summary>
    public int SweepAll()
    {
        Directory.CreateDirectory(Root);
        var removed = 0;

        foreach (var path in Directory.EnumerateFileSystemEntries(Root))
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else
                    File.Delete(path);
                removed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete stale temp entry {Path}", path);
            }
        }

        if (removed > 0)
            logger.LogInformation("Startup sweep removed {Count} stale temp entries", removed);

        return removed;
    }

    public string NewJobPath(string extension) =>
        Path.Combine(Root, $"{Guid.NewGuid():N}.{extension}");

    /// <summary>
    /// Deletes the job file and anything sharing its name as a prefix. Never throws.
    /// </summary>
    public void DeleteJob(string jobPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(jobPath)!;
            var prefix = Path.GetFileName(jobPath);
            foreach (var file in Directory.EnumerateFiles(dir, prefix + "*"))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete temp files for {Path}", jobPath);
        }
    }
}
