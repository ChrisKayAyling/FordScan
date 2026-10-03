"""Parses CyanLabs As-Built database pages (https://cyanlabs.net/asbuilt-db/) into structured items.

Each page is a list of accordion items: a title, one or more locations such as "726-01-01: x*xx-xxxx"
(hex nibbles of the line's data bytes, '*' marks the nibbles the option occupies) and a list of "value=label" lines.
"""
import re, html

def _txt(s):
    s = re.sub(r'<span class="cy-asbuilt-marker[^>]*>(.*?)</span>', r'\1', s)
    s = re.sub(r'<br\s*/?>', '\n', s)
    s = re.sub(r'<[^>]+>', '', s)
    return html.unescape(s).replace('\xa0', ' ').strip()

def parse_page(t):
    items = []
    for it in re.split(r'(?=<div class="[^"]*x-accordion_item")', t)[1:]:
        m = re.search(r'x-accordion_title">(.*?)</span>', it, re.S)
        if not m: continue
        title = _txt(m.group(1))
        sections = {}
        for h, body in re.findall(r'<h4 class="[^"]*brxe-heading">([^<]*)</h4>\s*<div class="[^"]*">(.*?)</div>', it, re.S):
            sections.setdefault(h.strip(), _txt(body))
        items.append({"title": title, "sections": sections})
    return items
