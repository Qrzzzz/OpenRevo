# 屏幕、风扇、电池与功耗的研究边界

状态：2026-10-18 第一轮静态证据。以下说明已发现的结构及实施前仍需解决的问题，不提供已验证的硬件控制参数。

## 屏幕色彩

代码里至少存在几组不同接口声明和管理类，不能合并成一个亮度或色温开关：

| 路径 | 已见证据 | 未确认内容 |
| --- | --- | --- |
| ICC / WCS 配置 | ColorProfileControl 中的 WcsGet/SetDefaultColorProfile、Associate/Disassociate、Install/Uninstall、校准管理 API 声明 | 本机实际使用哪个 profile、设备匹配、作用域、恢复顺序 |
| Gamma | ColorGamma / MonitorNativeWin32API 中的 Get/SetDeviceGammaRamp 声明 | 生效设备、原始 ramp、与 HDR / 显卡驱动的交互 |
| 全屏颜色矩阵 | MyMagSetFullscreenColorEffect 中的 MagInitialize、MagSetFullscreenColorEffect；25 个 float 的矩阵声明 | 是否为本机当前路径、组合次序、退出时还原方式 |
| 配置与显示模式 | MyDisplay_Manager、DisplayFeatureManager_Intel、ColorControl、DisplayModeParams | 参数单位、范围、默认值、配置持久化与模式选择 |

ColorControl 声明含 profile 名、亮度、色温、RGB、VibrantValue、Constrast、Gamma；DisplayModeParams 中有字符串亮度 / RGB / 色温 / 对比度字段。方法体大量受保护，声明不等于完整实现。

后续应先读取当前显示设备身份、色彩配置和可获取的原始变换；分开保存各路径状态，避免把 identity gamma / 通用 ICC 当成“恢复默认”。还需要确认多显示器、HDR、休眠、显卡切换及厂商后台的重新应用行为。当前不安装或切换 ICC，不修改 gamma、矩阵或显示模式。

来源：`.local/decompiled/GCUService/MyControlCenter.MySetting.ColorCalibration/ColorProfileControl.cs`、`MyControlCenter.MySetting.DisplayFeature/ColorGamma.cs`、`GCUService.MySetting/MyMagSetFullscreenColorEffect.cs`、`GCUService.MySetting/ColorControl.cs`。

## 风扇

已见 FanModel、RamFan1 / 1p5 / 2、多种 CPU / GPU 分支、独立 CPU/GPU 风扇表与 SafetyProtectionUpdate 等方法声明。存在声明不能证明温控或失效保护实际如何运作。

风扇表声明：

- FanTable1p5Buffer：`ID:uint`、`UpT:byte`、`DownT:byte`、`Duty:byte`。
- FanTable2Buffer：`ID:uint`、`CpuUpT:byte`、`CpuDownT:byte`、`Duty:byte`、`GpuUpT:byte`、`GpuDownT:byte`。
- CustomizeTable：CPU/GPU 曲线、独立控制标记，以及 PL1/PL2、AC/DC 数值、CTGP/DB/WM 等字段。

这些是托管字段声明，不证明内存打包、网络序列化、温度或 Duty 单位。UpT / DownT 提示可能存在升温降温滞回，但算法还没有恢复。

### 已发现的地址歧义

公共 ECSpec 同时声明：

| 地址 | 声明 A | 声明 B |
| --- | --- | --- |
| 1859 / 0x743 | ADDR_MYFAN2_L1_PWM | ADDR_ConfigurableTGP_DynamicBoost_CTRL_BYTE |
| 1860 / 0x744 | ADDR_MYFAN2_L2_PWM | ADDR_ConfigurableTGP_VALUE |
| 1861 / 0x745 | ADDR_MYFAN2_L3_PWM | ADDR_DynamicBoost_TotalProcessingPowerTarget_VALUE |
| 1862 / 0x746 | ADDR_MYFAN2_L4_PWM | ADDR_DynamicBoost_MaxinumTGP_VALUE |

RamFan1p5_ECSpec 和 RamFan2_ECSpec 的风扇表基址也存在不同布局，例如十进制 3920 在两个声明中含义不同。由此可以直接确定：**不能拿公共常量表给本机盲写，甚至不能仅靠寄存器名字解释读取值。** 必须先确认机型、固件、支持位和实际代码分支。

