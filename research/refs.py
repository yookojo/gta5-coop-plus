import sys, struct
img = open(sys.argv[1], 'rb').read(); base = int(open(sys.argv[2]).read().strip(), 16)
targets = {name: int(v, 16) - base for name, v in (a.split('=') for a in sys.argv[3:])}
mv = memoryview(img); n = len(img)
# x64 RIP-relative: [REX] opcode modrm(disp32) - find any 4-byte displacement d at i such that i+4+d == target,
# with a ModRM byte before it that encodes RIP-relative (mod=00, rm=101)
counts = {k: 0 for k in targets}; funcs = {k: set() for k in targets}
for i in range(2, n - 4):
    modrm = img[i - 1]
    if (modrm & 0xC7) != 0x05: continue
    d = struct.unpack_from('<i', img, i)[0]
    for k, t in targets.items():
        if i + 4 + d == t or i + 5 + d == t:   # disp ends the instruction, or one imm8 byte follows
            counts[k] += 1; funcs[k].add(i >> 10)
for k in targets: print(f"{k}: {counts[k]} instruction references, in ~{len(funcs[k])} separate 1 KB code blocks")
