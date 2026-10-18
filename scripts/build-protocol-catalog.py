"""Build provenance-backed catalogs from existing static analysis; no transport."""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'protocol'
OUT.mkdir(exist_ok=True)


def write(name, data):
    (OUT / name).write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def location(path, text, token):
    position = text.index(token)
    return {'path': path.relative_to(ROOT).as_posix(), 'line': text[:position].count('\n') + 1}


topic_path = ROOT / '.local/decompiled/Tray/UWP_Refactor.DataService/Topic.cs'
topic_text = topic_path.read_text(encoding='utf-8-sig')
topics = [{'symbol': m[1], 'topic': m[2], 'evidence': 'decompiled-constant-only',
           'source': location(topic_path, topic_text, m[0]), 'live_verified': False}
          for m in re.finditer(r'public const string (\w+) = "([^"]+)";', topic_text)]
write('declared-topics.json', {'status': 'static-only', 'topics': topics})

tray_path = ROOT / '.local/decompiled/Tray/SystrayComponent/SystrayApplicationContext.cs'
tray_text = tray_path.read_text(encoding='utf-8-sig')
commands = []
for action, topic, meaning, query in [
    ('GETSUPPORT', 'Customize/SupportControl', '查询能力（厂商发送端意图）', True),
    ('GETSTATUS', 'Fan/Control', '查询状态（厂商发送端意图）', True),
    ('OPERATING_GAMING_MODE', 'Fan/Control', '切换游戏模式', False),
    ('OPERATING_OFFICE_MODE', 'Fan/Control', '切换办公模式', False),
    ('OPERATING_TURBO_MODE', 'Fan/Control', '切换狂暴模式', False),
    ('OPERATING_CUSTOM_MODE', 'Fan/Control', '切换自定义模式', False),
]:
    token = f'Action = "{action}"'
    source = location(tray_path, tray_text, token)
    position = tray_text.index(token)
    assert f'"{topic}"' in tray_text[max(0, position - 160):position]
    commands.append({'topic': topic, 'payload': {'Action': action}, 'meaning': meaning,
                     'query_intent': query, 'sender_evidence': source,
                     'handler_semantics': 'unrecovered-runtime-protected',
                     'live_verified': False, 'enabled': False})
write('commands.json', {'status': 'static-sender-confirmed; not an executable allowlist',
                       'transport_implemented': False, 'commands': commands})

acpi_path = ROOT / '.local/decompiled/GCUService/MyECIO/AcpiCtrl.cs'
acpi_text = acpi_path.read_text(encoding='utf-8-sig')
constants = {int(m[2]): m[1] for m in re.finditer(r'public const uint (IOCTL_\w+) = (\d+)u;', acpi_text)}
disassembly = (ROOT / '.local/analysis/acpi-export-disassembly.txt').read_text(encoding='utf-8')
exports = []
for match in re.finditer(r'EXPORT (\w+) RVA (0x[0-9a-f]+)\n(.*?)(?=\nEXPORT|\Z)', disassembly, re.S):
    native_codes = re.findall(r'mov edx, (0x9c40[0-9a-f]+)', match[3])
    assert len(native_codes) == 1, (match[1], native_codes)
    code = int(native_codes[0], 16)
    assert code in constants, (match[1], code)
    exports.append({'export': match[1], 'export_rva': match[2], 'ioctl': hex(code),
                    'managed_constant': constants[code],
                    'evidence': ['native-export-disassembly', 'managed-constant'],
                    'driver_handler_verified': False, 'live_verified': False, 'enabled': False})
assert len(exports) == 19
write('native-ioctls.json', {'status': 'static-cross-checked; payload ABI and driver handlers incomplete',
                           'exports': exports})

model_path = ROOT / '.local/decompiled/Tray/SystrayComponent/MyRamFan1p5.cs'
model_text = model_path.read_text(encoding='utf-8-sig')
fields = [{'type': m[1], 'name': m[2], 'source': location(model_path, model_text, m[0]),
           'live_observed': False, 'units_and_valid_ranges': None}
          for m in re.finditer(r'public (string|bool|int|float|double) (\w+);', model_text)]
write('fan-status-fields.json', {'status': 'vendor client model; not complete live response schema', 'fields': fields})
print(f'Cataloged {len(topics)} declared topics, {len(commands)} sender commands, '
      f'{len(exports)} cross-checked IOCTLs, {len(fields)} fan/status model fields.')
