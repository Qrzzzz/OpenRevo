namespace OpenRevo.Core;

internal sealed class TelemetryLease(TimeProvider clock, TimeSpan lifetime)
{
    private readonly object _gate = new();
    private long _received;
    private bool _granted;
    private bool _revoked;

    internal bool Observe(TopicSample sample)
    {
        if (sample.Topic != "Customize/SupportInfo" || sample.Retained) return false;
        lock (_gate)
        {
            if (_revoked || !DeviceAudit.MatchesAuditedTelemetry(sample))
            {
                _revoked = true;
                throw new MonitoringException(DiagnosticCode.CapabilityRevoked);
            }
            var first = !_granted;
            _received = sample.ReceivedTimestamp;
            _granted = true;
            return first;
        }
    }

    internal bool Demand()
    {
        lock (_gate)
        {
            if (_revoked) throw new MonitoringException(DiagnosticCode.CapabilityRevoked);
            if (!_granted) return false;
            if (clock.GetElapsedTime(_received) > lifetime)
            {
                _revoked = true;
                throw new MonitoringException(DiagnosticCode.CapabilityExpired);
            }
            return true;
        }
    }
}
