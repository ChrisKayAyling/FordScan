#!/usr/bin/env python3
"""Imports the CyanLabs As-Built database (https://cyanlabs.net/asbuilt-db/, free to use) as FordDiag definition files.

usage: import_cyanlabs.py <cache_dir_with_html_and_list.json> <out_dir>
  cache_dir: one <slug>.html per database page plus list.json (WordPress REST listing: id, slug, title, link, excerpt)

Location masks ("726-01-01: x*xx-xxxx") are hex nibbles of the whole as-built line, including the 2-nibble checksum at the
end; '*' marks the nibbles an option occupies and the listed "value=label" lines are the values of those nibbles.
"""
import sys, os, re, json, html, collections
sys.path.insert(0, os.path.dirname(__file__))
from cyanlabs_parse import parse_page

LOC = re.compile(r'^(?:DE[0-9A-F]{2}/)?0?([0-9A-F]{3})-([0-9A-F]{2})-([0-9A-F]{2}):\s*([x*\-\s]+)$')
PN = re.compile(r'(?<![A-Z0-9])([A-Z0-9]{4})(?![A-Z0-9])')

def part_prefixes(text):
    out = []
    for t in PN.findall(text):
        if re.search(r'\d', t) and re.search(r'[A-Z]', t) and not re.fullmatch(r'\d{4}', t) and t not in out and not re.fullmatch(r'(19|20)\d\d', t):
            out.append(t)
    return out

def parse_values(text, nibbles):
    """-> (options {int: label}, unit_info dict or None)"""
    opts, info = {}, None
    maxv = 16 ** nibbles
    for line in [l.strip() for l in text.split("\n") if l.strip()]:
        m = re.match(r'^([0-9A-Fa-f]+)\s*=\s*(.+)$', line)
        if m:
            v = int(m.group(1), 16)
            if v < maxv: opts.setdefault(v, m.group(2).strip())
            continue
        m = re.match(r'^([0-9A-Fa-f]+)\s*(?:-|thru)\s*([0-9A-Fa-f]+)\s*=\s*(.+)$', line)
        if m:
            a, b = int(m.group(1), 16), int(m.group(2), 16)
            if 0 <= b - a <= 255:
                for v in range(a, b + 1):
                    if v < maxv: opts.setdefault(v, m.group(3).strip())
            continue
        if re.search(r'Hex2Dec', line):
            um = re.match(r'^[^=,]*,\s*([^=]+?)\s*=\s*Hex2Dec', line)
            info = {"kind": "value", "unit": (um.group(1).strip() if um else None), "multiplier": 1, "offset": 0}
            continue
        m = re.match(r'^\(?HEX\s*=\s*DEC\s*x\s*([0-9.]+)\s*([+-])\s*([0-9.]+)\s*=\s*Value\s*(?:\(([^)]*)\))?', line)
        if m:
            info = {"kind": "value", "unit": (m.group(4) or "").strip() or None, "multiplier": float(m.group(1)),
                    "offset": float(m.group(3)) * (1 if m.group(2) == "+" else -1)}
            continue
        pairs = re.findall(r'(?:^|[:,]\s*)([0-9A-Fa-f]+)\s*=\s*([^,]+)', line)
        if len(pairs) >= 2:
            for k, lab in pairs:
                v = int(k, 16)
                if v < maxv: opts.setdefault(v, lab.strip())
    return opts, info

def parse_location(line):
    m = LOC.match(line.strip())
    if not m: return None
    module, block, ln, pat = int(m.group(1), 16), int(m.group(2), 16), int(m.group(3), 16), m.group(4)
    nib = re.sub(r'[\-\s]', '', pat)
    if len(nib) < 4 or len(nib) % 2: return None
    data_nibbles = len(nib) - 2
    marked = [i for i, c in enumerate(nib) if c == '*']
    if not marked or marked[-1] >= data_nibbles or marked != list(range(marked[0], marked[0] + len(marked))) or len(marked) > 8:
        return None
    return module, block, ln, data_nibbles // 2, marked[0], len(marked)

