"""Inspect local decompiled metadata only; never execute vendor code or copy bodies."""
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TARGETS = {
    "MyControlCenter/App.cs": ["M_MQTTService_ClientMessage", "Recieve"],
    "MyControlCenter/CustomizeCtrl.cs": ["Receive", "PublishSupportAll"],
    "MyControlCenter/MyFanCtrl.cs": ["Receive"],
    "MyControlCenter/MySystemCtrl.cs": ["Receive"],
    "MyControlCenter/MySystemManager.cs": ["InitSystemInfo"],
    "MyControlCenter/MqttClientCtrl.cs": ["PublishTopic"],
}


def main():
    manifest = json.loads((ROOT / '.local/evidence/manifest.json').read_text(encoding='utf-8-sig'))
    service = next(f for f in manifest['files'] if f['relative'].endswith('GCUService.exe'))
    binary = ROOT / '.local/evidence' / service['relative'].replace('\\', '/')
    if hashlib.sha256(binary.read_bytes()).hexdigest() != service['sha256'].lower():
        raise ValueError('Service snapshot identity changed')
    rows = []
    for relative, names in TARGETS.items():
        path = ROOT / '.local/decompiled/GCUService' / relative
        data = path.read_bytes()
        text = data.decode('utf-8-sig')
        for name in names:
            match = re.search(r'(?m)^\s*(?:public|private|internal|protected)\s+(?:(?:static|async|virtual)\s+)*[^\n;{}]*\b' + name + r'(?:<[^>]+>)?\([^\n]*\)\s*\{', text)
            if not match:
                raise ValueError(f'Method metadata not found: {relative}::{name}')
            body = text[match.end():text.index('}', match.end())]
            rows.append({'type': relative[:-3].replace('/', '.'), 'method': name,
                         'source': '.local/decompiled/GCUService/' + relative,
                         'line': text.count('\n', 0, match.start()) + 1,
                         'sourceSha256': hashlib.sha256(data).hexdigest(),
                         'body': 'runtime-protected-placeholder' if 'throw new Exception("Runtime exception")' in body else 'requires-review'})
    result = {'schemaVersion': 1, 'serviceSha256': service['sha256'].lower(),
              'methodEvidence': rows,
              'verifiedEdges': [],
              'limitation': 'These are method declarations, not recovered dispatch edges or firmware semantics. No control writes were tested.',
              'blackBoxReadChains': [
                  {'request': 'GETSUPPORT', 'topic': 'Customize/SupportControl', 'observedResponse': 'Customize/SupportInfo'},
                  {'request': 'GETSTATUS', 'topic': 'Fan/Control', 'observedResponse': 'Fan/Status'},
                  {'request': 'System_ON', 'topic': 'System/Control', 'observedResponse': ['System/CpuInfo','System/GpuInfo','System/FanInfo','System/BatteryInfo']}
              ]}
    edges = [
        ('Tray/SystrayComponent/SystrayApplicationContext.cs', 'Service_ClientConnectionEvent', 'SendNumbersTopicToServer',
         'connected=true constructs GETSUPPORT and GETSTATUS; no claim about the service handler'),
        ('Tray/UWP_Refactor.DataService/MQTTDatsService.cs', 'SendNumbersTopicToServer', 'PublishTopic(command.key, command.value)',
         'per-command dispatch under the tray command lock'),
        ('Tray/UWP_Refactor.DataService/MQTTDatsService.cs', 'PublishTopic', 'client.Publish(topic, Encoding.ASCII.GetBytes(msg), (byte)2, retain)',
         'JSON serialization; ASCII; QoS2; default retain=false in original tray only'),
        ('Tray/UWP_Refactor.DataService/MQTTDatsService.cs', 'Client_MqttMsgPublishReceived', 'this.DataChange(e.Topic, val)',
         'UTF8 decode then JSON object parse and topic callback'),
        ('Tray/SystrayComponent/SystrayApplicationContext.cs', 'Service_DataChange', 'Json.ToObjectAsync<MyRamFan1p5>',
         'Fan/Status consumed as MyRamFan1p5; OperatingMode copied to current mode'),
        ('GCUBridge/MQTT_SERVER/MQTTServer.cs', 'PublishTopic', 'WithRetainFlag(true)',
         'bridge helper serializes JSON and publishes retained; does not prove every service response is retained')
    ]
    for relative, caller, token, semantics in edges:
        path = ROOT / '.local/decompiled' / relative
        data = path.read_bytes(); text = data.decode('utf-8-sig')
        position = text.index(token)
        result['verifiedEdges'].append({'source': '.local/decompiled/' + relative,
                                      'sourceSha256': hashlib.sha256(data).hexdigest(),
                                      'line': text.count('\n', 0, position) + 1,
                                      'caller': caller, 'calleeOrOperation': token, 'recoveredSemantics': semantics})
    il_path = ROOT / '.local/analysis/system-controller-il.txt'
    if il_path.exists():
        il = il_path.read_text(encoding='utf-8-sig')
        end = il.index('// end of method MySystemCtrl::Receive')
        body = il[il.rfind('.method', 0, end):end]
        if 'ldstr "Runtime exception"' not in body or 'Code size: 11' not in body:
            raise ValueError('Unexpected Receive IL; re-audit before reporting protection')
        result['ilCrossCheck'] = {'method': 'MySystemCtrl.Receive', 'rva': '0x8095e0', 'codeBytes': 11,
                                 'result': 'throw stub exists in raw IL; not merely a C# decompiler failure',
                                 'artifact': '.local/analysis/system-controller-il.txt',
                                 'sha256': hashlib.sha256(il_path.read_bytes()).hexdigest()}
    output = ROOT / 'protocol/processing-chains.json'
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    print(f'Indexed {len(rows)} target declarations; executable handler bodies remain protected.')


if __name__ == '__main__':
    main()
