using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenRevo.Core;
using OpenRevo.App;

var cases = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => cases.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => cases.Add((name, run));
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Reject<T>(Action run) where T : Exception
{ try { run(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
async Task RejectAsync<T>(Func<Task> run) where T : Exception
{ try { await run(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
TopicSample Sample(string topic, string json, bool retained = false) => new(topic, JsonDocument.Parse(json).RootElement.Clone(), DateTimeOffset.UtcNow, retained);
Packet Publish(string topic, string json, byte header = 0x30)
{
    var name = Encoding.UTF8.GetBytes(topic);
    var body = new byte[2 + name.Length + Encoding.UTF8.GetByteCount(json)];
    BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)name.Length); name.CopyTo(body, 2);
    Encoding.UTF8.GetBytes(json).CopyTo(body, 2 + name.Length);
    return new Packet(header, body);
}
var supportJson = """{"ProjectID":26,"CustomizeTarget":56,"BIOS_PROJECT_ID":"IDY","SystemMonitorSupport":1,"ServiceReady":1,"IsAMDPlatform":0,"IsNvGpu":1,"ColorCalibrationSupport":4,"LiquidCoolingSupport":0}""";

Test("UI bursts keep only the latest seven topic snapshots without losing message counts", () =>
{
    var buffer = new UiSampleBuffer();
    for (var i = 0; i < 10000; i++)
        foreach (var topic in ReadProtocol.Topics) buffer.Offer(Sample(topic, $"{{\"sequence\":{i}}}"));
    buffer.Offer(Sample("Unknown/Status", "{}"));
    var batch = buffer.Drain();
    Check(batch.Received == 10000L * ReadProtocol.Topics.Count && batch.Samples.Count == ReadProtocol.Topics.Count);
    Check(batch.Samples.All(s => s.Number("sequence") == 9999));
    Check(buffer.Drain() is { Received: 0, Samples.Count: 0 });
});
Test("visual delivery preserves source timestamps and retained state and isolates reconnections", () =>
{
    var oldConnection = new UiSampleBuffer(); var newConnection = new UiSampleBuffer();
    var cached = Sample("Fan/Status", "{}", true) with { ReceivedTimestamp = 900 };
    newConnection.Offer(cached);
    oldConnection.Offer(Sample("Fan/Status", """{"OperatingMode":2}"""));
    var batch = newConnection.Drain();
    Check(batch.Received == 1 && batch.Samples.Single() == cached);
    Check(batch.Samples.Single().Retained && batch.Samples.Single().ReceivedTimestamp == 900);
    Check(oldConnection.Drain().Received == 1 && newConnection.Drain().Received == 0);
});
AsyncTest("concurrent visual producers and draining preserve complete accounting", async () =>
{
    var buffer = new UiSampleBuffer();
    var sample = Sample("System/CpuInfo", """{"Temperature":65}""");
    var producers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        for (var i = 0; i < 5000; i++) buffer.Offer(sample);
    })).ToArray();
    var writing = Task.WhenAll(producers); long received = 0;
    while (!writing.IsCompleted)
    {
        var batch = buffer.Drain(); received += batch.Received;
        Check(batch.Samples.Count <= 1);
        await Task.Yield();
    }
    await writing; received += buffer.Drain().Received;
    Check(received == 20000 && buffer.Drain().Received == 0);
});

Test("UI preferences survive restart independently of device configuration", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "OpenRevo-preferences-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "preferences.json");
    try
    {
        Check(AppPreferences.Load(path) == new AppPreferences());
        var settings = new AppPreferences(AppThemePreference.Dark, new WindowBounds(-1800, 40, 1000, 700), true);
        settings.Save(path);
        Check(AppPreferences.Load(path) == settings);
        (settings with { Theme = AppThemePreference.Light }).Save(path);
        Check(AppPreferences.Load(path).Theme == AppThemePreference.Light);
        Check(Directory.GetFiles(directory).Length == 1, "Preference writes must leave no temporary files");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
});
Test("damaged or unknown UI preferences do not prevent startup", () =>
{
    var path = Path.GetTempFileName();
    try
    {
        foreach (var json in new[] { "{", "null", "{\"Theme\":\"FutureTheme\"}" })
        {
            File.WriteAllText(path, json);
            Check(AppPreferences.Load(path) == new AppPreferences());
            Check(File.ReadAllText(path) == json, "Loading must not overwrite the original settings");
        }
        File.WriteAllText(path, """{"Theme":999,"Window":{"X":0,"Y":0,"Width":-1,"Height":0},"Extra":true}""");
        Check(AppPreferences.Load(path) == new AppPreferences());
    }
    finally { File.Delete(path); }
});
Test("unwritable preference destination reports failure without replacing existing data", () =>
{
    var path = Path.GetTempFileName();
    try
    {
        File.WriteAllText(path, "keep existing file");
        Reject<IOException>(() => new AppPreferences().Save(Path.Combine(path, "preferences.json")));
        Check(File.ReadAllText(path) == "keep existing file");
    }
    finally { File.Delete(path); }
});
Test("window restoration keeps a negative-coordinate monitor and fits changed work areas", () =>
{
    var area = new WindowBounds(-1920, 0, 1920, 1040);
    var saved = new WindowBounds(-1800, 50, 1100, 800);
    Check(WindowBounds.Fit(saved, area, 1) == saved);
    Check(WindowBounds.Fit(new WindowBounds(-150, 900, 1100, 800), area, 1) == new WindowBounds(-1100, 240, 1100, 800));
});
Test("removed monitors recenter the window and oversized windows fit the screen", () =>
{
    var area = new WindowBounds(0, 0, 1920, 1040);
    Check(WindowBounds.Fit(new WindowBounds(-1800, 50, 1000, 700), area, 1) == new WindowBounds(460, 170, 1000, 700));
    Check(WindowBounds.Fit(new WindowBounds(20, 20, 4000, 3000), area, 1) == area);
});
Test("window minimums account for scale without exceeding small work areas", () =>
{
    Check(WindowBounds.Fit(new WindowBounds(0, 0, 200, 200), new WindowBounds(0, 0, 1920, 1040), 1.5)
        == new WindowBounds(0, 0, 1080, 660));
    Check(WindowBounds.Fit(null, new WindowBounds(0, 0, 600, 360), 2) == new WindowBounds(0, 0, 600, 360));
});

Test("only the audited board and BIOS pass the device gate", () =>
{
    var known = new DeviceIdentity("MECHREVO", "YAOSHI Series", "YAOSHI Series-X6AR55xY", "N.1.18MRO24");
    AuditedProfile.ValidateDevice(known);
    Reject<InvalidOperationException>(() => AuditedProfile.ValidateDevice(known with { Board = "OTHER" }));
    Reject<InvalidOperationException>(() => AuditedProfile.ValidateDevice(known with { Bios = "NEW" }));
});
Test("unknown or changed service file identities fail closed", () =>
{
    AuditedProfile.ValidateFiles(AuditedProfile.BridgeHash, AuditedProfile.ServiceHash);
    Reject<InvalidOperationException>(() => AuditedProfile.ValidateFiles(new string('0', 64), AuditedProfile.ServiceHash));
    Reject<InvalidOperationException>(() => AuditedProfile.ValidateFiles(AuditedProfile.BridgeHash, new string('0', 64)));
});
Test("installation serialization never exposes derived credentials", () =>
{
    var installation = new BridgeInstallation { Device = new("MECHREVO", "test", "test", "test"), BridgePath = "test.exe",
        BridgeSha256 = AuditedProfile.BridgeHash, ServiceSha256 = AuditedProfile.ServiceHash, Password = "private-test-password" };
    var json = JsonSerializer.Serialize(installation);
    Check(!json.Contains("private-test-password") && !json.Contains("Password"));
});

Test("only three read requests; invalid enum cannot encode publish", () =>
{
    Check(Enum.GetValues<ReadRequest>().Length == 3);
    foreach (var r in Enum.GetValues<ReadRequest>())
    {
        var encoded = MqttWire.Query(r);
        Check(encoded[0] == 0x30, "Queries must be QoS 0 and non-retained");
        Check(!Encoding.UTF8.GetString(encoded).Contains("System_OFF"));
    }
    Reject<ArgumentOutOfRangeException>(() => MqttWire.Query((ReadRequest)99));
});
Test("no wildcard or command subscriptions", () =>
{
    Check(ReadProtocol.Topics.Count == 7);
    Check(ReadProtocol.Topics.All(t => !t.Contains('#') && !t.Contains('+') && !t.EndsWith("/Control")));
});
Test("CONNECT clean session, no will, separate plugin slot", () =>
{
    var connect = MqttWire.Connect("test-password");
    // Remaining length is under 128; variable header starts at offset 2.
    Check(connect[0] == 0x10 && connect[8] == 4 && connect[9] == 0xc2);
    Check(Encoding.UTF8.GetString(connect).Contains("PluginClient_19"));
    Check(!Encoding.UTF8.GetString(connect).Contains("UWPClient_10"));
});
Test("MQTT UTF-8 string length uses bytes", () =>
{
    var text = MqttWire.String("温度"); Check(text[0] == 0 && text[1] == 6 && text.Length == 8);
    Reject<InvalidDataException>(() => MqttWire.String("bad\0topic"));
});
AsyncTest("fragmented network packets preserve frame boundaries", async () =>
{
    await using var stream = new FragmentedStream([0x30, 0x05, 0x00, 0x01, 0x61, 0x7b, 0x7d, 0xd0, 0x00]);
    var first = await MqttWire.ReadAsync(stream, default); var second = await MqttWire.ReadAsync(stream, default);
    Check(first.Header == 0x30 && first.Body.Length == 5 && second.Header == 0xd0 && second.Body.Length == 0);
});
AsyncTest("truncated frame fails instead of returning partial data", async () =>
    await RejectAsync<EndOfStreamException>(() => MqttWire.ReadAsync(new MemoryStream([0x30, 5, 0]), default)));
AsyncTest("oversized incoming packet rejected before allocation", async () =>
    await RejectAsync<InvalidDataException>(() => MqttWire.ReadAsync(new MemoryStream([0x30, 0x81, 0x80, 0x40]), default)));
AsyncTest("noncanonical remaining length rejected", async () =>
    await RejectAsync<InvalidDataException>(() => MqttWire.ReadAsync(new MemoryStream([0x30, 0x80, 0]), default)));
Test("oversized outgoing packet rejected", () => Reject<InvalidDataException>(() => MqttWire.Frame(0x30, new byte[MqttWire.MaximumPacketBytes + 1])));
Test("unsubscribed topics cannot enter device state", () => Reject<InvalidDataException>(() => MqttWire.ParsePublish(Publish("EC/Status", "{}"), DateTimeOffset.UtcNow)));
Test("unexpected publish QoS rejected", () => Reject<InvalidDataException>(() => MqttWire.ParsePublish(Publish("Fan/Status", "{}", 0x32), DateTimeOffset.UtcNow)));
Test("non-object and malformed JSON rejected", () =>
{
    Reject<InvalidDataException>(() => MqttWire.ParsePublish(Publish("Fan/Status", "[]"), DateTimeOffset.UtcNow));
    Reject<JsonException>(() => MqttWire.ParsePublish(Publish("Fan/Status", "{"), DateTimeOffset.UtcNow));
});
Test("numeric strings and JSON numbers retain their source representation", () =>
{
    var sample = MqttWire.ParsePublish(Publish("System/FanInfo", """{"CpuFanRpm":2147,"CpuFanDuty":"38","Unknown":{"a":1}}"""), DateTimeOffset.UtcNow);
    Check(sample.Number("CpuFanRpm") == 2147 && sample.Number("CpuFanDuty") == 38);
    Check(sample.Payload.GetProperty("Unknown").GetProperty("a").GetInt32() == 1);
    Check(sample.Payload.GetProperty("CpuFanDuty").ValueKind == JsonValueKind.String);
});
Test("missing and non-finite values remain unknown", () =>
{
    var sample = Sample("Fan/Status", """{"CPU_PL1":"NaN","CPU_PL2":"Infinity","Null":null}""");
    Check(sample.Number("CPU_PL1") is null && sample.Number("CPU_PL2") is null && sample.Number("Missing") is null && sample.Text("Null") is null);
});
Test("retained response cannot enable telemetry", () => Check(!DeviceAudit.MatchesAuditedTelemetry(Sample("Customize/SupportInfo", supportJson, true))));
Test("fresh audited capability response can enable telemetry", () => Check(DeviceAudit.MatchesAuditedTelemetry(Sample("Customize/SupportInfo", supportJson))));
Test("unknown model, missing support or service not ready cannot enable telemetry", () =>
{
    Check(!DeviceAudit.MatchesAuditedTelemetry(Sample("Customize/SupportInfo", supportJson.Replace("\"IDY\"", "\"OTHER\""))));
    Check(!DeviceAudit.MatchesAuditedTelemetry(Sample("Customize/SupportInfo", supportJson.Replace("\"ServiceReady\":1", "\"ServiceReady\":0"))));
    Check(!DeviceAudit.MatchesAuditedTelemetry(Sample("Customize/SupportInfo", "{}")));
});
Test("capability declaration stays separate from MVP availability", () =>
{
    var caps = DeviceAudit.Capabilities(Sample("Customize/SupportInfo", supportJson));
    Check(caps.Single(c => c.Field == "ColorCalibrationSupport").Availability == "待审计");
    Check(caps.Single(c => c.Field == "LiquidCoolingSupport").DeclaredSupported == false);
    Check(DeviceAudit.Capabilities(null).Single(c => c.Field == "SystemMonitorSupport").Availability == "待确认");
});
Test("retained state never becomes live because time passes", () =>
{
    var clock = new TestClock(); var sample = Sample("Fan/Status", "{}", true) with { ReceivedTimestamp = clock.GetTimestamp() };
    Check(sample.Freshness(clock, true) == SampleFreshness.Cached);
    clock.Advance(TimeSpan.FromMinutes(3)); Check(sample.Freshness(clock, true) == SampleFreshness.Cached);
});
Test("stale and disconnected states are distinct", () =>
{
    var clock = new TestClock(); var sample = Sample("Fan/Status", "{}") with { ReceivedTimestamp = clock.GetTimestamp() };
    clock.Advance(TimeSpan.FromSeconds(2)); Check(sample.Freshness(clock, true) == SampleFreshness.Current);
    clock.Advance(TimeSpan.FromSeconds(14)); Check(sample.Freshness(clock, true) == SampleFreshness.Stale);
    Check(sample.Freshness(clock, false) == SampleFreshness.Disconnected);
});
Test("observed TGP target 140 maximum 115 is preserved as an inconsistency", () =>
{
    var sample = Sample("Fan/Status", """{"GPU_ConfigurableTGPTarget":"140","GPU_ConfigurableTGPMaximum":"115"}""");
    Check(DeviceAudit.TgpInconsistency(sample)?.Contains("140") == true);
    Check(sample.Text("GPU_ConfigurableTGPTarget") == "140");
    Check(DeviceAudit.TgpInconsistency(Sample("Fan/Status", "{}")) is null);
});
Test("unknown operating mode is not silently mapped", () => Check(DeviceAudit.ModeName("42").Contains("42") && DeviceAudit.ModeName(null) == "等待状态"));

AsyncTest("loopback session sends only audited queries and receives status before peer close", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var receivedQueries = new List<string>(); var receivedSamples = new List<TopicSample>();
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(stop.Token); var stream = peer.GetStream();
        var connect = await MqttWire.ReadAsync(stream, stop.Token); Check(connect.Header == 0x10);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, stop.Token);
        var subscription = await MqttWire.ReadAsync(stream, stop.Token); Check(subscription.Header == 0x82);
        await stream.WriteAsync(new byte[] { 0x90, 9, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, stop.Token);
        for (var i = 0; i < 2; i++)
        {
            var query = await MqttWire.ReadAsync(stream, stop.Token); Check(query.Header == 0x30);
            var size = BinaryPrimitives.ReadUInt16BigEndian(query.Body);
            receivedQueries.Add(Encoding.UTF8.GetString(query.Body, 2 + size, query.Body.Length - 2 - size));
        }
        var retained = Publish("Customize/SupportInfo", supportJson, 0x31);
        await stream.WriteAsync(MqttWire.Frame(retained.Header, retained.Body), stop.Token);
        var live = Publish("Customize/SupportInfo", supportJson);
        await stream.WriteAsync(MqttWire.Frame(live.Header, live.Body), stop.Token);
        var telemetry = await MqttWire.ReadAsync(stream, stop.Token);
        Check(telemetry.Header == 0x30); var length = BinaryPrimitives.ReadUInt16BigEndian(telemetry.Body);
        receivedQueries.Add(Encoding.UTF8.GetString(telemetry.Body, 2 + length, telemetry.Body.Length - 2 - length));
        var sensor = Publish("System/CpuInfo", """{"CpuTemperature":"58","CpuUsage":"7"}""");
        await stream.WriteAsync(MqttWire.Frame(sensor.Header, sensor.Body), stop.Token);
        // Allow the client to consume the last complete frame before the peer closes.
        await Task.Delay(50, stop.Token);
    });
    var observer = new BridgeObserver();
    await RejectAsync<EndOfStreamException>(() => observer.ObserveAtAsync("test-password", endpoint, receivedSamples.Add, _ => { }, stop.Token, false));
    await server;
    Check(receivedQueries.Count == 3 && receivedQueries[0].Contains("GETSUPPORT") && receivedQueries[1].Contains("GETSTATUS") && receivedQueries[2].Contains("System_ON"));
    Check(receivedSamples.Count == 3 && receivedSamples[^1].Number("CpuTemperature") == 58);
    Check(observer.Requests.Count == 3 && !JsonSerializer.Serialize(observer.Requests).Contains("test-password"));
    Check(observer.Health.Snapshot(false).StopReason == DiagnosticCode.ConnectionClosed && observer.Health.Snapshot(false).SamplingRequests == 1);
});
AsyncTest("rejected authentication does not subscribe or publish", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(stop.Token); var stream = peer.GetStream();
        _ = await MqttWire.ReadAsync(stream, stop.Token);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 4 }, stop.Token);
    });
    var observer = new BridgeObserver();
    await RejectAsync<MonitoringException>(() => observer.ObserveAtAsync("test", (IPEndPoint)listener.LocalEndpoint, _ => { }, _ => { }, stop.Token, false));
    await server; Check(observer.Requests.Count == 0 && observer.LastDiagnostic?.Code == DiagnosticCode.AuthenticationRejected);
});
AsyncTest("remote host is rejected before any socket connection", async () =>
    await RejectAsync<InvalidOperationException>(() => new BridgeObserver().ObserveAtAsync("test", new IPEndPoint(IPAddress.Parse("192.0.2.1"), 13688), _ => { }, _ => { }, default, false)));