def build(slug, title, excerpt, link, items, stats):
    by_module = collections.defaultdict(list)
    for it in items:
        locs = [parse_location(l) for l in it["sections"].get("Location", "").split("\n") if l.strip()]
        locs = [l for l in locs if l]
        if not locs: stats["skipped_items"] += 1; continue
        by_module[locs[0][0]].append((it, locs))
    results = []
    for module, entries in by_module.items():
        widths = collections.defaultdict(dict)       # block -> {line: data bytes}
        conflicts = collections.defaultdict(set)     # block -> lines whose items disagree about the width
        for it, locs in entries:
            for (mod, b, ln, w, i0, k) in locs:
                if mod != module: continue
                if ln in widths[b] and widths[b][ln] != w: conflicts[b].add(ln); stats["width_conflicts"] += 1
                widths[b][ln] = max(w, widths[b].get(ln, 0))   # the widest mask wins so every option fits
        blocks = []
        for b in sorted(widths):
            known = widths[b]
            last = max(known)
            default = collections.Counter(known.values()).most_common(1)[0][0]
            line_w = [known.get(ln, default) for ln in range(1, last + 1)]
            guessed = any(ln not in known for ln in range(1, last + 1)) or bool(conflicts[b])
            offs, o = {}, 0
            for ln, w in enumerate(line_w, 1): offs[ln] = o; o += w
            fields = []
            for it, locs in entries:
                for n, (mod, bb, ln, w, i0, k) in enumerate(locs):
                    if mod != module or bb != b: continue
                    start = offs[ln] * 8 + i0 * 4; size = k * 4; end = start + size
                    first, lastb = start // 8, (end - 1) // 8
                    opts, info = parse_values(it["sections"].get("Values", ""), k)
                    ftitle = " · ".join(x.strip() for x in it["title"].split("\n") if x.strip())
                    f = {"name": ftitle + (f" ({n + 1})" if len(locs) > 1 else ""),
                         "byte": first, "bit": (lastb + 1) * 8 - end, "size": size}
                    if opts and not info:
                        f["kind"] = "enum"; f["options"] = {str(v): s for v, s in sorted(opts.items())}
                    else:
                        f["kind"] = "value"
                        if info:
                            for kk in ("unit", "multiplier", "offset"):
                                if info.get(kk) not in (None, 1, 0) or kk == "multiplier" and info.get(kk) != 1: f[kk] = info[kk]
                    note = it["sections"].get("Notes")
                    if note: f["note"] = note
                    f["loc"] = f"{module:03X}-{b:02X}-{ln:02X}"
                    fields.append(f)
                    stats["fields"] += 1
            fields.sort(key=lambda f: (f["byte"], f["bit"]))
            span = max((f["byte"] + (f["bit"] + f["size"] + 7) // 8 for f in fields), default=0)
            blocks.append({"block": b, "lineBytes": line_w, "minSize": max(o, span), "layoutGuessed": guessed, "fields": fields})
        name = re.sub(r'\s*Database\s*$', '', title).strip()
        d = {"id": f"cyan-{slug}" + (f"-{module:03X}" if len(by_module) > 1 else ""), "name": name, "module": f"{module:03X}",
             "source": link, "license": "Free to use (CyanLabs As-Built Database)",
             "attribution": "Option definitions imported from the CyanLabs As-Built Database (https://cyanlabs.net/asbuilt-db/), which is free to use. Credit to the CyanLabs community.",
             "notes": html.unescape(re.sub(r'<[^>]+>', '', excerpt)).strip(),
             "partNumberPrefixes": part_prefixes(title + " " + re.sub(r'<[^>]+>', ' ', excerpt)),
             "blocks": blocks}
        results.append(d)
    return results

def main(cache, out):
    os.makedirs(out, exist_ok=True)
    listing = {x["slug"]: x for x in json.load(open(os.path.join(cache, "list.json")))}
    stats = collections.Counter()
    total = 0
    for slug, meta in sorted(listing.items()):
        f = os.path.join(cache, slug + ".html")
        if not os.path.exists(f): continue
        items = parse_page(open(f, encoding="utf-8").read())
        stats["items"] += len(items)
        for d in build(slug, html.unescape(meta["title"]["rendered"]), meta.get("excerpt", {}).get("rendered", ""), meta["link"], items, stats):
            path = os.path.join(out, d["id"] + ".json")
            json.dump(d, open(path, "w", encoding="utf-8"), separators=(",", ":"), ensure_ascii=False)
            total += os.path.getsize(path)
            print(f"{d['id']:50} {d['module']} blocks={len(d['blocks'])} fields={sum(len(b['fields']) for b in d['blocks'])} pn={d['partNumberPrefixes'][:4]}")
    print(dict(stats), f"{total/1024:.0f} KB")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
