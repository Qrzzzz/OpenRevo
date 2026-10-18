# 本机静态分析记录

分析日期：2026-10-18。本页记录第一轮范围：安装文件复制、PE 元数据、静态反编译、原生导出反汇编，以及 Windows 系统元数据只读查询。该轮没有启动新的厂商程序、加载其程序集、连接 MQTT、调用设备接口或修改系统设置。随后已进行原厂前端监控观察和独立 MQTT 状态读取，见 [MVP 审计](mvp-audit.md)；下述“本轮”均指静态分析轮次。

## 版本与本机观察

| 对象 | 本次观察 |
| --- | --- |
| 系统厂商 / 型号 | MECHREVO / YAOSHI Series |
| 安装器登记版本 | 机械革命控制中心 Service `5.56.51.44` |
| 当前用户 Appx 包 | ControlCenter3 `5.56.51.49` |
| GCUBridge 文件版本 | `1.0.1.10` |
| GCUService 文件版本 | `1.0.2.70` |
| SystrayComponent 文件版本 | `1.0.0.19` |
| 驱动 INF | Uniwill；2024-08-27；`11.2.18.347`；硬件 ID `ACPI\INOU0000` |
| OEM 配置 | `BridgeType=0`、`CustomizeTarget=56`、`ProjectID=26` |
| 实际服务 | `GCUBridge`，Running，采样时 PID 6064 |
| 实际监听 | `0.0.0.0:13688` 和 `[::]:13688`，PID 同为 6064 |

安装根目录：`C:\Program Files\OEM\机械革命控制中心`。

Appx 目录：`C:\Program Files\WindowsApps\ControlCenter3_5.56.51.49_x64__wrbgcf7aesyd8`。

监听地址来自 Windows TCP 表，说明本机服务绑定通配地址；没有验证其他机器是否能连接，也没有检查防火墙可达性。PID 是采样时状态，后续可能变化。

OEM `ProjectID=26` 在本轮看到的 `Define/ProjectID.cs` 枚举里没有对应项。其与 EC 项目号、BIOS 项目号的关系尚未证实，不能据此选择风扇或功耗寄存器布局。

## 证据层级

| 层级 | 本轮达到的内容 | 不能据此推断 |
| --- | --- | --- |
| 文件身份 | 11 个副本逐一 SHA256 校验 | 覆盖所有依赖或所有安装版本 |
| 常量 / 声明 | 主题、字段、方法签名、EC 常量、API 声明 | 本机支持该功能，或实际运行时会调用该路径 |
| 发送端逻辑 | 托盘的 JSON 消息、发布参数、订阅列表 | 服务端无副作用，或硬件执行成功 |
| 桥接接收逻辑 | 认证、部分服务主题、固件缓冲分支 | 核心 GCUService 的完整处理逻辑 |
| 原生调用点 | 19 个导出里的 IOCTL 与托管常量一致 | 驱动完整 ABI、参数范围或硬件语义 |
| 运行状态 | 服务与端口 PID 匹配 | MQTT 握手、真实消息、实时传感器或控制效果 |

所有协议目录条目均标记为静态证据、未进行实时验证。候选字符串不自动升级为协议事实。

## 已恢复和未恢复的代码

| 文件 | 结果 |
| --- | --- |
| GCUBridge.exe | 27 个 C# 文件，未见该类占位异常，桥接主要逻辑可读 |
| GCUService.exe | 432 个 C# 文件，4329 处 `throw new Exception("Runtime exception")` |
| GCUServicePlugin.dll | 13 个 C# 文件，未见该类占位异常；已读部分主要涉及应用集成与网络逻辑 |
| SystrayComponent.exe | 31 个 C# 文件，未见该类占位异常，可读消息发送和接收逻辑 |
| GamingCenter3_Cross.dll | 无 CLR 元数据；ILSpy 返回 70，不能按普通托管 DLL 反编译；仅做 PE / 字符串分析 |
| ACPIDriverDll.dll | 原生 x64，19 个导出，可直接静态反汇编 |

上述文件数是 ILSpy 输出文件数；异常占位数是文本出现次数，不是恢复率，也不是精确业务方法数。没有占位异常也不意味着完成逐方法正确性审计。