AsyncTest("cancellation terminates an idle established session promptly", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var cancelClient = new CancellationTokenSource();
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(stop.Token); var stream = peer.GetStream();
        _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, stop.Token);
        _ = await MqttWire.ReadAsync(stream, stop.Token);
        await stream.WriteAsync(new byte[] { 0x90, 9, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, stop.Token);
        for (var i = 0; i < 2; i++) _ = await MqttWire.ReadAsync(stream, stop.Token);
        cancelClient.Cancel();
        var disconnect = await MqttWire.ReadAsync(stream, stop.Token); Check(disconnect.Header == 0xe0 && disconnect.Body.Length == 0);
    });
    await RejectAsync<OperationCanceledException>(() => new BridgeObserver().ObserveAtAsync("test", (IPEndPoint)listener.LocalEndpoint, _ => { }, _ => { }, cancelClient.Token, false));
    await server;
});

Test("clock corrections do not rejuvenate samples or capability leases", () =>
{
    var clock = new TestClock(); var sample = Sample("Customize/SupportInfo", supportJson) with { ReceivedTimestamp = clock.GetTimestamp() };
    var lease = new TelemetryLease(clock, TimeSpan.FromSeconds(45)); Check(lease.Observe(sample));
    clock.WallClock = clock.WallClock.AddYears(20);
    Check(sample.Freshness(clock, true) == SampleFreshness.Current && lease.Demand());
    clock.Advance(TimeSpan.FromSeconds(46)); clock.WallClock = DateTimeOffset.UnixEpoch.AddYears(-20);
    Check(sample.Freshness(clock, true) == SampleFreshness.Stale);
    Reject<MonitoringException>(() => lease.Demand());
});
Test("retained capabilities cannot grant or extend a monitoring lease", () =>
{
    var clock = new TestClock(); var lease = new TelemetryLease(clock, TimeSpan.FromSeconds(45));
    var sample = Sample("Customize/SupportInfo", supportJson) with { ReceivedTimestamp = clock.GetTimestamp() };
    Check(!lease.Observe(sample with { Retained = true }) && !lease.Demand());
    Check(lease.Observe(sample)); clock.Advance(TimeSpan.FromSeconds(40));
    Check(!lease.Observe(sample with { Retained = true, ReceivedTimestamp = clock.GetTimestamp() }));
    clock.Advance(TimeSpan.FromSeconds(6)); Reject<MonitoringException>(() => lease.Demand());
});
Test("live capability renewal extends lease without repeating initial enable", () =>
{
    var clock = new TestClock(); var lease = new TelemetryLease(clock, TimeSpan.FromSeconds(45));
    var sample = Sample("Customize/SupportInfo", supportJson) with { ReceivedTimestamp = clock.GetTimestamp() };
    Check(lease.Observe(sample)); clock.Advance(TimeSpan.FromSeconds(40));
    Check(!lease.Observe(sample with { ReceivedTimestamp = clock.GetTimestamp() }));
    clock.Advance(TimeSpan.FromSeconds(40)); Check(lease.Demand());
});
Test("capability downgrade revokes grant and cannot silently reauthorize the same session", () =>
{
    var clock = new TestClock(); var lease = new TelemetryLease(clock, TimeSpan.FromSeconds(45));
    var sample = Sample("Customize/SupportInfo", supportJson) with { ReceivedTimestamp = clock.GetTimestamp() };
    lease.Observe(sample);
    Reject<MonitoringException>(() => lease.Observe(Sample("Customize/SupportInfo", supportJson.Replace("\"ServiceReady\":1", "\"ServiceReady\":0"))));
    Reject<MonitoringException>(() => lease.Demand()); Reject<MonitoringException>(() => lease.Observe(sample));
});
Test("unknown boolean capability encodings do not become supported", () =>
{
    var sample = Sample("Customize/SupportInfo", """{"FanSettingsSupport":4,"ColorCalibrationSupport":4}""");
    var caps = DeviceAudit.Capabilities(sample);
    Check(caps.Single(c => c.Field == "FanSettingsSupport").DeclaredSupported is null);
    Check(caps.Single(c => c.Field == "ColorCalibrationSupport").DeclaredSupported == true);
    Check(DeviceAudit.Capabilities(sample, false).All(c => c.Availability == "历史声明"));
});
Test("model identity is independently checked alongside board and BIOS", () =>
{
    var profile = AuditedProfile.Current;
    Check(profile.CapabilityIdentity["BIOS_PROJECT_ID"] == "IDY" && profile.Bridge.Bytes == 2125496);
    Reject<MonitoringException>(() => AuditedProfile.ValidateDevice(new(profile.Manufacturer, "other model", profile.Board, profile.Bios)));
});
Test("embedded evidence matches the closed runtime allowlist and preserves unknown handlers", () =>
{
    var registry = ProtocolEvidence.Registry;
    Check(registry.GetProperty("requests").EnumerateArray().Count(r => r.GetProperty("enabled").GetBoolean()) == 3);
    Check(registry.GetProperty("requests").EnumerateArray().Count(r => !r.GetProperty("enabled").GetBoolean()) == 4);
    Check(registry.GetProperty("requests").EnumerateArray().All(r => !r.GetProperty("handlerBodyRecovered").GetBoolean()));
    Check(registry.GetProperty("subscriptions").GetArrayLength() == 7);
});
Test("typed diagnostics do not serialize original exception secrets", () =>
{
    foreach (var exception in new Exception[] { new IOException("private-test-password"), new InvalidDataException("private-test-password"), new Exception("private-test-password") })
    {
        var diagnostic = MonitoringDiagnostic.From(exception);
        Check(!JsonSerializer.Serialize(diagnostic).Contains("private-test-password"));
    }
    Check(MonitoringDiagnostic.From(new SocketException()).Code == DiagnosticCode.ServiceUnavailable);
    Check(MonitoringDiagnostic.From(new EndOfStreamException()).Code == DiagnosticCode.ConnectionClosed);
    Check(MonitoringDiagnostic.From(new OperationCanceledException()).Code == DiagnosticCode.ResponseTimeout);
});

