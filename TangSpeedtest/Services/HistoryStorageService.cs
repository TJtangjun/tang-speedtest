using System.Text.Json;
using TangSpeedtest.Models;

namespace TangSpeedtest.Services;

internal sealed class HistoryStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;

    public HistoryStorageService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(appData, "TangSpeedtest");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "history.json");
    }

    public IReadOnlyList<SpeedTestHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<SpeedTestHistoryEntry>();
            }

            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<SpeedTestHistoryEntry>>(json, JsonOptions);
            if (list == null || list.Count == 0)
            {
                return Array.Empty<SpeedTestHistoryEntry>();
            }

            return list
                .OrderBy(e => e.CompletedAtLocal)
                .TakeLast(SpeedTestConstants.MaxHistoryCount)
                .ToList();
        }
        catch
        {
            return Array.Empty<SpeedTestHistoryEntry>();
        }
    }

    public void Save(IReadOnlyList<SpeedTestHistoryEntry> entries)
    {
        try
        {
            var trimmed = entries
                .OrderBy(e => e.CompletedAtLocal)
                .TakeLast(SpeedTestConstants.MaxHistoryCount)
                .ToList();
            var json = JsonSerializer.Serialize(trimmed, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Persist failures should not crash the app.
        }
    }
}
