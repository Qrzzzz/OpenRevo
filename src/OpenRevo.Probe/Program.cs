using System.Collections.Concurrent;
using System.Text.Json;
using OpenRevo.Core;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
var duration = args.Length > 0 ? int.Parse(args[0]) : 15;
if (duration is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(duration));
var output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath(".local/evidence/csharp-observation.json");
var installation = AuditedInstallation.Inspect();
var messages = new ConcurrentQueue<TopicSample>();
var observer = new BridgeObserver();
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
string? error = null;
try { await observer.ObserveAsync(installation, messages.Enqueue, Console.WriteLine, stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (Exception ex) { error = ex.Message; }
var report = new { observedAtUtc = DateTimeOffset.UtcNow, device = installation.Device,
    installation.BridgeSha256, installation.ServiceSha256, duration, error,
    requests = observer.Requests, messages = messages.ToArray() };
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { messages = messages.Count, requests = observer.Requests.Count, error }));
return error is null ? 0 : 1;
