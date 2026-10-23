"""Publish only fan-control evidence summaries, never raw vendor data or credentials."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FILES = [
    ('vendor-ui', '.local/evidence/v110-vendor-fan-roundtrip.json'),
    ('independent', '.local/evidence/v110-fan-independent.json'),
    ('independent-all', '.local/evidence/v110-fan-other-modes.json'),
]


def main():
    evidence = []
    for kind, name in FILES:
        path = ROOT / name
        report = json.loads(path.read_text(encoding='utf-8-sig'))
        messages = report['messages']
        def topic(m):
            return m.get('Topic', m.get('topic'))
        def payload(m):
            return m.get('Payload', m.get('payload'))
        statuses = [payload(m) for m in messages if topic(m) == 'Fan/Status' and not m.get('Retained', m.get('retained', False))]
        fans = [payload(m) for m in messages if topic(m) == 'System/FanInfo']
        if kind == 'vendor-ui':
            actions = [payload(m).get('Action') for m in messages if topic(m) == 'Fan/Control' and payload(m).get('Action') in ('FAN_BOOST_ON', 'FAN_BOOST_OFF')]
        else:
            if report['error'] is not None or not report['switchResult']['Succeeded'] or not report['restoreResult']['Succeeded']:
                raise ValueError('Independent fan audit failed')
            actions = [m['Action'] for m in report['requests'] if m['Action'].startswith('FAN_BOOST_')]
        if actions != ['FAN_BOOST_ON', 'FAN_BOOST_OFF'] * (3 if kind == 'independent-all' else 1):
            raise ValueError('Unexpected fan command sequence')
        if not statuses or statuses[0] != statuses[-1]:
            raise ValueError('Starting and ending configuration differ')
        modes = sorted(set(s['OperatingMode'] for s in statuses if s.get('FanBoostEnable') == '1'))
        evidence.append({
            'kind': kind, 'file': name, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'actions': actions, 'boostedModes': modes, 'configurationRestoredExactly': True,
            'fanSamples': len(fans),
            'maxObservedRpm': {f: max((x[f] for x in fans), default=None) for f in ('CpuFanRpm', 'GpuFanRpm')},
            'lastObservedFan': fans[-1] if fans else None,
        })
    if set(m for e in evidence if e['kind'].startswith('independent') for m in e['boostedModes']) != set('0123'):
        raise ValueError('Four-mode independent fan coverage is incomplete')
    policy_path = ROOT / 'protocol/fan-boost.json'
    policy = json.loads(policy_path.read_text(encoding='utf-8'))
    policy['evidence'] = evidence
    policy_path.write_text(json.dumps(policy, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    print('Fan evidence verified: original UI commands; independent on/off in all four modes; exact configuration restoration.')


if __name__ == '__main__':
    main()
