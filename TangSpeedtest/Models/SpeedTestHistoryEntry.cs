namespace TangSpeedtest.Models;

public sealed class SpeedTestHistoryEntry
{
    public DateTime CompletedAtLocal { get; set; }
    public double AverageSpeedMegabytesPerSecond { get; set; }
    public long BytesDownloaded { get; set; }
    public TimeSpan Duration { get; set; }
}
