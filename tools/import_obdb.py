#!/usr/bin/env python3
"""Imports the Ford signal sets of the OBDb project (https://github.com/OBDb, CC-BY-SA-4.0) as FordDiag PID definition files.

usage: import_obdb.py <dir_with_downloaded_signalsets> <out_dir>
  input files: <Repo>__<file>.json, e.g. Ford-F-150__default.json, Ford-Ranger__2005-2018.json (OBDb layout signalsets/v3/*.json)

Decoding rules come from the OBDb reference implementation: a mode 22 request "22 hhhh" is answered "62 hhhh <data>";
a signal reads `len` bits starting at bit `bix` (MSB first, default 0) of <data>; value = raw * mul / div + add, clamped to
[min, max] when max > min; `sign` = two's complement; `blsb` = byte-swapped; `nullmin`/`nullmax` mark "no value".
"""
import sys, os, re, json, glob

ATTR = ("Signal definitions from the OBDb project (https://github.com/OBDb), licensed CC-BY-SA-4.0; adapted into compact JSON. "
        "Redistributed under CC-BY-SA-4.0 terms, which are compatible with GPL-3.0 for this combined work.")

def model_name(repo):
    n = repo.replace("-", " ")
    n = re.sub(r"\b([FE]) (\d{3}|Series)\b", r"\1-\2", n)       # F-150, E-Series
    n = n.replace("Mach E", "Mach-E").replace("Focus Electric", "Focus Electric")
    return n

def convert(path):
    repo, fname = os.path.basename(path)[:-5].split("__")
    d = json.load(open(path))
    fy = ty = None
    m = re.match(r"^(\d{4})-(\d{4})$", fname)
    if m:
        fy, ty = int(m.group(1)), int(m.group(2))
        if fy == 0: fy = None
    cmds = []
    for c in d.get("commands", []):
        svc = c.get("cmd", {})
        if list(svc.keys()) != ["22"]: continue
        did = svc["22"]
        did = int(did, 16) if isinstance(did, str) else int(did)
        hdr = c["hdr"]
        if hdr == "7DF":                       # functional broadcast: address the module that answers (response id - 8)
            if not c.get("rax"): continue
            hdr = f"{int(c['rax'], 16) - 8:X}"
        cmd = {"hdr": hdr, "did": f"{did:04X}", "freq": c.get("freq", 5)}
        if c.get("rax") and int(c["rax"], 16) != int(hdr, 16) + 8: continue   # FordDiag addresses modules as request + 8

        flt = c.get("filter") or {}
        if "from" in flt: cmd["from"] = flt["from"]
        if "to" in flt: cmd["to"] = flt["to"]
        if c.get("dbg"): cmd["exp"] = True
        sigs = []
        for s in c.get("signals", []):
            f = s["fmt"]
            sg = {"id": s["id"], "name": s["name"], "len": f["len"]}
            if s.get("path"): sg["group"] = s["path"]
            for k_in, k_out in (("bix", "bix"), ("mul", "mul"), ("div", "div"), ("add", "add"), ("min", "min"), ("max", "max"),
                                ("nullmin", "nullMin"), ("nullmax", "nullMax"), ("unit", "unit")):
                if k_in in f: sg[k_out] = f[k_in]
            if f.get("sign"): sg["signed"] = True
            if f.get("blsb"): sg["blsb"] = True
            if "map" in f:
                sg["map"] = {str(k): (v["description"] if isinstance(v, dict) else str(v)) for k, v in f["map"].items()}
            if s.get("description"): sg["note"] = s["description"]
            sigs.append(sg)
        if sigs:
            cmd["signals"] = sigs
            cmds.append(cmd)
    if not cmds: return None
    ecu = {e["hdr"]: e.get("type", "") for e in (d.get("ecu") or []) if isinstance(e, dict)}
    out = {
        "id": f"obdb-{repo}" + (f"-{fname}" if fname != "default" else ""),
        "name": model_name(repo) + (f" ({fname})" if fname != "default" else ""),
        "repo": repo, "source": f"https://github.com/OBDb/{repo}", "license": "CC-BY-SA-4.0", "attribution": ATTR,
        "commands": cmds,
    }
    if fy: out["fromYear"] = fy
    if ty: out["toYear"] = ty
    if ecu: out["ecu"] = ecu
    return out

def main(src, dst):
    os.makedirs(dst, exist_ok=True)
    total = n = 0
    for p in sorted(glob.glob(os.path.join(src, "*.json"))):
        try: d = convert(p)
        except Exception as e:
            print("skip", p, e); continue
        if not d: continue
        out = os.path.join(dst, d["id"] + ".json")
        json.dump(d, open(out, "w", encoding="utf-8"), separators=(",", ":"), ensure_ascii=False)
        total += os.path.getsize(out); n += 1
        print(f"{d['id']:40} commands={len(d['commands']):3} signals={sum(len(c['signals']) for c in d['commands']):4}")
    print(n, "sets", f"{total/1024:.0f} KB")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