核心服务还存在间接委托调用和运行时辅助代码。当前能确认大量方法体受到保护，但没有确认具体保护器品牌或完成脱壳。不能通过加载程序集运行初始化器来“查看方法”，因为这会破坏本轮的静态分析边界。

## 本地证据位置

- `.local/evidence/manifest.json`：源路径、文件版本、长度和 SHA256。
- `.local/evidence/host-observation.json`：白名单 OEM 字段、服务和监听采样。
- `.local/decompiled/GCUBridge/`、`GCUService/`、`GCUServicePlugin/`、`Tray/`：完整反编译输出。
- `.local/analysis/pe-index.json`：导出、导入、CLR 目录信息。
- `.local/analysis/topic-candidates.json`：619 个符合路径形状的字符串候选，包含噪声和重复，不代表 619 个真实 MQTT 主题。
- `.local/analysis/acpi-export-disassembly.txt`：19 个原生导出范围的反汇编及导入函数注释。
- `protocol/`：从已读证据整理出的主题、命令、状态字段及 IOCTL 对照。

二进制身份中的关键 SHA256：

| 文件 | SHA256 |
| --- | --- |
| GCUBridge.exe | `7A1405E231A5E8581C4E9FC4FCE2302A77578742AC643AA3D42F7DADACA80695` |
| GCUService.exe | `0469CF6D9F55B9C9ABED70DB3A11AD126F5BAD2E9246F45FB747E17E11AED450` |
| ACPIDriverDll.dll | `97D7115943600C2A09951440859F9BD75FD0D8BFF9DB49C296C868B49DF8C8C6` |
| UWACPIDriver.sys | `E9840521975E9073881A488F9E978A18C93AED44C57FE2382B1D513FD27126E2` |
| SystrayComponent.exe | `3B4477C52E83B1788274833248F063BAB13FEA6BCDC4A2F91C1DE18FB9766FD9` |

## 复现

本机有 .NET 8 运行时但没有 .NET SDK。实际使用 [ILSpy 官方项目](https://github.com/icsharpcode/ILSpy)发布的 NuGet 工具包 `ilspycmd 9.1.0.7988`，解包后通过 `dotnet` 直接运行，不需要安装 SDK。工具包来源为 `https://api.nuget.org/v3-flatcontainer/ilspycmd/9.1.0.7988/ilspycmd.9.1.0.7988.nupkg`，下载文件保存在 `.local/tools/ilspycmd.zip`，SHA256 为 `2B5058F5CCC164C33B7AABF1A5EB0CF3D3A6AF6C145AAF58EFD3ED891443AF7C`。

Python 分析依赖在 `.local/tools/python`：`pefile 2024.8.26`、`capstone 5.0.9`。没有修改系统 Python 环境或全局 .NET 工具。

工具准备好以后，在工作区执行：

```powershell
.\scripts\collect-static-evidence.ps1
.\scripts\decompile-static-evidence.ps1
python scripts/index-static-evidence.py
python scripts/disassemble-acpi-exports.py
python scripts/build-protocol-catalog.py
```

采集脚本使用明确文件白名单，不复制用户日志、个人风扇表或整个注册表；目录名中的用户风扇表等仅用于结构发现。脚本默认安装路径与本机当前版本绑定，其他版本需显式传入路径，并重新审查证据。采集会更新本地快照与 manifest，重要旧轮次应先另行保存。

## 下一步的证据缺口

1. 恢复或独立验证核心服务的消息处理方法、参数校验和硬件调用链。
2. 确认本机 BIOS / EC / 主板能力与具体分支，避免把跨机型公共代码当成本机规范。
3. 设计不会复用现有客户端 ID、不会发布控制消息的状态观察流程；先排查连接和订阅事件是否触发额外行为。
4. 实际状态样本到位后，补齐类型、单位、缺失字段、更新频率和消息新鲜度。原始字段不能直接转换为控制参数。
5. 分别核清显示恢复、风扇失效保护、电池充电上下限、AC/DC 切换与功耗联动，再实现具体写操作。

本轮没有实时 MQTT 验证、驱动请求、屏幕操作、风扇调速、电池阈值或功耗测试。当前成果是协议研究基线，尚未实现硬件控制。
