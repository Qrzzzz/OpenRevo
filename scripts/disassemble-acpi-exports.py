"""Disassemble copied native export ranges; never load the DLL or driver."""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / '.local/tools/python'))
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86_const import X86_OP_MEM, X86_REG_RIP

pe = pefile.PE(str(ROOT / '.local/evidence/AiStoneService/MyControlCenter/ACPIDriverDll.dll'))
base = pe.OPTIONAL_HEADER.ImageBase
imports = {s.address: f'{lib.dll.decode()}!{s.name.decode() if s.name else s.ordinal}'
           for lib in pe.DIRECTORY_ENTRY_IMPORT for s in lib.imports}
exports = sorted(pe.DIRECTORY_ENTRY_EXPORT.symbols, key=lambda s: s.address)
cs = Cs(CS_ARCH_X86, CS_MODE_64)
cs.detail = True
lines = ['Static linear disassembly of export ranges; not a complete control-flow analysis.']
for index, symbol in enumerate(exports):
    size = min(exports[index + 1].address - symbol.address, 2048) if index + 1 < len(exports) else 512
    lines.append(f'\nEXPORT {symbol.name.decode()} RVA 0x{symbol.address:x}')
    # Keep conditional success paths after an early error return.
    for instruction in cs.disasm(pe.get_data(symbol.address, size), base + symbol.address):
        annotation = ''
        if instruction.mnemonic in {'call', 'jmp'}:
            for op in instruction.operands:
                if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                    target = instruction.address + instruction.size + op.mem.disp
                    annotation = ' ; ' + imports.get(target, f'IAT/indirect 0x{target:x}')
        lines.append(f'{instruction.address:x}: {instruction.mnemonic} {instruction.op_str}{annotation}')
output = ROOT / '.local/analysis/acpi-export-disassembly.txt'
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text('\n'.join(lines), encoding='utf-8')
pe.close()
print(f'Indexed {len(exports)} native export ranges.')
