# TangSpeedtest

Windows 桌面下载测速工具。从固定测速地址下载文件，实时显示当前速度与平均速度，并把最近 10 次成功结果画成折线图。

## 功能

- 下载 `http://speedtest.360.cn/16m`（约 3 MB），测量平均下载速度（MB/s）
- 测试过程中显示当前速度、平均速度和进度
- 可随时取消；取消的结果不会写入历史
- 单次最长 120 秒，超时后停止并提示已下载量
- 保留最近 10 次成功测试的平均速度，用折线图展示（从旧到新）
- 可清除全部历史（需确认）
- 对 DNS 失败、网络不可达、连接中断等情况给出中文提示

只有完整下载成功（实际字节数达到预期大小的 95% 以上）才会记入历史。

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

`SpeedTestService` 用 `HttpClient` 流式读取响应，边下载边写入临时文件，避免把整个文件放进内存。请求带 `Cache-Control: no-cache`，减少中间缓存把结果测高。

速度单位为 MB/s（1 MB = 1024 × 1024 字节）：

- **当前速度**：相邻两次读取之间的瞬时速率
- **平均速度**：已下载字节数 ÷ 已用时间

相关常量在 `TangSpeedtest/SpeedTestConstants.cs`：

| 常量 | 值 |
| --- | --- |
| 测速地址 | `http://speedtest.360.cn/16m` |
| 预期大小 | 3,145,728 字节（3 MB） |
| 单次最长 | 120 秒 |
| 历史条数 | 10 |
| 读取缓冲 | 80 KB |

## 数据位置

| 内容 | 路径 |
| --- | --- |
| 历史记录 | `%LocalAppData%\TangSpeedtest\history.json` |
| 下载临时文件 | `%TEMP%\TangSpeedtest\download_*.tmp` |

临时文件使用 `DeleteOnClose`，测试结束时会再尝试删除。启动时会清理该目录中超过 24 小时的残留文件。历史文件损坏或无法读取时，界面按空历史启动，不会因此退出。

## 项目结构

```
TangSpeedtest.sln
TangSpeedtest/
  App.xaml                 应用入口
  MainWindow.xaml          主界面
  MainWindow.xaml.cs       创建 MainViewModel 并在关闭时释放
  SpeedTestConstants.cs    测速地址、超时、历史上限
  ViewModels/
    MainViewModel.cs       开始 / 取消 / 历史 / 图表
  Services/
    SpeedTestService.cs    下载测速与错误分类
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

测速结果反映的是到该文件所在服务器的下载情况，会受线路、时段和本机负载影响，不能代表全部网站的访问速度。测速地址使用 HTTP。
