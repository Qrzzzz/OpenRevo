using OpenRevo.Core;

namespace OpenRevo.App;

internal sealed record UiSampleBatch(long Received, IReadOnlyList<TopicSample> Samples);

// The observer still processes every message. Only visual snapshots are coalesced.
// Each connection owns a separate buffer; a late producer cannot enter a new session.
internal sealed class UiSampleBuffer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TopicSample?> _latest = ReadProtocol.Topics.ToDictionary(t => t, _ => (TopicSample?)null);
    private long _received;

    public void Offer(TopicSample sample)
    {
        lock (_gate)
        {
            if (!_latest.ContainsKey(sample.Topic)) return;
            _latest[sample.Topic] = sample;
            _received++;
        }
    }

    public UiSampleBatch Drain()
    {
        lock (_gate)
        {
            var batch = new UiSampleBatch(_received, _latest.Values.OfType<TopicSample>().ToArray());
            foreach (var topic in ReadProtocol.Topics) _latest[topic] = null;
            _received = 0;
            return batch;
        }
    }
}
