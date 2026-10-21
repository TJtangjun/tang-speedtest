using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace TangSpeedtest.Services;

internal enum SpeedTestCompletionKind
{
    Succeeded,
    CancelledByUser,
    TimedOut,
    Failed,
}

internal sealed class SpeedTestProgressReport
{
    public long BytesDownloaded { get; init; }
    public double ProgressPercent { get; init; }
    public double InstantMegabytesPerSecond { get; init; }
    public double AverageMegabytesPerSecond { get; init; }
    public bool IsWarmup { get; init; }
}

internal sealed class SpeedTestResult
{
    public SpeedTestCompletionKind Kind { get; init; }
    public long BytesDownloaded { get; init; }
    public TimeSpan Duration { get; init; }
    public double AverageMegabytesPerSecond { get; init; }
    public string? UserMessage { get; init; }
}

internal sealed class SpeedTestService : IDisposable
{
    private readonly HttpClient _httpClient;

    public SpeedTestService()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false,
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _httpClient.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
        };
        _httpClient.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
    }

    public async Task<SpeedTestResult> RunAsync(
        IProgress<SpeedTestProgressReport> progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var meter = new TransferMeter();
        meter.Report(TimeSpan.Zero, progress);

        var windowCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, windowCts.Token);
        var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var reportTask = ReportLoopAsync(meter, clock, progress, linkedCts.Token);
        var workers = new Task[SpeedTestConstants.ParallelConnections];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = RunWorkerAsync(meter, clock, linkedCts.Token);
        }

        var allWorkers = Task.WhenAll(workers);
        var delayTask = Task.Delay(SpeedTestConstants.TestWindow, delayCts.Token);
        var snapshot = new MeasurementSnapshot(0, 0, TimeSpan.Zero, null);
        var frozen = false;

        try
        {
            await Task.WhenAny(allWorkers, delayTask).ConfigureAwait(false);
            snapshot = meter.Freeze(clock.Elapsed);
            frozen = true;

            if (cancellationToken.IsCancellationRequested || delayTask.IsCanceled)
            {
                return Cancelled(snapshot);
            }

            if (allWorkers.IsFaulted)
            {
                var error = allWorkers.Exception?.InnerExceptions.FirstOrDefault();
                return Failed(snapshot, error == null ? "测试失败，请稍后重试。" : DescribeFailure(error));
            }

            if (!delayTask.IsCompletedSuccessfully)
            {
                if (snapshot.Error != null)
                {
                    return Failed(snapshot, DescribeFailure(snapshot.Error));
                }

                return Failed(snapshot, "下载未完成，可能是网络中断或连接被重置。");
            }

            if (snapshot.MeasuredBytes < SpeedTestConstants.MinimumMeasuredBytes)
            {
                if (snapshot.Error != null)
                {
                    return Failed(snapshot, DescribeFailure(snapshot.Error));
                }

                if (snapshot.WarmupBytes + snapshot.MeasuredBytes <= 0)
                {
                    return Failed(snapshot, "连接超时，当前网络可能较差或信号较弱。");
                }

                return Failed(
                    snapshot,
                    $"测量时间内下载数据过少（{snapshot.MeasuredBytes / 1024.0:F1} KB），无法得到有效速度。");
            }

            return new SpeedTestResult
            {
                Kind = SpeedTestCompletionKind.Succeeded,
                BytesDownloaded = snapshot.MeasuredBytes,
                Duration = snapshot.MeasureElapsed,
                AverageMegabytesPerSecond = ToMegabytesPerSecond(snapshot.MeasuredBytes, snapshot.MeasureElapsed),
                UserMessage = null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!frozen)
            {
                snapshot = meter.Freeze(clock.Elapsed);
            }

            return Cancelled(snapshot);
        }
        catch (Exception)
        {
            if (!frozen)
            {
                snapshot = meter.Freeze(clock.Elapsed);
            }

            return Failed(snapshot, "测试失败，请稍后重试。");
        }
        finally
        {
            var stopped = await ShutdownAsync(windowCts, delayCts, delayTask, allWorkers, reportTask)
                .ConfigureAwait(false);
            if (stopped)
            {
                windowCts.Dispose();
                linkedCts.Dispose();
                delayCts.Dispose();
            }
        }
    }

    private async Task RunWorkerAsync(TransferMeter meter, Stopwatch clock, CancellationToken cancellationToken)
    {
        var buffer = new byte[SpeedTestConstants.ReadBufferSize];
        var consecutiveFailures = 0;

        while (clock.Elapsed < SpeedTestConstants.TestWindow
            && !cancellationToken.IsCancellationRequested
            && !meter.IsFrozen)
        {
            try
            {
                var reachedEnd = await DownloadOnceAsync(buffer, meter, clock, cancellationToken)
                    .ConfigureAwait(false);
                consecutiveFailures = 0;
                if (!reachedEnd)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (IsTransferError(ex))
            {
                consecutiveFailures++;
                meter.RememberError(ex);
                if (consecutiveFailures >= 3)
                {
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> DownloadOnceAsync(
        byte[] buffer,
        TransferMeter meter,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        if (clock.Elapsed >= SpeedTestConstants.TestWindow
            || cancellationToken.IsCancellationRequested
            || meter.IsFrozen)
        {
            return false;
        }

        var url = $"{SpeedTestConstants.DownloadUrl}?r={Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"服务器返回 {(int)response.StatusCode}。",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var gotBytes = false;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested || meter.IsFrozen)
            {
                return false;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (!gotBytes)
                {
                    throw new IOException("响应没有数据。");
                }

                return true;
            }

            gotBytes = true;
            meter.Add(read, clock.Elapsed);
        }
    }

    private static async Task ReportLoopAsync(
        TransferMeter meter,
        Stopwatch clock,
        IProgress<SpeedTestProgressReport> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                meter.Report(clock.Elapsed, progress);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<bool> ShutdownAsync(
        CancellationTokenSource windowCts,
        CancellationTokenSource delayCts,
        Task delayTask,
        Task allWorkers,
        Task reportTask)
    {
        try
        {
            windowCts.Cancel();
            if (!delayTask.IsCompleted)
            {
                delayCts.Cancel();
            }

            try
            {
                await delayTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            var finished = await Task.WhenAny(allWorkers, Task.Delay(SpeedTestConstants.WorkerStopGrace))
                .ConfigureAwait(false);
            if (finished == allWorkers)
            {
                try
                {
                    await allWorkers.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Worker failures are already reflected in the test result.
                }
            }

            try
            {
                await reportTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return finished == allWorkers;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsTransferError(Exception ex)
    {
        return ex is HttpRequestException or IOException or SocketException;
    }

    private static SpeedTestResult Cancelled(MeasurementSnapshot snapshot)
    {
        return new SpeedTestResult
        {
            Kind = SpeedTestCompletionKind.CancelledByUser,
            BytesDownloaded = snapshot.MeasuredBytes,
            Duration = snapshot.MeasureElapsed,
            AverageMegabytesPerSecond = ToMegabytesPerSecond(snapshot.MeasuredBytes, snapshot.MeasureElapsed),
            UserMessage = "已取消测试。",
        };
    }

    private static SpeedTestResult Failed(MeasurementSnapshot snapshot, string message)
    {
        return new SpeedTestResult
        {
            Kind = SpeedTestCompletionKind.Failed,
            BytesDownloaded = snapshot.MeasuredBytes,
            Duration = snapshot.MeasureElapsed,
            AverageMegabytesPerSecond = ToMegabytesPerSecond(snapshot.MeasuredBytes, snapshot.MeasureElapsed),
            UserMessage = message,
        };
    }

    private static double ToMegabytesPerSecond(long bytes, TimeSpan duration)
    {
        if (bytes <= 0 || duration.TotalSeconds <= 0)
        {
            return 0;
        }

        return bytes / duration.TotalSeconds / (1024.0 * 1024.0);
    }

    private static string DescribeFailure(Exception ex)
    {
        var socket = FindSocketException(ex);
        if (socket != null)
        {
            return MapSocketError(socket);
        }

        if (ex is HttpRequestException httpEx)
        {
            return MapNetworkError(httpEx);
        }

        if (ex is IOException)
        {
            return "网络读写失败，请检查连接后重试。";
        }

        return "测试失败，请稍后重试。";
    }

    private static string MapNetworkError(HttpRequestException ex)
    {
        if (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return "服务暂时不可用，请稍后重试。";
        }

        var socket = FindSocketException(ex);
        if (socket != null)
        {
            return MapSocketError(socket);
        }

        if (ex.StatusCode is HttpStatusCode status)
        {
            return $"服务器返回 {(int)status}。";
        }

        if (ex.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("不知道这样的主机", StringComparison.OrdinalIgnoreCase))
        {
            return "DNS 解析失败，请检查网络或 DNS 设置。";
        }

        return "网络请求失败，请检查是否已连接互联网。";
    }

    private static string MapSocketError(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData => "DNS 解析失败，请检查网络或 DNS 设置。",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable => "网络不可达，请检查网络连接。",
            SocketError.TimedOut => "连接超时，当前网络可能较差或信号较弱。",
            SocketError.ConnectionReset or SocketError.ConnectionAborted => "连接被中断，可能正在切换 Wi-Fi 或网络不稳定。",
            SocketError.NotConnected or SocketError.Shutdown => "网络已断开，请连接后重试。",
            _ => "网络异常，请检查连接后重试。",
        };
    }

    private static SocketException? FindSocketException(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is SocketException socketEx)
            {
                return socketEx;
            }

            ex = ex.InnerException;
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class MeasurementSnapshot
    {
        public MeasurementSnapshot(long measuredBytes, long warmupBytes, TimeSpan measureElapsed, Exception? error)
        {
            MeasuredBytes = measuredBytes;
            WarmupBytes = warmupBytes;
            MeasureElapsed = measureElapsed;
            Error = error;
        }

        public long MeasuredBytes { get; }
        public long WarmupBytes { get; }
        public TimeSpan MeasureElapsed { get; }
        public Exception? Error { get; }
    }

    private sealed class TransferMeter
    {
        private readonly object _gate = new();
        private readonly SpeedSampleWindow _window = new();
        private long _warmupBytes;
        private long _measuredBytes;
        private Exception? _error;
        private bool _frozen;

        public bool IsFrozen
        {
            get
            {
                lock (_gate)
                {
                    return _frozen;
                }
            }
        }

        public void Add(int count, TimeSpan elapsed)
        {
            if (count <= 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_frozen)
                {
                    return;
                }

                if (elapsed <= SpeedTestConstants.WarmupDuration)
                {
                    _warmupBytes += count;
                }
                else
                {
                    _measuredBytes += count;
                }
            }
        }

        public void RememberError(Exception ex)
        {
            lock (_gate)
            {
                _error ??= ex;
            }
        }

        public MeasurementSnapshot Freeze(TimeSpan elapsed)
        {
            lock (_gate)
            {
                _frozen = true;
                var measureElapsed = elapsed - SpeedTestConstants.WarmupDuration;
                if (measureElapsed < TimeSpan.Zero)
                {
                    measureElapsed = TimeSpan.Zero;
                }

                return new MeasurementSnapshot(_measuredBytes, _warmupBytes, measureElapsed, _error);
            }
        }

        public void Report(TimeSpan elapsed, IProgress<SpeedTestProgressReport> progress)
        {
            SpeedTestProgressReport report;
            lock (_gate)
            {
                if (_frozen)
                {
                    return;
                }

                var isWarmup = elapsed < SpeedTestConstants.WarmupDuration;
                long averageBytes;
                TimeSpan averageDuration;
                if (isWarmup)
                {
                    averageBytes = _warmupBytes;
                    averageDuration = elapsed;
                }
                else
                {
                    averageBytes = _measuredBytes;
                    averageDuration = elapsed - SpeedTestConstants.WarmupDuration;
                }

                var totalBytes = _warmupBytes + _measuredBytes;
                var windowSeconds = SpeedTestConstants.TestWindow.TotalSeconds;
                var percent = windowSeconds <= 0
                    ? 0
                    : Math.Min(100.0, elapsed.TotalSeconds / windowSeconds * 100.0);

                report = new SpeedTestProgressReport
                {
                    BytesDownloaded = isWarmup ? _warmupBytes : _measuredBytes,
                    ProgressPercent = percent,
                    InstantMegabytesPerSecond = _window.AddSample(totalBytes, elapsed),
                    AverageMegabytesPerSecond = ToMegabytesPerSecond(averageBytes, averageDuration),
                    IsWarmup = isWarmup,
                };
            }

            progress.Report(report);
        }
    }

    private sealed class SpeedSampleWindow
    {
        private long _lastBytes;
        private TimeSpan _lastElapsed;
        private bool _hasSample;

        public double AddSample(long totalBytes, TimeSpan elapsed)
        {
            if (!_hasSample)
            {
                _lastBytes = totalBytes;
                _lastElapsed = elapsed;
                _hasSample = true;
                return 0;
            }

            var deltaBytes = totalBytes - _lastBytes;
            var deltaSeconds = (elapsed - _lastElapsed).TotalSeconds;
            _lastBytes = totalBytes;
            _lastElapsed = elapsed;

            if (deltaBytes <= 0 || deltaSeconds <= 0)
            {
                return 0;
            }

            return deltaBytes / deltaSeconds / (1024.0 * 1024.0);
        }
    }
}
