"""Promote an actual, successful AC mode roundtrip into an exact machine policy."""
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    relative = '.local/evidence/v030-roundtrip2.json'
    raw = (ROOT / relative).read_bytes()
    report = json.loads(raw.decode('utf-8-sig'))
    journal = report['modePilot']
    if report['error'] is not None or not report['switchResult']['Succeeded'] or not report['restoreResult']['Succeeded'] or journal['Phase'] != 'Restored':
        raise ValueError('No successful physical roundtrip; policy stays disabled')
    commands = [r for r in report['requests'] if r['Action'].startswith('OPERATING_')]
    if [r['Action'] for r in commands] != ['OPERATING_GAMING_MODE', 'OPERATING_OFFICE_MODE']:
        raise ValueError('Unexpected mode command sequence')
    source = (ROOT / 'src/OpenRevo.Core/ModePilot.cs').read_text(encoding='utf-8-sig')
    field_text = source.split('ConfigurationFields = [', 1)[1].split('];', 1)[0]
    fields = re.findall(r'"([A-Za-z0-9_]+)"', field_text)
    baseline = journal['Baseline']['Payload']
    target = report['switchResult']['Snapshot']['Payload']
    restored = report['restoreResult']['Snapshot']['Payload']
    fields = sorted(baseline)
    if set(baseline) != set(restored):
        raise ValueError('Restored field set changed')
    if baseline['OperatingMode'] != '0' or restored['OperatingMode'] != '0' or any(baseline.get(f) != restored.get(f) for f in fields):
        raise ValueError('Restored reported configuration differs from baseline')
    if not all(s.get('IsAC') is True for s in (baseline, target, restored)):
        raise ValueError('This pilot requires AC power')
    if not any(m['Topic'] == 'Tray/Status' and str(m['Payload'].get('OperatingMode')) == '1' and not m['Retained'] for m in report['messages']):
        raise ValueError('Balanced mode lacks independent Tray corroboration')
    policy_path = ROOT / 'protocol/mode-pilot.json'
    policy = json.loads(policy_path.read_text(encoding='utf-8'))
    profile = json.loads((ROOT / 'protocol/device-profiles.json').read_text(encoding='utf-8'))['profiles'][0]
    if report['inspection']['ProfileId'] != policy['profileId'] or report['bridgeSha256'].upper() != profile['bridge']['sha256'] or report['serviceSha256'].upper() != profile['service']['sha256']:
        raise ValueError('Physical evidence does not match the embedded profile')
    configurations = {code: {f: values[f] for f in fields} for code, values in [('0', baseline), ('1', target)]}
    trials = []
    for code, name, action in [('2', 'turbo', 'OPERATING_TURBO_MODE'), ('3', 'custom', 'OPERATING_CUSTOM_MODE')]:
        trial_path = f'.local/evidence/v030-{name}-roundtrip.json'
        trial_raw = (ROOT / trial_path).read_bytes()
        trial = json.loads(trial_raw.decode('utf-8-sig'))
        base = trial['modePilot']['Baseline']['Payload']
        selected = trial['switchResult']['Snapshot']['Payload']
        final = trial['restoreResult']['Snapshot']['Payload']
        writes = [r for r in trial['requests'] if r['Action'].startswith('OPERATING_')]
        if (trial['error'] is not None or not trial['switchResult']['Succeeded'] or not trial['restoreResult']['Succeeded']
                or trial['modePilot']['Phase'] != 'Restored' or base != baseline or final != base
                or selected['OperatingMode'] != code or set(selected) != set(base)
                or not all(v.get('IsAC') is True for v in (base, selected, final))
                or trial['inspection']['ProfileId'] != policy['profileId']
                or trial['bridgeSha256'].upper() != profile['bridge']['sha256']
                or trial['serviceSha256'].upper() != profile['service']['sha256']
                or [r['Action'] for r in writes] != [action, 'OPERATING_OFFICE_MODE']):
            raise ValueError(f'{name}: incomplete physical mode/restore evidence')
        if not any(m['Topic'] == 'Tray/Status' and str(m['Payload'].get('OperatingMode')) == code and not m['Retained'] for m in trial['messages']):
            raise ValueError(f'{name}: missing Tray corroboration')
        configurations[code] = {f: selected[f] for f in fields}
        trials.append({'mode': code, 'physicalEvidence': trial_path, 'sha256': hashlib.sha256(trial_raw).hexdigest(),
                       'commands': writes, 'restoredFieldCount': len(base), 'restoreDifferences': []})
    failed = ROOT / '.local/evidence/v030-roundtrip.json' 
    evidence = {'schemaVersion': 1, 'physicalEvidence': relative, 'sha256': hashlib.sha256(raw).hexdigest(),
                'additionalTrials': trials, 'customScope': 'Activate OEM saved custom profile only; no parameter editing',
                'profileId': policy['profileId'], 'commandCount': len(commands), 'commands': commands,
                'rawBalancedOperatingMode': target['OperatingMode'], 'balancedModeCorroboration': 'fresh non-retained Tray/Status OperatingMode=1',
                'restoredFields': fields, 'restoreDifferences': [], 'configurations': configurations,
                'firstTrial': {'sha256': hashlib.sha256(failed.read_bytes()).hexdigest(),
                               'result': 'stopped as uncertain after one mode command; Fan/Status mode was null; user restored office with OEM UI'},
                'limits': 'AC and this exact profile/configuration pair only; reported fields are not all firmware registers; no sleep/DC/fault hardware testing or server CAS.'}
    (ROOT / 'protocol/mode-evidence.json').write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    policy.update(verified=True, observedConfigurations=configurations,
                  evidence={'path': 'protocol/mode-evidence.json', 'sha256': hashlib.sha256((ROOT / 'protocol/mode-evidence.json').read_bytes()).hexdigest()},
                  modeResolution='Preserve raw null; balanced is resolved only by the exact audited Fan configuration fingerprint. Audit trial additionally required fresh Tray status.')
    policy_path.write_text(json.dumps(policy, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    print('Physical roundtrip verified: three physical roundtrips, six mode writes; 91 reported fields restored each time; four-mode policy generated.')


if __name__ == '__main__':
    main()
