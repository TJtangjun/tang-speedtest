namespace TangSpeedtest.Services;

internal sealed class TempCacheCleanupService
{
    public void CleanupStaleFiles()
    {
        try
        {
            var folder = GetCacheFolderPath();
            if (!Directory.Exists(folder))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - SpeedTestConstants.TempFileMaxAge;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        info.Delete();
                    }
                }
                catch
                {
                    // Best-effort cleanup; ignore locked or inaccessible files.
                }
            }
        }
        catch
        {
            // Ignore cleanup failures so startup never fails.
        }
    }

    public void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore delete failures.
        }
    }

    public static string GetCacheFolderPath()
    {
        return Path.Combine(Path.GetTempPath(), SpeedTestConstants.TempCacheFolderName);
    }

    public static string CreateTempDownloadPath()
    {
        var folder = GetCacheFolderPath();
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"download_{Guid.NewGuid():N}.tmp");
    }
}
