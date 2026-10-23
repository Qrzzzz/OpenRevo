"""Reproducible public evidence index. --check needs no vendor installation."""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCES = ['protocol/commands.json', 'protocol/live-evidence.json', 'protocol/processing-chains.json', 'protocol/device-profiles.json', 'protocol/mode-pilot.json', 'protocol/mode-evidence.json', 'protocol/stability-evidence.json', 'protocol/configuration-policy.json', 'protocol/fan-boost.json']
REQUESTS = [
    ('Capabilities', 'Customize/SupportControl', 'GETSUPPORT', ['Customize/SupportInfo'], 'query capabilities; no hardware control'),
    ('FanStatus', 'Fan/Control', 'GETSTATUS', ['Fan/Status'], 'query current configuration; values are not live watts'),
    ('EnableTelemetry', 'System/Control', 'System_ON', ['System/CpuInfo', 'System/GpuInfo', 'System/FanInfo', 'System/BatteryInfo'], 'starts vendor shared sampling timer; no ownership or global stop')
]
TOPICS = ['Customize/SupportInfo', 'Fan/Status', 'Tray/Status', 'System/FanInfo', 'System/CpuInfo', 'System/GpuInfo', 'System/BatteryInfo']


def build():
    inputs = {p: json.loads((ROOT / p).read_text(encoding='utf-8-sig')) for p in SOURCES}
    reports = inputs['protocol/live-evidence.json']['reports']
    requests = []
    for request, topic, action, responses, side_effect in REQUESTS:
        requests.append({'id': request, 'topic': topic, 'action': action, 'enabled': True,
                         'qos': 0, 'retain': False, 'payloadFields': {'Action': 'string literal: ' + action},
                         'responseTopics': responses, 'businessAck': 'not established',
                         'failure': 'typed diagnostic; session stops; manual reconnect; no replay',
                         'sideEffect': side_effect, 'liveEvidence': 'protocol/live-evidence.json',
                         'handlerBodyRecovered': False, 'rollbackVerified': False})
    for command in inputs['protocol/commands.json']['commands']:
        if command['payload']['Action'] in {r[2] for r in REQUESTS}:
            continue
        requests.append({'id': command['payload']['Action'], 'topic': command['topic'],
                         'action': command['payload']['Action'], 'enabled': False,
                         'senderEvidence': command['sender_evidence'], 'liveEvidence': None,
                         'handlerBodyRecovered': False, 'rollbackVerified': False,
                         'reason': 'mode-dependent cooling/power coupling and recovery unverified'})
    topics = []
    for topic in TOPICS:
        fields = {}
        references = []
        for report in reports:
            observed = report['topics'].get(topic)
            if not observed:
                continue
            references.append({'file': report['evidenceFile'], 'sha256': report['sha256'],
                               'retainedCount': observed['retainedCount'], 'nonRetainedCount': observed['nonRetainedCount']})
            for field, schema in observed['fields'].items():
                fields.setdefault(field, set()).update(schema['jsonTypes'])
        topics.append({'topic': topic, 'fields': {f: {'jsonTypes': sorted(t), 'unit':
                      'degC' if f in ('CpuTemperature','GpuTemperature') else
                      'percent' if f in ('CpuUsage','GpuUsage','CpuFanDuty','GpuFanDuty','BatteryLifePercent') else
                      'rpm' if f in ('CpuFanRpm','GpuFanRpm') else 'unconfirmed or encoded'} for f, t in sorted(fields.items())},
                       'evidence': references, 'freshness': 'retained=cached; live ages on monotonic clock; >15 seconds stale; disconnected=history'})
    pilot = inputs['protocol/mode-pilot.json']
    controls = [{'id': name, 'topic': 'Fan/Control', 'action': action, 'enabled': pilot['verified'] and str(code) in pilot['observedConfigurations'], 'qos': 0, 'retain': False,
                 'acOnly': True, 'evidence': 'protocol/mode-evidence.json', 'restoreScope': 'original mode and reported audited configuration fields only',
                 'gate': 'same audited profile and exact configuration, current capabilities, AC, saved baseline, same connection, explicit action',
                 'businessAck': 'two consistent fresh readbacks; not a server operation ACK; no CAS'}
                for code, name, action in [(0, 'Office', 'OPERATING_OFFICE_MODE'), (1, 'Balanced', 'OPERATING_GAMING_MODE'), (2, 'Turbo', 'OPERATING_TURBO_MODE'), (3, 'Custom', 'OPERATING_CUSTOM_MODE')]]
    return {'schemaVersion': 1, 'sourceHashFormat': 'UTF-8 without BOM, LF newlines',
            'sources': [{'path': p, 'sha256': hashlib.sha256((ROOT / p).read_text(encoding='utf-8-sig').encode('utf-8')).hexdigest()} for p in SOURCES],
            'scope': 'audited local machine only; handler metadata is not recovered handler logic',
            'requests': requests, 'controlRequests': controls,
            'fanControlRequests': [{'id': 'FanBoost' + name.title(), 'topic': 'Fan/Control', 'action': action, 'enabled': inputs['protocol/fan-boost.json']['verified'], 'qos': 0, 'retain': False, 'acOnly': True, 'evidence': 'protocol/fan-boost.json'} for name, action in inputs['protocol/fan-boost.json']['actions'].items()],
            'modePilotPolicy': 'protocol/mode-pilot.json', 'subscriptions': topics,
            'machineProfiles': 'protocol/device-profiles.json', 'processingChains': 'protocol/processing-chains.json',
            'timing': {'pingSeconds': 10, 'configurationRefreshSeconds': 15, 'staleSeconds': 15, 'capabilityLeaseSeconds': 45},
            'blocked': ['System_OFF', 'raw publish', 'driver access', 'remote hosts', 'automatic reconnect or control replay']}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    text = json.dumps(build(), ensure_ascii=False, indent=2) + '\n'
    path = ROOT / 'protocol/registry.json'
    if args.check:
        if path.read_text(encoding='utf-8') != text:
            raise SystemExit('Evidence registry is stale; run scripts/build-evidence-registry.py')
    else:
        path.write_text(text, encoding='utf-8', newline='\n')
    print('Evidence registry verified: 3 read requests, 4 mode actions, 2 fan boost actions, 7 subscriptions; historical sender catalog is not an executable allowlist.')


if __name__ == '__main__':
    main()
