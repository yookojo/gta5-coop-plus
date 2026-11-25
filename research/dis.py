import sys, capstone, struct
img = open(sys.argv[1], 'rb').read(); base = int(open(sys.argv[2]).read().strip(), 16); off = int(sys.argv[3], 16); n = int(sys.argv[4])
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = False
for ins in md.disasm(img[off:off + n], base + off):
    rel = ins.address - base
    line = f"+{rel:07X}  {ins.mnemonic:6} {ins.op_str}"
    if 'rip' in ins.op_str:
        try:
            disp = int(ins.op_str.split('rip')[1].split(']')[0].replace(' ', ''), 16)
            line += f"    ; -> +{ins.address + ins.size + disp - base:07X}"
        except Exception: pass
    elif ins.mnemonic in ('call', 'jmp') and ins.op_str.startswith('0x'):
        line += f"    ; -> +{int(ins.op_str, 16) - base:07X}"
    print(line)
    if ins.mnemonic == 'ret' or (ins.mnemonic == 'int3'): break