实现前需要明确曲线点数、完整范围、滞回、最小散热能力、失败时硬件接管、原后台是否周期覆盖、睡眠恢复、自动模式恢复和并发排他。先保留原服务，由语义接口管理已验证的模式，比直接按公共地址写曲线更容易建立证据，但仍需分别核清模式切换的功耗联动。

## 电池

代码中至少有两代不同管理结构：

- BatteryProtection：健康保护、自动校正、阶段状态、通知与持久化字段。
- BatteryProtection2：PERFORMANCEDMODE / BALANCEDMODE / HEALTHYMODE，以及 GET、CHARGING_UP_LIMIT、CHARGING_DOWN_LIMIT、RECOVERY、Type-C 电源优先级开关等枚举项。
- ECSpec 声明 `ADDR_BATTERY_CHARGE_LIMIT_UP=1977`、`ADDR_BATTERY_CHARGE_LIMIT_DOWN=2000`。

枚举项是内部声明，不能直接当作 MQTT 的 Action 字符串。上述地址没有完成本机适用性验证，也没有恢复默认上下限或校验关系。

后续要区分充电阈值、主动校正 / 放电流程、健康模式和 Type-C 电源策略。不能把“恢复”或“校正”当成单纯清理配置；两代实现的状态机不同，需确认本机实际使用哪一代。首阶段仅观察电量、供电与健康信息；改变阈值前要保留原值，并确认上下限关系与跨重启持久化。

来源：`.local/decompiled/GCUService/GCUService.MySystem/BatteryProtection.cs`、`BatteryProtection2.cs`、`Define/ECSpec.cs`、`BatteryModel/`。

## CPU / GPU 功耗

SMAPCTABLE_STRUCT 声明了 PL1、PL2、PL4、TccOffset、ConfigurableTGP、DynamicBoost、TargetTemperature、DefaultTGP、DynamicBoostCpuTdp，字段类型为 byte。原托盘状态模型还有电压、偏移、温度和 AMD 相关功耗字段。

这说明必须区分持续 / 短时功耗、温度 / 电流约束、CPU / GPU 分配、AC / DC 和性能模式。byte 只给出存储类型，不能推导安全范围或物理单位；范围应来自本机能力和真实处理代码，不能统一钳制到 0–255 后就发送。

恢复写路径前需明确单位和编码、合法上下限、模式切换顺序、CPU/GPU 联动、原服务覆盖、失败回读与恢复。偏移电压和超频先保持禁用；模式切换也不能按“纯风扇操作”实现。

## 原生接口与驱动

ACPIDriverDll 的 19 个导出已与 AcpiCtrl 常量核对，完整数据在 [native-ioctls.json](../protocol/native-ioctls.json)。例：

| 导出 | 静态调用点 IOCTL |
| --- | --- |
| ReadEC | 0x9c40a488 |
| WriteEC | 0x9c40a48c |
| SMAPCTable | 0x9c40a500 |

DLL 字符串中存在 `\\.\ACPIDriver`，驱动中存在 `\DosDevices\ACPIDriver`。ReadEC 导出 RVA 为 0x3410：静态代码以读写访问标志打开设备，调用 DeviceIoControl，并从共享缓冲读取返回字节。其错误分支可返回 0，调用点没有把 DeviceIoControl 成败转换成独立的对外状态。这不能作为健壮读取接口直接复用。

驱动内部请求分派、缓冲区 ABI、同步、超时、固件语义、支持条件均未完整恢复。即使导出名为 Read，也不在当前阶段加载 DLL 试调；设备的读写访问标志也不等于本轮已进行写操作。

## 实现顺序

1. 继续恢复静态处理逻辑，补齐本机能力与分支身份；仅声明或字符串证据的功能保持未支持。
2. 建立独立的状态观察流程，输出原始值和证据来源，保留未知字段与不确定单位。
3. 优先验证已知语义的厂商服务接口。新实现与原后台之间明确控制权，避免同时写同一硬件。
4. 每个控制功能单独完成参数边界、联动、回读、失败与恢复验证，再开放该能力。
5. 只有需要完全移除原服务、且硬件协议与恢复行为已足够清楚时，才实现直接硬件后端。

当前没有可以安全启用的控制实现。未知项明确保留为未知，不用通用默认值或其他机型地址补齐。


## 0.3.0 受限模式试点更新

四种模式的已审计语义命令现由独立门控开放；此前仅查询的描述作为历史基线保留。完整条件、实机成功与中止记录、恢复限制见 [0.3.0 审计](v0.3.0-audit.md)。统一目录的 controlRequests 为当前受限入口，历史发送端目录不构成可执行白名单。
