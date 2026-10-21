# TangSpeedtest

Windows 桌面下载测速工具。用 3 条连接持续从固定测速地址下载，实时显示当前速度与平均速度，并把最近 10 次成功结果画成折线图。

## 功能

- 并行 3 条连接，反复下载 `http://speedtest.360.cn/16m`（约 3 MB），测量稳定下载速度
- 前 2 秒为预热，不计入成绩；之后连续测量 10 秒
- 测试过程中显示当前速度、平均速度和进度（进度按时间，而不是按单个文件）
- 可随时取消；取消的结果不会写入历史
- 测量阶段至少下载 64 KB 才算成功；数据过少、连接失败或中途断开都不会写入历史
- 保留最近 10 次成功测试的平均速度，用折线图展示（从旧到新）
- 可清除全部历史（需确认）
- 对 DNS 失败、网络不可达、连接中断等情况给出中文提示

计入历史的平均速度只包含测量阶段：测量阶段字节数 ÷ 测量时长。

## 环境要求

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（目标框架 `net8.0-windows`）
- 测试时需要能访问 `speedtest.360.cn`

## 构建与运行

在仓库根目录执行：

```powershell
dotnet build TangSpeedtest.sln
dotnet run --project TangSpeedtest\TangSpeedtest.csproj
```

发布为独立可执行文件（无需目标机器安装 .NET）：

```powershell
dotnet publish TangSpeedtest\TangSpeedtest.csproj -c Release -r win-x64 --self-contained true
```

输出目录为 `TangSpeedtest\bin\Release\net8.0-windows\win-x64\publish\`。

也可以用 Visual Studio 2022 打开 `TangSpeedtest.sln`，直接生成并运行。

## 测速方式

`SpeedTestService` 同时发起 3 个下载。每条连接在测试窗口内反复请求测速地址，请求带随机参数和 `Cache-Control: no-cache`，减少中间缓存把结果测高。响应体只用来计数，不写入磁盘。连接默认复用，避免每个小文件都重新握手。

单次测试约 12 秒：

- **预热 2 秒**：建立连接并让 TCP 窗口爬升，这段字节不计入平均速度
- **测量 10 秒**：平均速度 = 这 10 秒内 3 条连接下载的总字节 ÷ 实际测量时长

速度单位为 MB/s（1 MB = 1024 × 1024 字节）：

- **当前速度**：最近约 100 毫秒内的合计速率
- **平均速度**：预热结束后的测量字节 ÷ 已测量时间
- **结果中的 Mbps**：按十进制兆比特换算（字节 × 8 ÷ 1,000,000），用来对照签约带宽

相关常量在 `TangSpeedtest/SpeedTestConstants.cs`：

| 常量 | 值 |
| --- | --- |
| 测速地址 | `http://speedtest.360.cn/16m` |
| 并行连接 | 3 |
| 预热 | 2 秒 |
| 测量 | 10 秒 |
| 有效样本下限 | 64 KB |
| 历史条数 | 10 |
| 读取缓冲 | 80 KB |

停止测试后，如果连接没有马上退出，最多再等 5 秒。

## 数据位置

| 内容 | 路径 |
| --- | --- |
| 历史记录 | `%LocalAppData%\TangSpeedtest\history.json` |

测试本身不写下载文件。启动时仍会清理 `%TEMP%\TangSpeedtest` 中超过 24 小时的旧临时文件（早期版本会把测速文件下到这个目录）。历史文件损坏或无法读取时，界面按空历史启动，不会因此退出。

## 项目结构

```
TangSpeedtest.sln
TangSpeedtest/
  App.xaml                 应用入口
  MainWindow.xaml          主界面
  MainWindow.xaml.cs       创建 MainViewModel 并在关闭时释放
  SpeedTestConstants.cs    测速地址、连接数、预热与测量时长
  ViewModels/
    MainViewModel.cs       开始 / 取消 / 历史 / 图表
  Services/
    SpeedTestService.cs    多连接下载测速与错误分类
    HistoryStorageService.cs
    TempCacheCleanupService.cs
  Models/
    SpeedTestHistoryEntry.cs
```

界面通过 MVVM 绑定到 `MainViewModel`：`开始测试`、`取消`、`清除历史` 分别对应 `StartTestCommand`、`CancelTestCommand`、`ClearHistoryCommand`。折线图使用 LiveCharts2。

## 依赖

- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) 8.4.2
- [LiveChartsCore.SkiaSharpView.WPF](https://livecharts.dev/) 2.0.5

## 说明

测速结果反映的是到该文件所在服务器、用 3 条连接能拉到的下载速度，会受线路、时段和本机负载影响，不能代表全部网站的访问速度。测速地址使用 HTTP。该文件大约只有 3 MB，百兆和三百兆线路上反复下载已经能看出稳定吞吐；千兆线路上，单文件偏小、请求要反复发起，读数仍可能低于签约带宽。
