#!/usr/bin/env python3
"""Converts the Ford APIM as-built field definitions of consp/apim-asbuilt-decode (GPL-3.0) into FordDiag JSON.

usage: convert_apim_definitions.py path/to/statics.py out_dir
The Python source is parsed with ast (never executed).
Definition source: https://github.com/consp/apim-asbuilt-decode  (src/statics.py, GPL-3.0)
"""
import ast, json, sys, os

src, out_dir = sys.argv[1], sys.argv[2]
tree = ast.parse(open(src, encoding="utf-8").read())
classes = {n.name: n for n in tree.body if isinstance(n, ast.ClassDef)}

def literals(cls, prefix=None):
    res = {}
    for st in cls.body:
        if isinstance(st, ast.Assign) and isinstance(st.targets[0], ast.Name):
            name = st.targets[0].id
            if prefix and not name.startswith(prefix): continue
            try: res[name] = ast.literal_eval(st.value)
            except Exception: pass
    return res

tables = literals(classes["JumpTables"])
def resolve(name):
    key = name.lstrip("_")
    cands = [(len(v), v) for k, v in tables.items() if k.lstrip("_") == key and isinstance(v, list)]
    return max(cands)[1] if cands else None

def convert(fields):
    blocks, skipped = [], []
    for de in sorted(k for k in fields if k.startswith("de")):
        out = []
        for f in fields[de]:
            t, size = f["type"], f["size"]
            # upstream 'bit' counts from the most significant bit of the byte (bit 0 = 0x80); FordDiag stores the offset of
            # the least significant bit of the field inside its last byte
            start = f["byte"] * 8 + f["bit"]
            end = start + size
            if size > 32: skipped.append((de, f["name"])); continue
            bit = ((end - 1) // 8 + 1) * 8 - end
            fld = {"name": f["name"].strip(), "byte": start // 8, "bit": bit, "size": size}
            if t == "mask":
                opts = {k: v for k, v in f.items() if k.isdigit()}
                fld["kind"] = "enum"; fld["options"] = opts
            elif t == "table":
                lst = resolve(f.get("table", ""))
                if lst is None or size > 16: skipped.append((de, f["name"])); continue
                fld["kind"] = "enum"; fld["options"] = {str(i): s for i, s in enumerate(lst)}
            elif t == "mul":
                fld["kind"] = "value"
                fld.update({k: f[k] for k in ("multiplier", "offset", "unit", "min", "max") if k in f})
            elif t == "ascii":
                fld["kind"] = "ascii"
            else: skipped.append((de, f["name"])); continue
            out.append(fld)
        blocks.append({"block": int(de[2:], 16) + 1, "fields": out})
    return blocks, skipped

sets = [
    ("Fields", "apim-sync3", "Ford APIM (SYNC 3)", [10, 12, 5, 7, 6, 1, 16, 10, 20, 20]),
    ("Fields_s4", "apim-sync4", "Ford APIM (SYNC 4)", [20, 15, 15, 5, 15, 6, 16, 10, 25, 25]),
]
for cls, id_, name, sizes in sets:
    blocks, skipped = convert(literals(classes[cls], "de"))
    d = {
        "id": id_, "name": name, "module": "7D0", "blockSizes": sizes,
        "source": "https://github.com/consp/apim-asbuilt-decode",
        "license": "GPL-3.0",
        "attribution": "Field definitions derived from consp/apim-asbuilt-decode (GPL-3.0), which extracted them from QNX application debug information and community findings.",
        "blocks": blocks,
    }
    path = os.path.join(out_dir, id_ + ".json")
    json.dump(d, open(path, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    print(path, sum(len(b["fields"]) for b in blocks), "fields,", len(skipped), "skipped", skipped[:4])