async Task SessionFailure(DiagnosticCode expected, bool revoke, bool answerPings)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var timing = new ObserverTiming(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
    var observer = new BridgeObserver(TimeProvider.System, timing);
    var observedPackets = new List<Packet>();
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(stop.Token); var stream = peer.GetStream();
        _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, stop.Token);
        _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x90, 9, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, stop.Token);
        for (var i = 0; i < 2; i++) observedPackets.Add(await MqttWire.ReadAsync(stream, stop.Token));
        var live = Publish("Customize/SupportInfo", supportJson);
        await stream.WriteAsync(MqttWire.Frame(live.Header, live.Body), stop.Token);
        observedPackets.Add(await MqttWire.ReadAsync(stream, stop.Token));
        if (revoke)
        {
            var invalid = Publish("Customize/SupportInfo", supportJson.Replace("\"ServiceReady\":1", "\"ServiceReady\":0"));
            await stream.WriteAsync(MqttWire.Frame(invalid.Header, invalid.Body), stop.Token);
        }
        while (true)
        {
            var packet = await MqttWire.ReadAsync(stream, stop.Token); observedPackets.Add(packet);
            if (packet.Header == 0xe0) break;
            if (packet.Header == 0xc0 && answerPings) await stream.WriteAsync(new byte[] { 0xd0, 0 }, stop.Token);
            if (!answerPings && packet.Header == 0x30) await stream.WriteAsync(MqttWire.Frame(live.Header, live.Body), stop.Token);
            if (!revoke)
            {
                var sensor = Publish("System/CpuInfo", """{"CpuTemperature":58}""");
                await stream.WriteAsync(MqttWire.Frame(sensor.Header, sensor.Body), stop.Token);
            }
        }
    });
    await RejectAsync<MonitoringException>(() => observer.ObserveAtAsync("test", (IPEndPoint)listener.LocalEndpoint,
        sample => { if (revoke && sample.Text("ServiceReady") == "0") observer.RequestRefresh(); }, _ => { }, stop.Token, false));
    await server;
    Check(observer.LastDiagnostic?.Code == expected && observer.Phase == MonitorPhase.Stopped);
    Check(observer.Requests.Count(r => r.Action == "System_ON") == 1);
    Check(observedPackets[^1].Header == 0xe0 && !observedPackets.Any(p => Encoding.UTF8.GetString(p.Body).Contains("System_OFF")));
    if (revoke) Check(observer.Requests.Count == 3, "Downgrade must stop before any pending manual refresh");
}
AsyncTest("live capability downgrade disconnects before queued manual refresh", () => SessionFailure(DiagnosticCode.CapabilityRevoked, true, true));
AsyncTest("sensor traffic and PINGRESP do not renew capability authorization", () => SessionFailure(DiagnosticCode.CapabilityExpired, false, true));
AsyncTest("fresh sensor and capability traffic cannot mask missing PINGRESP", () => SessionFailure(DiagnosticCode.ResponseTimeout, false, false));

