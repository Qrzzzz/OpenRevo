"""Index local PE copies and decompiled text without loading vendor code.

Dependency: pefile (may be installed into .local/tools/python).
All generated output stays under .local; strings are evidence candidates only.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.local/tools/python'))
import pefile


def decode(value):
    return value.decode('utf-8', errors='replace') if value else None


def main():
    evidence = ROOT / '.local/evidence'
    out = ROOT / '.local/analysis'
    out.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((evidence / 'manifest.json').read_text(encoding='utf-8-sig'))
    images = []
    topic_candidates = []
    for item in manifest['files']:
        path = evidence / Path(item['relative'])
        if path.suffix.lower() not in {'.dll', '.exe', '.sys'}:
            continue
        pe = pefile.PE(str(path))
        exports = []
        for symbol in getattr(getattr(pe, 'DIRECTORY_ENTRY_EXPORT', None), 'symbols', []):
            exports.append({'name': decode(symbol.name), 'ordinal': symbol.ordinal,
                            'rva': hex(symbol.address), 'forwarder': decode(symbol.forwarder)})
        imports = []
        for lib in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', []):
            imports.append({'dll': decode(lib.dll), 'symbols': [decode(s.name) or f'#{s.ordinal}' for s in lib.imports]})
        clr = pe.OPTIONAL_HEADER.DATA_DIRECTORY[14]
        images.append({'file': item['relative'], 'sha256': item['sha256'],
                       'machine': hex(pe.FILE_HEADER.Machine),
                       'clr_directory_present': bool(clr.VirtualAddress and clr.Size),
                       'exports': exports, 'imports': imports})
        data = path.read_bytes()
        strings = [('ascii', m.start(), m.group().decode('ascii'))
                   for m in re.finditer(rb'[\x20-\x7e]{5,}', data)]
        strings += [('utf16le', m.start(), m.group().decode('utf-16le'))
                    for m in re.finditer(rb'(?:[\x20-\x7e]\x00){5,}', data)]
        candidates = []
        for encoding, offset, value in strings:
            if re.fullmatch(r'[A-Za-z][A-Za-z0-9_+.-]*(?:/[A-Za-z0-9_+#.-]+){1,5}', value):
                candidates.append({'file': item['relative'], 'offset': hex(offset),
                                   'encoding': encoding, 'value': value,
                                   'status': 'string-only; direction and handler unverified'})
        topic_candidates.extend(candidates)
        (out / (path.name + '.strings.txt')).write_text(
            '\n'.join(f'{encoding} 0x{offset:x} {value}' for encoding, offset, value in strings), encoding='utf-8')
        pe.close()
    (out / 'pe-index.json').write_text(json.dumps(images, ensure_ascii=False, indent=2), encoding='utf-8')
    (out / 'topic-candidates.json').write_text(json.dumps(topic_candidates, ensure_ascii=False, indent=2), encoding='utf-8')
    stats = []
    for directory in sorted((ROOT / '.local/decompiled').iterdir()):
        files = list(directory.rglob('*.cs'))
        texts = [p.read_text(encoding='utf-8-sig') for p in files]
        stats.append({'assembly': directory.name, 'cs_files': len(files),
                      'runtime_exception_stubs': sum(t.count('throw new Exception("Runtime exception")') for t in texts)})
    (out / 'decompilation-stats.json').write_text(json.dumps(stats, indent=2), encoding='utf-8')
    print(json.dumps({'pe_files': len(images), 'topic_candidates': len(topic_candidates), 'decompilation': stats}))


if __name__ == '__main__':
    main()
