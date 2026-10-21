namespace TangSpeedtest;

internal static class SpeedTestConstants
{
    public const string DownloadUrl = "http://speedtest.360.cn/16m";
    public const long ExpectedFileSizeBytes = 3_145_728;
    public const int MaxHistoryCount = 10;
    public static readonly TimeSpan MaxTestDuration = TimeSpan.FromSeconds(120);
    public const int ReadBufferSize = 81920;
    public const string TempCacheFolderName = "TangSpeedtest";
    public static readonly TimeSpan TempFileMaxAge = TimeSpan.FromHours(24);
}