AsyncTest("rejected subscription has a distinct diagnostic and no queries", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(stop.Token); var stream = peer.GetStream();
        _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, stop.Token);
        _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x90, 9, 0, 1, 0x80, 0, 0, 0, 0, 0, 0 }, stop.Token);
    });
    var observer = new BridgeObserver();
    await RejectAsync<MonitoringException>(() => observer.ObserveAtAsync("test", (IPEndPoint)listener.LocalEndpoint, _ => { }, _ => { }, stop.Token, false));
    await server; Check(observer.Requests.Count == 0 && observer.LastDiagnostic?.Code == DiagnosticCode.SubscriptionRejected);
});

Test("pilot names follow current UI while GAMING remains the wire identifier", () =>
{
    Check(DeviceAudit.ModeName("0") == "办公模式" && DeviceAudit.ModeName("1") == "均衡模式"
        && DeviceAudit.ModeName("2") == "狂暴模式" && DeviceAudit.ModeName("3") == "自定义模式");
    Check(Encoding.UTF8.GetString(MqttWire.Mode(PilotMode.Balanced)).Contains("OPERATING_GAMING_MODE"));
    Check(MqttWire.Mode(PilotMode.Office)[0] == 0x30);
    Check(Encoding.UTF8.GetString(MqttWire.Mode(PilotMode.Turbo)).Contains("OPERATING_TURBO_MODE"));
    Check(Encoding.UTF8.GetString(MqttWire.Mode(PilotMode.Custom)).Contains("OPERATING_CUSTOM_MODE"));
    Reject<ArgumentOutOfRangeException>(() => MqttWire.Mode((PilotMode)4));
});

