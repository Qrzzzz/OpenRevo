# 0.2.0 关键处理链路

本次分别恢复可读的发送 / 消费链路、重建已验证读取协议的行为模型，并定位核心服务的受保护入口。证据目录为 [processing-chains.json](../protocol/processing-chains.json)，统一索引为 [registry.json](../protocol/registry.json)。二者不表示厂商所有业务处理方法已恢复。

## 可读逻辑：已还原的六处边

| 调用位置 | 已还原行为 | 边界 |
| --- | --- | --- |
| 原托盘 Service_ClientConnectionEvent | connection=true 时构造 GETSUPPORT、GETSTATUS，交给 SendNumbersTopicToServer | 仅证明发送方触发条件 |
| SendNumbersTopicToServer | 命令锁内逐条调用 PublishTopic(command.key, command.value) | 不是硬件控制的互斥锁 |
| 原托盘 PublishTopic | JSON 序列化、ASCII 编码、QoS 2，retain 默认 false | OpenRevo 自身使用实测可用的 QoS 0，不能混同两者 |
| Client_MqttMsgPublishReceived | UTF-8 解码、JSON 对象解析、按主题触发 DataChange | 不订阅会触发原厂界面或进程操作的其他主题 |
| Service_DataChange 的 Fan/Status 分支 | 反序列化 MyRamFan1p5，读取 OperatingMode 等状态 | 消费模型不证明本机每个字段的编码与安全范围 |
| 桥接 PublishTopic 辅助方法 | JSON 序列化、retain=true、QoS 取调用参数 | 不能据此认定全部服务响应都是 retained |

记录保存各源文件摘要、行号和操作名，没有复制完整反编译代码或认证值。可用 `python scripts/recover-processing-chains.py` 对本机静态快照复核；不执行或加载厂商程序集。

## 实机读取路径与独立行为模型

已有本机观察表明：GETSUPPORT 对应能力响应，GETSTATUS 对应模式 / 配置状态，System_ON 后收到监控推送。传感器无统一 request ID 或业务 ACK，GPU / 电池可能不在每次采样中推送；这里的关联来自有限观察，不能推导所有隐藏副作用。

OpenRevo 0.2.0 将这条读取路径实现为：本机身份及文件指纹通过 → loopback 握手 → 七个主题完整订阅 → 两种固定查询 → 实时能力与配置匹配 → 启用共享采样 → 周期查询与独立心跳检查。

能力缓存不能授权；首次实时响应未匹配时关闭连接。已授权后，后续实时响应不匹配立即撤销；能力超过 45 秒未更新也撤销。正常能力续期不重复启用采样；手动刷新只有授权有效时才允许请求采样。撤销后必须建立新会话，旧连接中不能自动恢复授权或重放请求。

PINGRESP 的期限独立于传感器流量：发送 PINGREQ 后，20 秒未收到 PINGRESP 时结束会话。数据新鲜度、能力租约、周期查询和心跳间隔按单调时间计算，UTC 只用于记录接收与操作时间。

## 核心业务处理：仍未恢复的部分

已定位 App 的消息入口，以及 CustomizeCtrl.Receive、MyFanCtrl.Receive、MySystemCtrl.Receive、PublishSupportAll、InitSystemInfo 和 MqttClientCtrl.PublishTopic 等八处目标声明。原始 IL 复核显示，MySystemCtrl.Receive 在 RVA `0x8095e0` 只有 11 字节的抛出异常桩。这不是仅由 C# 反编译器输出失败造成的缺口。

因此不能宣称已经得到“主题 → 分支 → 参数校验 → EC / 固件操作”的完整调用图。模式命令与内部 FAN 枚举名称也不能直接互换。本版本的恢复成果限定为可读的传输 / 消费逻辑、受限读取行为模型及入口证据；受保护业务方法体、模式功耗联动和恢复仍未完成。

下一步针对这些入口研究保护层和实际分支，不试发未知参数，不按公共寄存器地址推断行为。模式切换、风扇曲线、电池阈值、色彩与功耗写入继续禁用。

## TGP 专项

同一 Fan/Status 中目标 140、上限 115 的矛盾尚未解释。静态字段与采样都没有提供足以确认单位、模式关系或编码的证据。0.2.0 保留原始值和矛盾提示，不将二者转换为可写范围，也不声称已消除矛盾。


## 0.3.0 受限模式试点更新

四种模式的已审计语义命令现由独立门控开放；此前仅查询的描述作为历史基线保留。完整条件、实机成功与中止记录、恢复限制见 [0.3.0 审计](v0.3.0-audit.md)。统一目录的 controlRequests 为当前受限入口，历史发送端目录不构成可执行白名单。
