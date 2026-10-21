"""Summarize local 0.4 acceptance records; private raw diagnostics are not packaged."""
import hashlib
import json
import re
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def raw(relative):
    data = (ROOT / relative).read_bytes()
    return json.loads(data.decode('utf-8-sig')), {'path': relative, 'sha256': hashlib.sha256(data).hexdigest()}


def main():
    soak, soak_source = raw('.local/evidence/v040-soak.json')
    duplicate, duplicate_source = raw('.local/evidence/v040-duplicate.json')
    power, power_source = raw('.local/evidence/v040-power-standby.json')
    standby, standby_source = raw('.local/evidence/v040-modern-standby-events.json')
    resumed, resumed_source = raw('.local/evidence/v040-reconnected.json')
    manual, manual_source = raw('.local/evidence/v040-user-observation.json')
    health = soak['sessionHealth']
    if (soak['error'] is not None or health['DurationSeconds'] < 1790
            or any(t['LiveReceived'] == 0 for t in health['Topics'])
            or health['SamplingRequests'] != 1 or soak['trimmedMessages'] != 0
            or any(r['Action'].startswith('OPERATING_') for r in soak['requests'])):
        raise ValueError('Continuous observation did not meet the recorded acceptance scope')
    if duplicate['error']['Code'] != 'SlotOccupied' or duplicate['requests'] or duplicate['receivedCount']:
        raise ValueError('Duplicate session did not exit without requests')
    events = power['sessionHealth']['Events']
    if (power['sessionHealth']['StopReason'] != 'Cancelled' or power['sessionHealth']['SamplingRequests'] != 1 or not power['powerNotificationsAvailable']
            or not any(e['Kind'] == 'WindowsPowerSource' and '电池' in e['Detail'] for e in events)
            or not any(e['Kind'] == 'VendorPowerSource' and '电池' in e['Detail'] for e in events)
            or not manual['dcButtonsDisabled']):
        raise ValueError('Physical AC/DC evidence incomplete')
    parse_time = lambda value: datetime.fromisoformat(value.replace('Z', '+00:00'))
    started, stopped = map(parse_time, (power['sessionHealth']['StartedAtUtc'], power['sessionHealth']['StoppedAtUtc']))
    standby = [e for e in standby if started < parse_time(e['atUtc']) < stopped]
    enter = next(e for e in standby if e['id'] == 506)
    wake = next(e for e in standby if e['id'] == 507 and parse_time(e['atUtc']) > parse_time(enter['atUtc']))
    standby_seconds = (parse_time(wake['atUtc']) - parse_time(enter['atUtc'])).total_seconds()
    if any(r['Action'].startswith('OPERATING_') for r in power['requests'] + resumed['requests']):
        raise ValueError('Unexpected mode command in physical stability validation')
    topics = {t['Topic']: t for t in resumed['sessionHealth']['Topics']}
    if not all(topics[t]['LiveReceived'] > 0 for t in ['Customize/SupportInfo', 'Fan/Status', 'System/CpuInfo', 'System/FanInfo']):
        raise ValueError('Fresh monitoring after explicit reconnect not established')
    log_path = ROOT / '.local/validation/core-tests.log'
    log = log_path.read_bytes()
    match = re.search(r'(\d+)/(\d+) checks passed', log.decode('utf-8-sig'))
    if not match or match[1] != match[2]:
        raise ValueError('Automated checks are not all passing')
    samples = [json.loads(line) for line in (ROOT / '.local/validation/v040-process-samples.jsonl').read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    evidence = {
        'schemaVersion': 1, 'version': '0.4.0', 'profileId': soak['inspection']['ProfileId'],
        'automated': {'passed': int(match[1]), 'total': int(match[2]), 'sourceSha256': hashlib.sha256(log).hexdigest()},
        'continuousObservation': {'source': soak_source, 'scope': '0.4 observation CLI candidate, approximately 30 minutes',
            'durationSeconds': health['DurationSeconds'], 'receivedMessages': soak['receivedCount'],
            'trimmedMessages': soak['trimmedMessages'], 'readRequests': len(soak['requests']),
            'samplingRequests': health['SamplingRequests'], 'modeCommands': 0,
            'topicLiveCounts': {t['Topic']: t['LiveReceived'] for t in health['Topics']},
            'maximumLiveGapsSeconds': {t['Topic']: t['MaximumLiveGapSeconds'] for t in health['Topics']},
            'processSamples': len(samples), 'sampledWorkingSetBytes': {'min': min(s['workingSetBytes'] for s in samples), 'max': max(s['workingSetBytes'] for s in samples)}},
        'duplicateInstance': {'source': duplicate_source, 'requests': 0, 'result': 'SlotOccupied; existing session unaffected'},
        'physicalPowerAndStandby': {'source': power_source, 'userObservation': manual_source, 'standbySource': standby_source,
            'dcButtonsDisabled': True, 'standbyMode': 'S0 low-power idle, connected network',
            'standbySeconds': standby_seconds, 'standbyEnteredAtUtc': enter['atUtc'], 'standbyExitedAtUtc': wake['atUtc'],
            'connectionPreservedThroughStandby': True,
            'endReason': 'explicit disconnect after observation', 'modeCommands': 0,
            'events': [e for e in events if e['Kind'] in ['WindowsPowerSource', 'VendorPowerSource', 'Suspending', 'Resumed']]},
        'explicitReconnect': {'source': resumed_source, 'freshCapabilitiesAndSensors': True, 'modeCommands': 0},
        'traditionalSuspend': {'verification': 'simulated-only; S3 is unavailable on this host', 'expectedStopReason': 'SystemSuspended'},
        'serviceRestart': {'verification': 'simulated-only, as requested', 'physicalServiceModified': False},
        'limits': 'One audited host; no 24-hour/multiday or firmware fault tests; sampled process memory is not a proof of absence of leaks.'
    }
    (ROOT / 'protocol/stability-evidence.json').write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    print('Stability evidence verified: continuous observation, duplicate session, physical AC/DC/S0 standby and explicit reconnect; service restart simulated only.')


if __name__ == '__main__':
    main()