TopicSample PilotStatus(int mode, bool ac = true, bool changed = false)
{
    var values = ModePilotPolicy.ConfigurationFields.ToDictionary(f => f, f => (object)$"m{mode}-{f}");
    values["OperatingMode"] = mode.ToString(); values["IsAC"] = ac;
    if (changed) values["CPU_PL1"] = "external-change";
    return Sample("Fan/Status", JsonSerializer.Serialize(values));
}

async Task PilotCase(string scenario)
{
    var mode = scenario == "noop" ? 1 : 0;
    var writes = new List<PilotMode>(); var saved = new List<ModeJournal>();
    var pilot = new ModePilot(TimeProvider.System, true, scenario is "timeout" or "missing" ? TimeSpan.FromMilliseconds(250) : null);
    var cap = supportJson[..^1] + ",\"FanSettingsSupport\":1}";
    pilot.SetJournalWriter(j =>
    {
        if (scenario == "disk" && j.Phase == PilotPhase.Sending) throw new IOException("test full disk");
        saved.Add(j);
    });
    pilot.Bind((target, _) =>
    {
        pilot.CheckSending(); Check(saved.Last().Phase is PilotPhase.Sending or PilotPhase.Restoring);
        Check(ModePilotPolicy.Complete(saved.Last().Baseline), "Baseline must precede every write");
        writes.Add(target); if (scenario != "timeout") mode = (int)target;
        if (scenario == "os-during") pilot.UpdatePowerSource(false);
        return Task.CompletedTask;
    }, _ =>
    {
        if (scenario != "missing")
        {
            pilot.Observe(Sample("Customize/SupportInfo", cap));
            if (scenario == "null-mode")
            {
                pilot.Observe(Sample("Tray/Status", JsonSerializer.Serialize(new { OperatingMode = mode, IsAC = true })));
                var status = PilotStatus(mode);
                var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(status.Payload.GetRawText())!;
                if (mode == 1) payload["OperatingMode"] = null;
                pilot.Observe(Sample("Fan/Status", JsonSerializer.Serialize(payload)));
            }
            else pilot.Observe(PilotStatus(mode, scenario != "dc"));
        }
        return Task.CompletedTask;
    }, () => true, default);
    var firstTask = pilot.SwitchAsync(PilotMode.Balanced);
    if (scenario == "busy") Check(!(await pilot.SwitchAsync(PilotMode.Office)).Succeeded);
    var first = await firstTask;
    if (scenario is "disk" or "missing" or "dc") { Check(!first.Succeeded && writes.Count == 0); return; }
    if (scenario == "noop") { Check(first.Succeeded && writes.Count == 0); return; }
    if (scenario is "timeout" or "os-during")
    {
        Check(!first.Succeeded && pilot.Journal.Phase == PilotPhase.Uncertain && writes.Count == 1);
        Check(!(await pilot.RestoreAsync()).Succeeded && writes.Count == 1); return;
    }
    Check(first.Succeeded && pilot.Journal.Phase == PilotPhase.Applied && writes.SequenceEqual(new[] { PilotMode.Balanced }));
    if (scenario == "keep")
    {
        Check(pilot.CanKeep && pilot.KeepCurrent().Succeeded && writes.Count == 1);
        Check(pilot.Journal.Phase == PilotPhase.Kept && !pilot.CanRestore);
        Check(!(await pilot.RestoreAsync()).Succeeded && pilot.Journal.Phase == PilotPhase.Kept);
        pilot.End(); Check(pilot.Journal.Phase == PilotPhase.Kept); return;
    }
    if (scenario == "noop-applied")
    {
        var id = pilot.Journal.Id;
        Check((await pilot.SwitchAsync(PilotMode.Balanced)).Succeeded && writes.Count == 1);
        Check(pilot.Journal.Id == id && pilot.Journal.Phase == PilotPhase.Applied && pilot.CanRestore);
    }
    if (scenario is "sequential" or "keep-next")
    {
        if (scenario == "keep-next") Check(pilot.KeepCurrent().Succeeded);
        Check((await pilot.SwitchAsync(PilotMode.Office)).Succeeded);
        Check(pilot.Journal.BaselineMode == "1");
        Check((await pilot.RestoreAsync()).Succeeded);
        Check(writes.SequenceEqual(new[] { PilotMode.Balanced, PilotMode.Office, PilotMode.Balanced })); return;
    }
    if (scenario == "keep-disk")
    {
        pilot.SetJournalWriter(j => { if (j.Phase == PilotPhase.Kept) throw new IOException(); });
        Check(!pilot.KeepCurrent().Succeeded && pilot.Journal.Phase == PilotPhase.Uncertain && writes.Count == 1);
        Check(!(await pilot.SwitchAsync(PilotMode.Office)).Succeeded && pilot.Journal.Phase == PilotPhase.Uncertain); return;
    }
    if (scenario == "os-after")
    {
        pilot.UpdatePowerSource(false); pilot.UpdatePowerSource(true);
        Check(pilot.Journal.Phase == PilotPhase.Uncertain && !(await pilot.RestoreAsync()).Succeeded && writes.Count == 1); return;
    }
    if (scenario == "external")
    {
        pilot.Observe(PilotStatus(mode, true, true));
        Check(pilot.Journal.Phase == PilotPhase.Uncertain && !(await pilot.RestoreAsync()).Succeeded && writes.Count == 1); return;
    }
    if (scenario == "disconnect")
    {
        pilot.End(); Check(pilot.Journal.Phase == PilotPhase.Interrupted);
        Check(!(await pilot.RestoreAsync()).Succeeded && writes.Count == 1); return;
    }
    var restored = await pilot.RestoreAsync();
    Check(restored.Succeeded && pilot.Journal.Phase == PilotPhase.Restored);
    Check(writes.SequenceEqual(new[] { PilotMode.Balanced, PilotMode.Office }));
    Check(ModePilotPolicy.SameConfiguration(pilot.Journal.Baseline, restored.Snapshot));
}
AsyncTest("pilot snapshots baseline, verifies target twice, explicitly restores configuration", () => PilotCase("roundtrip"));
AsyncTest("daily keep sends no command and survives connection end", () => PilotCase("keep"));
AsyncTest("daily no-op preserves a pending restore baseline", () => PilotCase("noop-applied"));
AsyncTest("daily consecutive switch restores latest baseline", () => PilotCase("sequential"));
AsyncTest("daily switch after keep records a new baseline", () => PilotCase("keep-next"));
AsyncTest("failed keep persistence stops controls without clearing uncertainty", () => PilotCase("keep-disk"));
Test("legacy mode journal handles kept, applied, malformed and oversized records", () =>
{
    Check(PreviousModeJournal.Describe("{\"Phase\":\"Kept\"}").Contains("已保留"));
    Check(PreviousModeJournal.Describe("{\"Phase\":\"Applied\"}").Contains("已回读确认"));
    Check(PreviousModeJournal.Describe("{\"Phase\":\"Restored\"}") == "");
    Check(PreviousModeJournal.Describe("{}").Contains("缺少"));
    Check(PreviousModeJournal.Describe("{broken").Contains("无法解析"));
    Check(PreviousModeJournal.Describe(new string(' ', 65537)).Contains("过大"));
});
AsyncTest("pilot rejects double-click rather than queueing another mode write", () => PilotCase("busy"));
AsyncTest("pilot refuses DC before any write", () => PilotCase("dc"));
AsyncTest("pilot refuses a journal failure before any write", () => PilotCase("disk"));
AsyncTest("pilot refuses missing fresh responses before any write", () => PilotCase("missing"));
AsyncTest("pilot no-op sends no mode command", () => PilotCase("noop"));
AsyncTest("pilot uncertain outcome never retries or blindly restores", () => PilotCase("timeout"));
AsyncTest("pilot detects external configuration change and refuses overwrite", () => PilotCase("external"));
AsyncTest("pilot connection end preserves baseline but prevents restore replay", () => PilotCase("disconnect"));
AsyncTest("balanced null Fan mode is corroborated by fresh Tray status without modifying raw payload", () => PilotCase("null-mode"));

