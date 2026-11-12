import re, sys, collections
txt = open(sys.argv[1], encoding='utf-8', errors='replace').read().splitlines()
# split into per-device static information blocks
blocks, cur, cid = {}, None, None
for line in txt:
    m = re.match(r'== Camera HAL device device@[\d.]+/internal/(\d+) \(v[\d.]+\) static information: ==', line)
    if m:
        cid = m.group(1); blocks[cid] = []; continue
    if re.match(r'== Camera HAL device .* dumpState: ==', line): cid = None
    if cid is not None: blocks[cid].append(line)
def val(b, key):
    for i, l in enumerate(b):
        if l.strip().startswith(key + ' ('):
            out = []
            for j in range(i + 1, len(b)):
                s = b[j].strip()
                if s.startswith('[') : out.append(s.strip('[] '))
                else: break
            return out
    return []
print(f"{'id':>3} {'facing':6} {'focal':>6} {'array':>10} {'lvl':>3} {'maxPriv':>10} {'maxPrivMaxRes':>13} {'fps ranges (max)':>18}  caps")
for cid, b in sorted(blocks.items(), key=lambda x: int(x[0])):
    facing = ' '.join(val(b, 'android.lens.facing'))
    focal = ' '.join(val(b, 'android.lens.info.availableFocalLengths'))[:6]
    arr = ' '.join(val(b, 'android.sensor.info.pixelArraySize')).replace(' ', 'x')
    lvl = ' '.join(val(b, 'android.info.supportedHardwareLevel'))
    caps = ' '.join(val(b, 'android.request.availableCapabilities'))
    out = []
    for s in val(b, 'android.scaler.availableStreamConfigurations'):
        p = s.split()
        if len(p) == 4 and p[3] == 'OUTPUT' and p[0] == '34': out.append((int(p[1]) * int(p[2]), p[1] + 'x' + p[2]))
    mx = max(out)[1] if out else '-'
    out2 = []
    for s in val(b, 'android.scaler.availableStreamConfigurationsMaximumResolution'):
        p = s.split()
        if len(p) == 4 and p[3] == 'OUTPUT' and p[0] == '34': out2.append((int(p[1]) * int(p[2]), p[1] + 'x' + p[2]))
    mx2 = max(out2)[1] if out2 else '-'
    fr = sorted({int(x.split()[1]) for x in val(b, 'android.control.aeAvailableTargetFpsRanges') if len(x.split()) >= 2} |
                {int(n) for x in val(b, 'android.control.aeAvailableTargetFpsRanges') for n in x.split()[1::2]})
    print(f"{cid:>3} {facing:6} {focal:>6} {arr:>10} {lvl:>3} {mx:>10} {mx2:>13} {str(fr[-1]) if fr else '-':>18}  {caps[:90]}")
