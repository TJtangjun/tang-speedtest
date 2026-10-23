using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TangSpeedtest.Models;
using TangSpeedtest.Services;

namespace TangSpeedtest.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SpeedTestService _speedTestService;
    private readonly HistoryStorageService _historyStorage;
    private CancellationTokenSource? _testCts;
    private readonly Dispatcher _dispatcher;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        var cacheCleanup = new TempCacheCleanupService();
        cacheCleanup.CleanupStaleFiles();
        _historyStorage = new HistoryStorageService();
        _speedTestService = new SpeedTestService();

        HistoryEntries = new ObservableCollection<SpeedTestHistoryEntry>(_historyStorage.Load());
        RefreshChart();

        StartTestCommand = new AsyncRelayCommand(StartTestAsync, () => !IsRunning);
        CancelTestCommand = new RelayCommand(CancelTest, () => IsRunning);
        ClearHistoryCommand = new RelayCommand(ClearHistory, CanClearHistory);
    }

    public IAsyncRelayCommand StartTestCommand { get; }
    public IRelayCommand CancelTestCommand { get; }
    public IRelayCommand ClearHistoryCommand { get; }

    public ObservableCollection<SpeedTestHistoryEntry> HistoryEntries { get; }

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _statusMessage = "Click Start Test to measure your download speed.";

    [ObservableProperty]
    private string _currentSpeedText = "—";

    [ObservableProperty]
    private string _averageSpeedText = "—";

    [ObservableProperty]
    private string _lastResultText = "—";

    [ObservableProperty]
    private ISeries[] _chartSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] _xAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private Axis[] _yAxes = Array.Empty<Axis>();

    partial void OnIsRunningChanged(bool value)
    {
        StartTestCommand.NotifyCanExecuteChanged();
        CancelTestCommand.NotifyCanExecuteChanged();
        ClearHistoryCommand.NotifyCanExecuteChanged();
    }

    private async Task StartTestAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _testCts = new CancellationTokenSource();
        IsRunning = true;
        ProgressPercent = 0;
        CurrentSpeedText = "0.00 MB/s";
        AverageSpeedText = "0.00 MB/s";
        StatusMessage = "Opening 3 download connections…";

        var progress = new Progress<SpeedTestProgressReport>(report =>
        {
            _dispatcher.Invoke(() =>
            {
                if (!IsRunning)
                {
                    return;
                }

                ProgressPercent = report.ProgressPercent;
                CurrentSpeedText = $"{report.InstantMegabytesPerSecond:F2} MB/s";
                AverageSpeedText = $"{report.AverageMegabytesPerSecond:F2} MB/s";
                StatusMessage = report.IsWarmup
                    ? "Warming up connections (not counted)…"
                    : "Measuring download speed…";
            });
        });

        SpeedTestResult result;
        try
        {
            result = await _speedTestService.RunAsync(progress, _testCts.Token).ConfigureAwait(true);
        }
        catch (Exception)
        {
            result = new SpeedTestResult
            {
                Kind = SpeedTestCompletionKind.Failed,
                UserMessage = "Test failed. Please try again later.",
            };
        }

        _testCts.Dispose();
        _testCts = null;
        IsRunning = false;

        switch (result.Kind)
        {
            case SpeedTestCompletionKind.Succeeded:
                StatusMessage = "Test complete.";
                ProgressPercent = 100;
                AverageSpeedText = $"{result.AverageMegabytesPerSecond:F2} MB/s";
                LastResultText = FormatResultLine(result);
                AppendHistory(result);
                break;
            case SpeedTestCompletionKind.CancelledByUser:
                StatusMessage = result.UserMessage ?? "Test cancelled.";
                LastResultText = "Test cancelled. Result was not saved to history.";
                ProgressPercent = 0;
                CurrentSpeedText = "—";
                AverageSpeedText = "—";
                break;
            case SpeedTestCompletionKind.TimedOut:
                StatusMessage = result.UserMessage ?? "Test timed out.";
                LastResultText = $"Did not finish in time (downloaded {FormatBytes(result.BytesDownloaded)}).";
                ProgressPercent = 0;
                break;
            default:
                StatusMessage = result.UserMessage ?? "Test failed.";
                LastResultText = string.IsNullOrEmpty(result.UserMessage)
                    ? "Test failed."
                    : result.UserMessage;
                ProgressPercent = 0;
                break;
        }
    }

    private void CancelTest()
    {
        if (_testCts == null || _testCts.IsCancellationRequested)
        {
            return;
        }

        _testCts.Cancel();
        StatusMessage = "Cancelling…";
    }

    private void AppendHistory(SpeedTestResult result)
    {
        var entry = new SpeedTestHistoryEntry
        {
            CompletedAtLocal = DateTime.Now,
            AverageSpeedMegabytesPerSecond = result.AverageMegabytesPerSecond,
            BytesDownloaded = result.BytesDownloaded,
            Duration = result.Duration,
        };

        HistoryEntries.Add(entry);
        while (HistoryEntries.Count > SpeedTestConstants.MaxHistoryCount)
        {
            HistoryEntries.RemoveAt(0);
        }

        _historyStorage.Save(HistoryEntries.ToList());
        RefreshChart();
        ClearHistoryCommand.NotifyCanExecuteChanged();
    }

    private bool CanClearHistory()
    {
        return !IsRunning && HistoryEntries.Count > 0;
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show(
            "Clear all speed test history? This cannot be undone.",
            "Clear history",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        HistoryEntries.Clear();
        _historyStorage.Save(HistoryEntries.ToList());
        RefreshChart();
        ClearHistoryCommand.NotifyCanExecuteChanged();
    }

    private void RefreshChart()
    {
        var values = HistoryEntries
            .Select(e => e.AverageSpeedMegabytesPerSecond)
            .ToArray();

        var labels = Enumerable.Range(1, values.Length)
            .Select(i => $"#{i}")
            .ToArray();

        ChartSeries =
        [
            new LineSeries<double>
            {
                Values = values,
                Name = "Average speed",
                Fill = null,
                GeometrySize = 10,
                LineSmoothness = 0.85,
                Stroke = new SolidColorPaint(SKColors.DodgerBlue) { StrokeThickness = 3 },
                GeometryFill = new SolidColorPaint(SKColors.DodgerBlue),
                GeometryStroke = new SolidColorPaint(SKColors.White) { StrokeThickness = 2 },
            },
        ];

        XAxes =
        [
            new Axis
            {
                Labels = labels,
                Name = "Recent tests (oldest → newest)",
                LabelsRotation = 0,
                TextSize = 12,
            },
        ];

        var maxValue = values.Length == 0 ? 1 : values.Max();
        YAxes =
        [
            new Axis
            {
                Name = "MB/s",
                MinLimit = 0,
                MaxLimit = Math.Max(1, maxValue * 1.2),
                Labeler = value => $"{value:F1}",
            },
        ];
    }

    private static string FormatResultLine(SpeedTestResult result)
    {
        var mbps = result.AverageMegabytesPerSecond * (1024.0 * 1024.0) * 8.0 / 1_000_000.0;
        var mbpsText = mbps >= 10 ? $"{mbps:F0}" : $"{mbps:F1}";
        return $"Avg {result.AverageMegabytesPerSecond:F2} MB/s (~{mbpsText} Mbps) · measured {result.Duration.TotalSeconds:F1}s · {FormatBytes(result.BytesDownloaded)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:F1} KB";
        }

        var mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:F1} MB";
        }

        var gb = mb / 1024.0;
        return $"{gb:F2} GB";
    }

    public void Dispose()
    {
        _testCts?.Cancel();
        _testCts?.Dispose();
        _speedTestService.Dispose();
    }
}