Test("production mode policy requires all observed fields and preserves null mode", () =>
{
    var config = ModePilotPolicy.Evidence.GetProperty("observedConfigurations").GetProperty("1");
    var sample = Sample("Fan/Status", config.GetRawText());
    Check(ModePilotPolicy.Enabled && ModePilotPolicy.AuditedMode(sample) == "1");
    var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(config.GetRawText())!;
    values["OperatingMode"] = null;
    Check(ModePilotPolicy.AuditedMode(Sample("Fan/Status", JsonSerializer.Serialize(values))) == "1");
    values.Remove("IsAC");
    Check(ModePilotPolicy.AuditedMode(Sample("Fan/Status", JsonSerializer.Serialize(values))) is null);
});
Test("configuration comparison detects changes outside the required field subset", () =>
{
    var a = PilotStatus(0);
    var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(a.Payload.GetRawText())!;
    values["AdditionalSetting"] = 1;
    Check(!ModePilotPolicy.SameConfiguration(a, Sample("Fan/Status", JsonSerializer.Serialize(values))));
});

Test("configuration roles exempt only notifications and keep every other field audited", () =>
{
    foreach (var entry in ModePilotPolicy.Evidence.GetProperty("observedConfigurations").EnumerateObject())
    {
        var original = Sample("Fan/Status", entry.Value.GetRawText());
        foreach (var field in entry.Value.EnumerateObject())
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(entry.Value.GetRawText())!;
            values[field.Name] = "changed-for-test";
            var changed = Sample("Fan/Status", JsonSerializer.Serialize(values));
            if (AuditReview.AdvisoryFields.Contains(field.Name))
                Check(ModePilotPolicy.AuditedMode(changed) == entry.Name && ModePilotPolicy.SameConfiguration(original, changed), field.Name);
            else
                Check(ModePilotPolicy.AuditedMode(changed) is null, field.Name);
        }
    }
});
Test("AC is a live gate rather than a configuration identity", () =>
{
    var config = ModePilotPolicy.Evidence.GetProperty("observedConfigurations").GetProperty("0");
    var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(config.GetRawText())!;
    values["IsAC"] = false;
    var sample = Sample("Fan/Status", JsonSerializer.Serialize(values));
    Check(ModePilotPolicy.AuditedMode(sample) == "0");
    Check(ModePilotPolicy.SameConfiguration(Sample("Fan/Status", config.GetRawText()), sample));
});
Test("all four null mode reports resolve uniquely and compare symmetrically with their baseline", () =>
{
    foreach (var entry in ModePilotPolicy.Evidence.GetProperty("observedConfigurations").EnumerateObject())
    {
        var original = Sample("Fan/Status", entry.Value.GetRawText());
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(entry.Value.GetRawText())!;
        values["OperatingMode"] = null;
        var sample = Sample("Fan/Status", JsonSerializer.Serialize(values));
        Check(ModePilotPolicy.AuditedMode(sample) == entry.Name);
        Check(sample.Text("OperatingMode") is null);
        Check(ModePilotPolicy.SameConfiguration(original, sample) && ModePilotPolicy.SameConfiguration(sample, original));
    }
});
Test("audit review identifies concrete changed and missing control fields", () =>
{
    var config = ModePilotPolicy.Evidence.GetProperty("observedConfigurations").GetProperty("3");
    var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(config.GetRawText())!;
    values["CPU_PL1"] = "99"; values.Remove("CustomProfileIndex"); values["NewControl"] = "1";
    var review = AuditReview.Configuration(Sample("Fan/Status", JsonSerializer.Serialize(values)));
    Check(review.Mode is null);
    Check(review.Differences.Any(d => d.Field == "CPU_PL1" && d.Observed == "99"));
    Check(review.Differences.Any(d => d.Field == "CustomProfileIndex" && d.Observed == "missing"));
    Check(review.Differences.Any(d => d.Field == "NewControl" && d.Category == "unclassified"));
});
Test("installation review distinguishes BIOS, service hash and file size changes", () =>
{
    var profile = AuditedProfile.Current;
    var device = new DeviceIdentity(profile.Manufacturer, profile.Model, profile.Board, "new BIOS");
    var difference = AuditReview.Device(device).Single();
    Check(difference.Field == "BIOS" && difference.Expected == profile.Bios && difference.Observed == "new BIOS");
    var files = AuditReview.Files(new string('0', 64), profile.Bridge.Bytes + 1, profile.Service.Sha256, profile.Service.Bytes);
    Check(files.Count == 2 && files.All(d => d.Field.StartsWith("GCUBridge")));
    Check(AuditReview.Files(profile.Bridge.Sha256, profile.Bridge.Bytes, profile.Service.Sha256, profile.Service.Bytes).Count == 0);
});
Test("unrelated capability changes preserve lease while identity and readiness differences are explained", () =>
{
    var original = Sample("Customize/SupportInfo", supportJson);
    var changed = Sample("Customize/SupportInfo", supportJson.Replace("\"ColorCalibrationSupport\":4", "\"ColorCalibrationSupport\":0"));
    var clock = new TestClock(); var lease = new TelemetryLease(clock, TimeSpan.FromSeconds(45));
    Check(lease.Observe(original) && !lease.Observe(changed) && lease.Demand());
    Check(AuditReview.Capabilities(changed).Count == 0);
    var mismatched = Sample("Customize/SupportInfo", supportJson.Replace("\"IDY\"", "\"NEW\"").Replace("\"ServiceReady\":1", "\"ServiceReady\":0"));
    var differences = AuditReview.Capabilities(mismatched);
    Check(differences.Any(d => d.Field == "BIOS_PROJECT_ID" && d.Category == "machine identity"));
    Check(differences.Any(d => d.Field == "ServiceReady" && d.Category == "monitoring requirement"));
});

