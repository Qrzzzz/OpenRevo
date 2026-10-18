using System.Net.Sockets;
using System.Text.Json;

namespace OpenRevo.Core;

public enum DiagnosticCode
{
    UnsupportedDevice, InstallationNotFound, ServiceVersionMismatch, CredentialFormat,
    InstallationAccessDenied, ServiceUnavailable, AuthenticationRejected, SubscriptionRejected,
    CapabilityRevoked, CapabilityExpired, ResponseTimeout, ConnectionClosed, ProtocolViolation,
    SlotOccupied, Cancelled, ExportFailed, UnexpectedFailure
}

public sealed record MonitoringDiagnostic(DiagnosticCode Code, string Message, string NextStep, DateTimeOffset AtUtc)
{
    public static MonitoringDiagnostic Create(DiagnosticCode code) => new(code, code switch
    {
        DiagnosticCode.UnsupportedDevice => "本机型号、主板或 BIOS 尚未通过审计。",
        DiagnosticCode.InstallationNotFound => "未找到已审计的厂商服务安装。",
        DiagnosticCode.ServiceVersionMismatch => "厂商服务文件与已审计版本不匹配。",
        DiagnosticCode.CredentialFormat => "无法识别已审计安装的认证格式。",
        DiagnosticCode.InstallationAccessDenied => "无法读取本机安装信息或占用锁。",
        DiagnosticCode.ServiceUnavailable => "本机厂商服务不可用或连接失败。",
        DiagnosticCode.AuthenticationRejected => "厂商桥接服务拒绝认证或会话参数。",
        DiagnosticCode.SubscriptionRejected => "厂商服务未完整接受状态订阅。",
        DiagnosticCode.CapabilityRevoked => "最新能力响应已不符合本机监控条件，已停止本次连接。",
        DiagnosticCode.CapabilityExpired => "能力响应已过期，已停止本次连接。",
        DiagnosticCode.ResponseTimeout => "等待厂商服务响应超时。",
        DiagnosticCode.ConnectionClosed => "厂商服务已关闭连接。",
        DiagnosticCode.ProtocolViolation => "收到不符合已审计协议的数据，已停止读取。",
        DiagnosticCode.SlotOccupied => "已有 OpenRevo 会话占用本机连接。",
        DiagnosticCode.Cancelled => "本次监控已结束。",
        DiagnosticCode.ExportFailed => "无法保存本地诊断文件。",
        _ => "监控遇到未分类错误。"
    }, code switch
    {
        DiagnosticCode.UnsupportedDevice or DiagnosticCode.ServiceVersionMismatch or DiagnosticCode.CredentialFormat => "保留诊断，核对机型与安装版本后重新审计。",
        DiagnosticCode.SlotOccupied => "先断开另一个 OpenRevo 窗口或读取器，再重新连接。",
        DiagnosticCode.CapabilityRevoked or DiagnosticCode.CapabilityExpired => "核对厂商后台状态；确认后手动重新连接。",
        DiagnosticCode.ExportFailed => "选择可写目录，或查看连接与诊断页中的保存结果。",
        DiagnosticCode.Cancelled => "需要时手动重新连接。",
        _ => "检查本机厂商服务和诊断记录，再手动重新连接。"
    }, DateTimeOffset.UtcNow);

    public static MonitoringDiagnostic From(Exception exception) => Create(exception switch
    {
        MonitoringException known => known.Code,
        UnauthorizedAccessException or System.Security.SecurityException => DiagnosticCode.InstallationAccessDenied,
        SocketException => DiagnosticCode.ServiceUnavailable,
        EndOfStreamException => DiagnosticCode.ConnectionClosed,
        OperationCanceledException => DiagnosticCode.ResponseTimeout,
        InvalidDataException or JsonException or System.Text.DecoderFallbackException => DiagnosticCode.ProtocolViolation,
        IOException => DiagnosticCode.ConnectionClosed,
        _ => DiagnosticCode.UnexpectedFailure
    });
}

public sealed class MonitoringException(DiagnosticCode code) : InvalidOperationException(MonitoringDiagnostic.Create(code).Message)
{
    public DiagnosticCode Code { get; } = code;
}
