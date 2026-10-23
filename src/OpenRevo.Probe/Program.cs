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
var auditOption = args.Length == 3 ? args[2] : null;
if (args.Length > 3 || auditOption is not (null or "--audit-modes" or "--audit-turbo" or "--audit-custom" or "--audit-fan" or "--audit-fan-modes" or "--check-updates")) throw new ArgumentException("Unknown probe option.");
if (auditOption == "--check-updates")
{
    using var http = new HttpClient();
    var update = await new GitHubUpdates(http).CheckAsync(AppVersion.Current);
    DiagnosticFile.Write(output, JsonSerializer.Serialize(update, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
    Console.WriteLine(JsonSerializer.Serialize(update));
    return 0;
}
var auditModes = auditOption is "--audit-modes" or "--audit-turbo" or "--audit-custom";
var observer = new BridgeObserver(TimeProvider.System, ObserverTiming.Default, auditModes);
ModePilotResult? switchResult = null, restoreResult = null;
observer.Pilot.SetJournalWriter(journal => DiagnosticFile.Write(Path.Combine(Path.GetDirectoryName(output)!, "mode-pilot-journal.json"),
    JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } })));
observer.Pilot.SetFanJournalWriter(journal => DiagnosticFile.Write(Path.Combine(Path.GetDirectoryName(output)!, "fan-journal.json"), JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true })));
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
MonitoringDiagnostic? error = null;
long received = 0;
try
{
    _ = ProtocolEvidence.Registry;
    installation = AuditedInstallation.Inspect(report => inspection = report);
    var monitor = observer.ObserveAsync(installation, sample =>
    {
        Interlocked.Increment(ref received); messages.Enqueue(sample);
        while (messages.Count > 4096) messages.TryDequeue(out _);
    }, Console.WriteLine, stop.Token);
    if (auditOption == "--audit-fan-modes")
    {
        try
        {
            var start = TimeProvider.System.GetTimestamp();
            while (!observer.Pilot.CanControlFan && !monitor.IsCompleted && TimeProvider.System.GetElapsedTime(start) < TimeSpan.FromSeconds(12)) await Task.Delay(100, stop.Token);
            var original = observer.Pilot.CurrentStatus;
            var originalMode = observer.Pilot.CurrentMode;
            if (observer.Pilot.FanBoostEnabled != false || originalMode is null) throw new InvalidOperationException("Audit requires a verified automatic fan baseline.");
            foreach (var mode in new[] { PilotMode.Office, PilotMode.Balanced, PilotMode.Custom })
            {
                var changed = await observer.Pilot.SwitchAsync(mode, stop.Token);
                if (!changed.Succeeded) throw new InvalidOperationException("Audit mode switch failed: " + changed.Detail);
                var before = observer.Pilot.CurrentStatus;
                switchResult = await observer.Pilot.SetFanBoostAsync(true, stop.Token);
                if (!switchResult.Succeeded) throw new InvalidOperationException("Audit fan on failed: " + switchResult.Detail);
                await Task.Delay(8000, stop.Token);
                restoreResult = await observer.Pilot.SetFanBoostAsync(false, stop.Token);
                if (!restoreResult.Succeeded || !ModePilotPolicy.SameConfiguration(before, restoreResult.Snapshot)) throw new InvalidOperationException("Audit fan baseline not restored.");
                Console.WriteLine("Fan roundtrip verified in " + mode);
                await Task.Delay(6000, stop.Token);
            }
            var restored = await observer.Pilot.SwitchAsync((PilotMode)int.Parse(originalMode), stop.Token);
            if (!restored.Succeeded || !ModePilotPolicy.SameConfiguration(original, restored.Snapshot)) throw new InvalidOperationException("Original mode/configuration not restored.");
            observer.Pilot.KeepCurrent();
            Console.WriteLine("Original mode/configuration restored exactly.");
        }
        finally { stop.Cancel(); try { await monitor; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
    }
    else if (auditOption == "--audit-fan")
    {
        try
        {
            var start = TimeProvider.System.GetTimestamp();
            while (!observer.Pilot.CanControlFan && !monitor.IsCompleted && TimeProvider.System.GetElapsedTime(start) < TimeSpan.FromSeconds(12)) await Task.Delay(100, stop.Token);
            var before = observer.Pilot.CurrentStatus;
            if (observer.Pilot.FanBoostEnabled != false) throw new InvalidOperationException("Fan audit requires automatic fan control baseline; no command sent.");
            switchResult = await observer.Pilot.SetFanBoostAsync(true, stop.Token);
            Console.WriteLine("Fan on: " + switchResult.Detail);
            if (!switchResult.Succeeded) throw new InvalidOperationException("Fan boost not verified; check original vendor UI.");
            await Task.Delay(12000, stop.Token);
            restoreResult = await observer.Pilot.SetFanBoostAsync(false, stop.Token);
            Console.WriteLine("Fan auto: " + restoreResult.Detail);
            if (!restoreResult.Succeeded || !ModePilotPolicy.SameConfiguration(before, restoreResult.Snapshot)) throw new InvalidOperationException("Fan baseline was not restored exactly.");
            await Task.Delay(10000, stop.Token);
        }
        finally { stop.Cancel(); try { await monitor; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
    }
    else if (auditModes)
    {
        try
        {
            var start = TimeProvider.System.GetTimestamp();
            while (observer.Phase != MonitorPhase.Monitoring && !monitor.IsCompleted && TimeProvider.System.GetElapsedTime(start) < TimeSpan.FromSeconds(10))
                await Task.Delay(100, stop.Token);
            var current = observer.Pilot.CurrentMode;
            if (current is not ("0" or "1")) throw new InvalidOperationException("Mode audit requires office or balanced baseline; no mode command sent.");
            var target = auditOption switch { "--audit-turbo" => PilotMode.Turbo, "--audit-custom" => PilotMode.Custom, _ => current == "0" ? PilotMode.Balanced : PilotMode.Office };
            switchResult = await observer.Pilot.SwitchAsync(target, stop.Token);
            Console.WriteLine("Switch: " + switchResult.Detail);
            if (!switchResult.Succeeded || observer.Pilot.Journal.Phase != PilotPhase.Applied) throw new InvalidOperationException("Mode switch not verified; no retry or automatic restore.");
            await Task.Delay(1500, stop.Token);
            restoreResult = await observer.Pilot.RestoreAsync(stop.Token);
            Console.WriteLine("Restore: " + restoreResult.Detail);
            if (!restoreResult.Succeeded) throw new InvalidOperationException("Mode restoration not verified; use original vendor UI.");
        }
        finally { stop.Cancel(); try { await monitor; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
    }
    else await monitor;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (Exception ex) { error = observer.LastDiagnostic is { Code: not DiagnosticCode.Cancelled } diagnostic ? diagnostic : MonitoringDiagnostic.From(ex); }
var report = new { schemaVersion = 3, version = AppVersion.Current, observedAtUtc = DateTimeOffset.UtcNow, device = installation?.Device,
    inspection, bridgeSha256 = installation?.BridgeSha256, serviceSha256 = installation?.ServiceSha256, duration, error,
    phase = observer.Phase.ToString(), receivedCount = received, trimmedMessages = received - messages.Count,
    sessionHealth = observer.Health.Snapshot(false),
    modePilot = observer.Pilot.Journal, switchResult, restoreResult,
    fanControl = observer.Pilot.FanJournal,
    requests = observer.Requests, messages = messages.ToArray() };
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
DiagnosticFile.Write(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
Console.WriteLine(JsonSerializer.Serialize(new { messages = messages.Count, requests = observer.Requests.Count, error }));
return error is null ? 0 : 1;