async Task ProductionModeCase(PilotMode baseline, PilotMode target, bool changedProfile = false, bool turboSupported = true, bool varyingNotifications = false)
{
    var mode = baseline; var writes = new List<PilotMode>();
    var pilot = new ModePilot(TimeProvider.System);
    var statusNumber = 0;
    pilot.SetJournalWriter(_ => { });
    pilot.Bind((selected, _) => { pilot.CheckSending(); writes.Add(selected); mode = selected; return Task.CompletedTask; }, _ =>
    {
        pilot.Observe(Sample("Customize/SupportInfo", supportJson[..^1] + ",\"FanSettingsSupport\":1,\"TurboModeSupport\":" + (turboSupported ? "1}" : "0}")));
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(ModePilotPolicy.Evidence.GetProperty("observedConfigurations").GetProperty(((int)mode).ToString()).GetRawText())!;
        if (changedProfile) payload["CustomProfileIndex"] = "99";
        if (varyingNotifications)
        {
            payload["BSOD_DefaultNotify"] = (++statusNumber % 2).ToString();
            payload["BSOD_TimestampRestored"] = "notification-" + statusNumber;
        }
        pilot.Observe(Sample("Fan/Status", JsonSerializer.Serialize(payload)));
        return Task.CompletedTask;
    }, () => true, default);
    var result = await pilot.SwitchAsync(target);
    if (changedProfile || !turboSupported) { Check(!result.Succeeded && writes.Count == 0); return; }
    Check(result.Succeeded && pilot.Journal.ConfirmedMode == ((int)target).ToString());
    Check((await pilot.RestoreAsync()).Succeeded && pilot.Journal.Phase == PilotPhase.Restored);
    Check(writes.SequenceEqual(new[] { target, baseline }));
    Check(ModePilotPolicy.SameConfiguration(pilot.Journal.Baseline, pilot.Journal.Confirmed));
}
AsyncTest("production office-turbo-office uses audited configurations", () => ProductionModeCase(PilotMode.Office, PilotMode.Turbo));
AsyncTest("notification changes during switch and restore do not invalidate stable control readback", () => ProductionModeCase(PilotMode.Office, PilotMode.Balanced, varyingNotifications: true));
AsyncTest("production office-saved-custom-office uses audited configurations", () => ProductionModeCase(PilotMode.Office, PilotMode.Custom));
AsyncTest("production restores an original turbo baseline", () => ProductionModeCase(PilotMode.Turbo, PilotMode.Balanced));
AsyncTest("production restores an original saved custom baseline", () => ProductionModeCase(PilotMode.Custom, PilotMode.Office));
AsyncTest("changed OEM custom profile index prevents any mode write", () => ProductionModeCase(PilotMode.Office, PilotMode.Custom, true));
AsyncTest("turbo capability unavailable prevents any turbo write", () => ProductionModeCase(PilotMode.Office, PilotMode.Turbo, turboSupported: false));

Test("missing or quiet sensors are advisory and preserve per-topic freshness", () =>
{
    var clock = new TestClock(); var health = new SessionHealth(clock); health.Begin();
    void Observe(string topic, string json, bool retained = false)
    {
        clock.WallClock = DateTimeOffset.UnixEpoch.AddSeconds(clock.GetElapsedTime(0).TotalSeconds);
        health.Observe(Sample(topic, json, retained) with { ReceivedAtUtc = clock.GetUtcNow(), ReceivedTimestamp = clock.GetTimestamp() });
    }
    Observe("System/CpuInfo", "{\"CpuTemperature\":58}", true);
    Observe("Fan/Status", "{\"IsAC\":true}");
    clock.Advance(TimeSpan.FromSeconds(40)); Observe("Customize/SupportInfo", supportJson);
    var state = health.Snapshot(true);
    Check(state.SensorNotice.Contains("CpuInfo") && state.Topics.Single(t => t.Topic == "System/CpuInfo").Freshness == "Cached");
    Observe("System/CpuInfo", "{\"CpuTemperature\":58}");
    var changed = health.Snapshot(true).Topics.Single(t => t.Topic == "System/CpuInfo").LastChangedAtUtc;
    clock.Advance(TimeSpan.FromSeconds(20)); Observe("System/CpuInfo", "{\"CpuTemperature\":58}");
    var cpu = health.Snapshot(true).Topics.Single(t => t.Topic == "System/CpuInfo");
    Check(cpu.LiveReceived == 2 && cpu.MaximumLiveGapSeconds == 20 && cpu.AgeSeconds == 0 && cpu.LastChangedAtUtc == changed);
    clock.Advance(TimeSpan.FromSeconds(20));
    Check(health.Snapshot(true).Topics.Single(t => t.Topic == "System/CpuInfo").Freshness == "Stale");
    Check(health.Snapshot(true).Topics.Single(t => t.Topic == "Customize/SupportInfo").AgeSeconds == 40);
});
Test("session counters remain bounded through long simulated monitoring and stop time freezes", () =>
{
    var clock = new TestClock(); var health = new SessionHealth(clock); health.Begin();
    var sample = Sample("System/FanInfo", "{\"CpuFanRpm\":2000}");
    for (var i = 0; i < 10000; i++)
    {
        clock.Advance(TimeSpan.FromSeconds(1));
        health.Observe(sample with { ReceivedTimestamp = clock.GetTimestamp(), ReceivedAtUtc = clock.GetUtcNow() });
        if (i % 100 == 0) health.RecordEvent("Test", "bounded event");
    }
    health.Request(ReadRequest.EnableTelemetry); health.Request(ReadRequest.FanStatus);
    health.Stop(DiagnosticCode.Cancelled); var before = health.Snapshot(false); clock.Advance(TimeSpan.FromHours(2));
    var after = health.Snapshot(false);
    Check(after.ReceivedMessages == 10000 && after.Topics.Count == 7 && after.Events.Count == 64);
    Check(after.DurationSeconds == 10000 && before.DurationSeconds == after.DurationSeconds);
    Check(after.SamplingRequests == 1 && after.SentReadRequests == 2 && after.StopReason == DiagnosticCode.Cancelled);
});
Test("vendor power transitions are recorded without cached-state false transitions", () =>
{
    var health = new SessionHealth(TimeProvider.System);
    foreach (var sample in new[] { Sample("Fan/Status", "{\"IsAC\":true}"), Sample("Fan/Status", "{\"IsAC\":false}", true), Sample("Fan/Status", "{\"IsAC\":false}"), Sample("Fan/Status", "{\"IsAC\":true}") }) health.Observe(sample);
    var events = health.Snapshot(true).Events.Where(e => e.Kind == "VendorPowerSource").ToArray();
    Check(events.Length == 3 && events[1].Detail.Contains("电池"));
});
AsyncTest("OS battery notification prevents a write even while vendor AC state is still fresh", async () =>
{
    var pilot = new ModePilot(TimeProvider.System, true); var writes = 0;
    pilot.SetJournalWriter(_ => { });
    pilot.Bind((_, _) => { writes++; return Task.CompletedTask; }, _ =>
    {
        pilot.Observe(Sample("Customize/SupportInfo", supportJson[..^1] + ",\"FanSettingsSupport\":1}"));
        pilot.Observe(PilotStatus(0));
        return Task.CompletedTask;
    }, () => true, default);
    pilot.UpdatePowerSource(false);
    Check(!(await pilot.SwitchAsync(PilotMode.Balanced)).Succeeded && writes == 0);
});
AsyncTest("suspend interrupts an established session without mode replay and reports its actual cause", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(stop.Token); var stream = peer.GetStream();
        _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, stop.Token);
        _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x90, 9, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, stop.Token);
        for (var i = 0; i < 2; i++) _ = await MqttWire.ReadAsync(stream, stop.Token);
        var cap = Publish("Customize/SupportInfo", supportJson); await stream.WriteAsync(MqttWire.Frame(cap.Header, cap.Body), stop.Token);
        _ = await MqttWire.ReadAsync(stream, stop.Token); ready.SetResult();
        Check((await MqttWire.ReadAsync(stream, stop.Token)).Header == 0xe0);
    });
    var observer = new BridgeObserver();
    var monitor = observer.ObserveAtAsync("test", (IPEndPoint)listener.LocalEndpoint, _ => { }, _ => { }, stop.Token, false);
    await ready.Task.WaitAsync(stop.Token); observer.NotifySuspend();
    await RejectAsync<MonitoringException>(() => monitor); await server; observer.NotifyResume();
    Check(observer.LastDiagnostic?.Code == DiagnosticCode.SystemSuspended && observer.Phase == MonitorPhase.Stopped);
    Check(observer.Requests.Count == 3 && observer.Requests.All(r => !r.Action.StartsWith("OPERATING_")));
    Check(observer.Health.Snapshot(false).StopReason == DiagnosticCode.SystemSuspended);
    Check(observer.Health.Snapshot(false).Events.Any(e => e.Kind == "Resumed"));
});

