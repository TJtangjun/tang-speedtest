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
    private readonly TempCacheCleanupService _cacheCleanup;

    public SpeedTestService(TempCacheCleanupService cacheCleanup)
    {
        _cacheCleanup = cacheCleanup;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
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
        using var timeoutCts = new CancellationTokenSource(SpeedTestConstants.MaxTestDuration);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stopwatch = Stopwatch.StartNew();
        long totalBytes = 0;
        string? tempPath = null;

        try
        {
            tempPath = TempCacheCleanupService.CreateTempDownloadPath();
            using var request = new HttpRequestMessage(HttpMethod.Get, SpeedTestConstants.DownloadUrl);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failed(totalBytes, stopwatch.Elapsed, $"服务器返回 {(int)response.StatusCode}。");
            }

            await using var networkStream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            await using var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                SpeedTestConstants.ReadBufferSize,
                FileOptions.SequentialScan | FileOptions.DeleteOnClose);

            var buffer = new byte[SpeedTestConstants.ReadBufferSize];
            var speedWindow = new SpeedSampleWindow();

            while (true)
            {
                int read;
                try
                {
                    read = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Cancelled(totalBytes, stopwatch.Elapsed);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    return TimedOut(totalBytes, stopwatch.Elapsed);
                }

                if (read == 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token).ConfigureAwait(false);
                totalBytes += read;

                var instant = speedWindow.AddSample(totalBytes, stopwatch.Elapsed);
                var averageSoFar = ToMegabytesPerSecond(totalBytes, stopwatch.Elapsed);

                var percent = Math.Min(100.0, totalBytes * 100.0 / SpeedTestConstants.ExpectedFileSizeBytes);
                progress.Report(new SpeedTestProgressReport
                {
                    BytesDownloaded = totalBytes,
                    ProgressPercent = percent,
                    InstantMegabytesPerSecond = instant,
                    AverageMegabytesPerSecond = averageSoFar,
                });
            }

            stopwatch.Stop();
            var average = ToMegabytesPerSecond(totalBytes, stopwatch.Elapsed);

            if (totalBytes < SpeedTestConstants.ExpectedFileSizeBytes * 0.95)
            {
                return Failed(
                    totalBytes,
                    stopwatch.Elapsed,
                    "下载未完成，可能是网络中断或连接被重置。",
                    average);
            }

            return new SpeedTestResult
            {
                Kind = SpeedTestCompletionKind.Succeeded,
                BytesDownloaded = totalBytes,
                Duration = stopwatch.Elapsed,
                AverageMegabytesPerSecond = average,
                UserMessage = null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(totalBytes, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return TimedOut(totalBytes, stopwatch.Elapsed);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            return Failed(totalBytes, stopwatch.Elapsed, MapNetworkError(ex));
        }
        catch (HttpRequestException ex)
        {
            return Failed(totalBytes, stopwatch.Elapsed, MapNetworkError(ex));
        }
        catch (SocketException ex)
        {
            return Failed(totalBytes, stopwatch.Elapsed, MapSocketError(ex));
        }
        catch (IOException)
        {
            return Failed(totalBytes, stopwatch.Elapsed, "网络读写失败，请检查连接后重试。");
        }
        catch (Exception)
        {
            return Failed(totalBytes, stopwatch.Elapsed, "测试失败，请稍后重试。");
        }
        finally
        {
            _cacheCleanup.TryDeleteFile(tempPath);
            _cacheCleanup.CleanupStaleFiles();
        }
    }

    private static SpeedTestResult Cancelled(long bytes, TimeSpan duration)
    {
        return new SpeedTestResult
        {
            Kind = SpeedTestCompletionKind.CancelledByUser,
            BytesDownloaded = bytes,
            Duration = duration,
            AverageMegabytesPerSecond = ToMegabytesPerSecond(bytes, duration),
            UserMessage = "已取消测试。",
        };
    }

    private static SpeedTestResult TimedOut(long bytes, TimeSpan duration)
    {
        return new SpeedTestResult
        {
            Kind = SpeedTestCompletionKind.TimedOut,
            BytesDownloaded = bytes,
            Duration = duration,
            AverageMegabytesPerSecond = ToMegabytesPerSecond(bytes, duration),
            UserMessage = $"已超过最大测试时间（{SpeedTestConstants.MaxTestDuration.TotalSeconds:0} 秒），测试已停止。",
        };
    }

    private static SpeedTestResult Failed(
        long bytes,
        TimeSpan duration,
        string message,
        double average = 0)
    {
        if (average <= 0)
        {
            average = ToMegabytesPerSecond(bytes, duration);
        }

        return new SpeedTestResult
        {
            Kind = SpeedTestCompletionKind.Failed,
            BytesDownloaded = bytes,
            Duration = duration,
            AverageMegabytesPerSecond = average,
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

    private static string MapNetworkError(HttpRequestException ex)
    {
        if (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return "服务暂时不可用，请稍后重试。";
        }

        var inner = ex.InnerException;
        if (inner is SocketException socketEx)
        {
            return MapSocketError(socketEx);
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class SpeedSampleWindow
    {
        private long _lastBytes;
        private TimeSpan _lastElapsed = TimeSpan.Zero;

        public double AddSample(long totalBytes, TimeSpan elapsed)
        {
            if (_lastElapsed == TimeSpan.Zero)
            {
                _lastBytes = totalBytes;
                _lastElapsed = elapsed;
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
