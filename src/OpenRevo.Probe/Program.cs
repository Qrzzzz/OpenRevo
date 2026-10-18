using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenRevo.Core;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
var duration = args.Length > 0 ? int.Parse(args[0]) : 15;
if (duration is < 1 or > 1800) throw new ArgumentOutOfRangeException(nameof(duration));
var output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath(".local/evidence/csharp-observation.json");
BridgeInstallation? installation = null;
InstallationReport? inspection = null;
var messages = new ConcurrentQueue<TopicSample>();
var observer = new BridgeObserver();
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
MonitoringDiagnostic? error = null;
long received = 0;
try
{
    _ = ProtocolEvidence.Registry;
    installation = AuditedInstallation.Inspect(report => inspection = report);
    await observer.ObserveAsync(installation, sample =>
    {
        Interlocked.Increment(ref received); messages.Enqueue(sample);
        while (messages.Count > 4096) messages.TryDequeue(out _);
    }, Console.WriteLine, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (Exception ex) { error = observer.LastDiagnostic ?? MonitoringDiagnostic.From(ex); }
var report = new { schemaVersion = 2, version = AppVersion.Current, observedAtUtc = DateTimeOffset.UtcNow, device = installation?.Device,
    inspection, bridgeSha256 = installation?.BridgeSha256, serviceSha256 = installation?.ServiceSha256, duration, error,
    phase = observer.Phase.ToString(), receivedCount = received, droppedMessages = received - messages.Count,
    requests = observer.Requests, messages = messages.ToArray() };
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
DiagnosticFile.Write(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
Console.WriteLine(JsonSerializer.Serialize(new { messages = messages.Count, requests = observer.Requests.Count, error }));
return error is null ? 0 : 1;
