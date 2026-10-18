using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenRevo.Core;

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
