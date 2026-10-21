# OpenRevo

面向本机机械革命控制中心的功能与通信协议研究。目标是独立实现所需后台能力，不复刻厂商前端。

## 当前版本：0.4.0

已实现 Windows 11 原生 WinUI 3 的本机监控 MVP：设备与服务识别、CPU/GPU 温度和占用、两路风扇、电量、当前模式、能力清单、原始状态与本地诊断导出。

本版仍依赖已安装的厂商后台。启动时校验已审计的主板、BIOS 与服务文件；不匹配时停止连接。风扇曲线、色彩、电池阈值、功耗和超频写入尚未开放。缓存、旧数据、缺失值与断开后的历史值会分别显示。

0.4.0 补充会话统计、传感器缺失提示，以及系统供电和睡眠事件处理。单个传感器未推送不会终止其他监控；联网 S0 待机连接正常时继续监测，实际断线后手动重连，不重放模式命令。详见 [0.4.0 验收](docs/v0.4.0-audit.md)。

0.3.0 增加受限的四模式切换试点：保存基线、单条命令、两次一致回读、手动恢复及全字段比对。仅允许已审计机型、交流电与精确配置组合；狂暴需实时能力确认，自定义使用原厂已保存配置。详见 [0.3.0 审计](docs/v0.3.0-audit.md)。

0.2.0 增加单调时间计时、能力降级与过期处理、独立心跳超时、类型化诊断，以及嵌入式机型配置和统一协议证据目录。详细范围与验证记录见 [0.2.0 审计](docs/v0.2.0-audit.md)，0.1.0 基线见 [MVP 审计](docs/mvp-audit.md)。

## 下载与自动化

[GitHub Releases](https://github.com/Qrzzzz/OpenRevo/releases) 提供 Windows x64 便携 ZIP 与 `SHA256SUMS.txt`。已发布版本的适用范围和验收状态见对应随包记录。

`Windows CI` 在 main 推送和拉取请求中执行受限协议测试、CLI / WinUI 构建、便携包与源文件身份校验，保留包和验证日志。云端工作流不连接本机厂商服务，也不执行硬件写入。

`Release` 在推送 `v*` 标签时调用同一构建流程；标签必须与 `Directory.Build.props` 一致，并提供对应的 `docs/releases/vX.Y.Z.md`。构建成功后先创建草稿，上传 ZIP 与校验文件，下载核对两个资产的 SHA256 后发布。已有 Release 不会被覆盖；若流程失败留下草稿，应先检查失败原因和草稿状态。

## 构建与运行

需要 Windows 11 x64 和 `global.json` 固定的 .NET 10 SDK。运行 `scripts/build.ps1`；如系统只有 .NET 运行时，用 `-DotnetPath` 指定已有 SDK 的 `dotnet.exe`。可以把 SDK 放在 `.local/dotnet/`，或设置 `OPENREVO_DOTNET_EXE`。

```powershell
.\scripts\test.ps1 -DotnetPath '你的 SDK 路径\dotnet.exe'
.\scripts\build.ps1 -DotnetPath '你的 SDK 路径\dotnet.exe' -Publish
```

便携包输出到 `artifacts/OpenRevo-0.4.0-win-x64.zip`，解压整个文件夹后运行 `OpenRevo.exe`。自包含构建不要求目标机器另外安装 .NET。`Ctrl+R` 刷新，`Ctrl+S` 导出；“连接与外观”可切换主题。

`scripts/test.ps1 -LiveProbe` 会实际连接本机厂商服务，发送三种已审计的查询 / 监控请求；先关闭或断开 OpenRevo 窗口以释放连接槽位。普通测试仅使用本地测试服务器，不访问硬件。

## 静态分析基线

2026-10-18 完成第一轮静态分析，以及 Windows 服务、监听端口和少量机型配置的只读查询：

- 保存 11 个安装文件的本地副本，并校验 SHA256。
- 确认 `GCUBridge` 承载 MQTT 服务，默认端口 `13688`；本机监听 PID 与该服务匹配。
- 从托盘代码提取 42 个主题常量、6 条有发送端证据的命令和 60 个风扇状态模型字段。
- 从原生 DLL 反汇编提取 19 条 IOCTL，并与托管常量逐条交叉核对。
- 核心 `GCUService` 的反编译文本中有 4329 处 `Runtime exception` 占位，实际方法体尚未恢复。

这些静态结果不证明硬件控制可用。后续补充了实际查询与传感器读取证据，未恢复的核心处理方法及硬件写路径仍然保留为未知。

## 阅读入口

- [后续更新计划](docs/roadmap.md)
- [0.2.0 验收记录](docs/v0.2.0-audit.md)
- [关键处理链路与未恢复部分](docs/processing-chains.md)
- [静态分析、证据与复现](docs/static-analysis.md)
- [MQTT 协议与已知消息](docs/protocol.md)
- [屏幕、风扇、电池、功耗的实现边界](docs/hardware-controls.md)
- [统一协议证据目录](protocol/registry.json)
- [机型审计配置](protocol/device-profiles.json)
- [历史静态发送端目录](protocol/commands.json)

静态分析脚本只处理文件副本、反编译文本和系统只读元数据。`observe-bridge.py`、`OpenRevo.Probe` 和应用使用仅限本机的 MQTT 连接，允许固定查询及启用厂商监控采样；0.3.0 另设受限模式控制入口；没有通用发布入口，不加载厂商程序集或调用驱动。工具、厂商二进制、完整反编译结果和分析中间文件统一保存在被忽略的 `.local/`。

后续控制功能仍按机型支持、参数限制、联动行为和恢复机制分别审计、验收；不从本版的状态读取成功推断写入安全。
