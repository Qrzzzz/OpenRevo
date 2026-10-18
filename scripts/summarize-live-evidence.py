"""Create a reviewable summary from actual saved observations, never mocks."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
evidence = ROOT / '.local/evidence'
names = ['live-observation.json', 'queried-observation.json', 'vendor-ui-observation.json',
         'independent-monitor-observation.json', 'csharp-observation.json']
reports = []
for name in names:
    path = evidence / name
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    records = data['messages']
    if name == 'csharp-observation.json':
        assert data['error'] is None, 'C# observation failed; cannot summarize as acceptance'
        records = [{'topic': m['Topic'], 'payload': m['Payload'], 'retained': m['Retained'],
                    'receivedAtUtc': m['ReceivedAtUtc']} for m in records]
        requests = [{'topic': r['Topic'], 'payload': {'Action': r['Action']}, 'sentAtUtc': r['SentAtUtc']} for r in data['requests']]
    else:
        requests = data.get('publishedQueries', [])
    allowed = {('Customize/SupportControl', 'GETSUPPORT'), ('Fan/Control', 'GETSTATUS'), ('System/Control', 'System_ON')}
    assert all((r['topic'], r['payload']['Action']) in allowed for r in requests)
    topics = {}
    for message in records:
        topic = topics.setdefault(message['topic'], {'retainedCount': 0, 'nonRetainedCount': 0, 'fields': {}})
        topic['retainedCount' if message['retained'] else 'nonRetainedCount'] += 1
        for key, value in message['payload'].items():
            field = topic['fields'].setdefault(key, {'jsonTypes': set()})
            field['jsonTypes'].add(type(value).__name__)
    for topic in topics.values():
        for field in topic['fields'].values():
            field['jsonTypes'] = sorted(field['jsonTypes'])
    reports.append({'evidenceFile': '.local/evidence/' + name,
                    'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                    'messageCount': len(records), 'applicationQueriesSent': requests, 'topics': topics})

output = {'scope': 'actual machine observations; no control-write acceptance',
          'reports': reports,
          'limitations': ['No hardware write tested or implemented',
                          'Retained data is not current-state proof',
                          'Telemetry timers are shared; System_OFF is never sent',
                          'Some fields are absent or unchanged between pushes; no fabricated fallback values']}
(ROOT / 'protocol/live-evidence.json').write_text(json.dumps(output, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print('Summarized', len(reports), 'actual observations:', sum(r['messageCount'] for r in reports), 'messages.')