AsyncTest("OS battery notification during readback stops without retry or restore", () => PilotCase("os-during"));
AsyncTest("returning to AC after a changed applied session does not replay control", () => PilotCase("os-after"));
AsyncTest("quiet sensor stream keeps healthy monitoring alive without automatic sampling retries", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var cancel = new CancellationTokenSource();
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(stop.Token); var stream = peer.GetStream();
        _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, stop.Token);
        _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x90, 9, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, stop.Token);
        while (true)
        {
            var packet = await MqttWire.ReadAsync(stream, stop.Token);
            if (packet.Header == 0xe0) return;
            if (packet.Header == 0xc0) await stream.WriteAsync(new byte[] { 0xd0, 0 }, stop.Token);
            if (packet.Header == 0x30)
            {
                var text = Encoding.UTF8.GetString(packet.Body);
                if (text.Contains("System_ON")) ready.TrySetResult();
                if (text.Contains("GETSUPPORT"))
                {
                    var sample = Publish("Customize/SupportInfo", supportJson);
                    await stream.WriteAsync(MqttWire.Frame(sample.Header, sample.Body), stop.Token);
                }
                if (text.Contains("GETSTATUS"))
                {
                    var sample = Publish("Fan/Status", "{\"OperatingMode\":\"0\",\"IsAC\":true}");
                    await stream.WriteAsync(MqttWire.Frame(sample.Header, sample.Body), stop.Token);
                }
            }
        }
    });
    var timing = new ObserverTiming(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
    var observer = new BridgeObserver(TimeProvider.System, timing);
    var task = observer.ObserveAtAsync("test", (IPEndPoint)listener.LocalEndpoint, _ => { }, _ => { }, cancel.Token, false);
    await ready.Task.WaitAsync(stop.Token); await Task.Delay(750, stop.Token);
    Check(observer.Phase == MonitorPhase.Monitoring && observer.LastDiagnostic is null);
    Check(observer.Health.Snapshot(true).Topics.Where(t => t.Topic.StartsWith("System/")).All(t => t.LiveReceived == 0));
    Check(observer.Requests.Count(r => r.Action == "System_ON") == 1 && observer.Requests.All(r => !r.Action.StartsWith("OPERATING_")));
    cancel.Cancel(); await RejectAsync<OperationCanceledException>(() => task); await server;
});

AsyncTest("simulated service restart leaves applied control interrupted and reconnect requires fresh capabilities", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondCached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var allowFresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    TcpClient? firstPeer = null; var interrupted = false; var mode = 0;
    var server = Task.Run(async () =>
    {
        for (var round = 0; round < 2; round++)
        {
            using var peer = await listener.AcceptTcpClientAsync(stop.Token);
            if (round == 0) firstPeer = peer;
            var stream = peer.GetStream();
            _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, stop.Token);
            _ = await MqttWire.ReadAsync(stream, stop.Token); await stream.WriteAsync(new byte[] { 0x90, 9, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, stop.Token);
            for (var i = 0; i < 2; i++) _ = await MqttWire.ReadAsync(stream, stop.Token);
            var cap = Publish("Customize/SupportInfo", supportJson[..^1] + ",\"FanSettingsSupport\":1}");
            await stream.WriteAsync(MqttWire.Frame(round == 1 ? (byte)0x31 : cap.Header, cap.Body), stop.Token);
            if (round == 1)
            {
                secondCached.SetResult(); await allowFresh.Task.WaitAsync(stop.Token);
                await stream.WriteAsync(MqttWire.Frame(cap.Header, cap.Body), stop.Token);
            }
            var status = Publish("Fan/Status", ModePilotPolicy.Evidence.GetProperty("observedConfigurations").GetProperty(mode.ToString()).GetRawText());
            await stream.WriteAsync(MqttWire.Frame(status.Header, status.Body), stop.Token);
            try
            {
                while (true)
                {
                    var packet = await MqttWire.ReadAsync(stream, stop.Token);
                    if (packet.Header == 0xe0) break;
                    if (packet.Header == 0xc0) { await stream.WriteAsync(new byte[] { 0xd0, 0 }, stop.Token); continue; }
                    var text = Encoding.UTF8.GetString(packet.Body);
                    if (text.Contains("System_ON")) (round == 0 ? firstReady : secondReady).TrySetResult();
                    if (text.Contains("GETSUPPORT")) await stream.WriteAsync(MqttWire.Frame(cap.Header, cap.Body), stop.Token);
                    if (text.Contains("OPERATING_GAMING_MODE")) mode = 1;
                    if (text.Contains("GETSTATUS") || text.Contains("OPERATING_GAMING_MODE"))
                    {
                        status = Publish("Fan/Status", ModePilotPolicy.Evidence.GetProperty("observedConfigurations").GetProperty(mode.ToString()).GetRawText());
                        await stream.WriteAsync(MqttWire.Frame(status.Header, status.Body), stop.Token);
                    }
                }
            }
            catch (Exception ex) when (round == 0 && interrupted && ex is IOException or SocketException or ObjectDisposedException) { }
        }
    });
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    var first = new BridgeObserver(); first.Pilot.SetJournalWriter(_ => { });
    var original = first.ObserveAtAsync("test", endpoint, _ => { }, _ => { }, stop.Token, false);
    await firstReady.Task.WaitAsync(stop.Token);
    Check((await first.Pilot.SwitchAsync(PilotMode.Balanced, stop.Token)).Succeeded);
    interrupted = true; firstPeer!.Close();
    await RejectAsync<IOException>(() => original);
    Check(first.Pilot.Journal.Phase == PilotPhase.Interrupted && first.Requests.Count(r => r.Action.StartsWith("OPERATING_")) == 1);
    using var cancel = new CancellationTokenSource(); var second = new BridgeObserver(); second.Pilot.SetJournalWriter(_ => { });
    var reconnected = second.ObserveAtAsync("test", endpoint, _ => { }, _ => { }, cancel.Token, false);
    await secondCached.Task.WaitAsync(stop.Token); await Task.Delay(75, stop.Token);
    Check(second.Phase == MonitorPhase.WaitingCapabilities && second.Requests.Count == 2);
    allowFresh.SetResult(); await secondReady.Task.WaitAsync(stop.Token); await Task.Delay(25, stop.Token);
    Check(second.Phase == MonitorPhase.Monitoring && !second.Pilot.CanRestore && second.Pilot.CurrentMode == "1");
    Check(second.Requests.All(r => !r.Action.StartsWith("OPERATING_")) && second.Requests.Count(r => r.Action == "System_ON") == 1);
    cancel.Cancel(); await RejectAsync<OperationCanceledException>(() => reconnected); await server;
});

var failures = new List<string>();
foreach (var test in cases)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures.Add(test.Name); Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{cases.Count - failures.Count}/{cases.Count} checks passed");
return failures.Count == 0 ? 0 : 1;

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
}

sealed class TestClock : TimeProvider
{
    private long _ticks;
    public DateTimeOffset WallClock { get; set; } = DateTimeOffset.UnixEpoch;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => WallClock;
    public void Advance(TimeSpan elapsed) { _ticks += elapsed.Ticks; }
}
