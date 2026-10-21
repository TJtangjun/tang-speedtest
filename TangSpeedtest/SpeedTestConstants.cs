namespace TangSpeedtest;

internal static class SpeedTestConstants
{
    public const string DownloadUrl = "http://speedtest.360.cn/16m";
    public const int ParallelConnections = 3;
    public static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MeasureDuration = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TestWindow = WarmupDuration + MeasureDuration;
    public static readonly TimeSpan WorkerStopGrace = TimeSpan.FromSeconds(5);
    public const long MinimumMeasuredBytes = 64 * 1024;
    public const int MaxHistoryCount = 10;
    public const int ReadBufferSize = 81920;
    public const string TempCacheFolderName = "TangSpeedtest";
    public static readonly TimeSpan TempFileMaxAge = TimeSpan.FromHours(24);
}
