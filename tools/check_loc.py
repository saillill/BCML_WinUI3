#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校验 Loc.cs 两张词表：有没有断行、有没有只在一侧、有没有重复。"""
import collections
import re
import sys
from pathlib import Path

P = Path(__file__).resolve().parent.parent / "src" / "BCML.WinUI3.Core" / "Services" / "Loc.cs"
src = P.read_text(encoding="utf-8")

good = re.compile(r'^\s*\["[^"]+"\] = ".*",$')
bad = [l for l in src.split(chr(10))
       if l.strip().startswith('["') and not (good.match(l) and l.count('["') == 1)]

i = src.index("[En] = new()")
zh = re.findall(r'^\s*\["([^"]+)"\]\s*=', src[:i], re.M)
en = re.findall(r'^\s*\["([^"]+)"\]\s*=', src[i:], re.M)

print(f"不合法行: {len(bad)}")
for b in bad[:5]:
    print("   ", repr(b[:130]))
print(f"zh {len(zh)} / en {len(en)}")
print("  仅 zh:", sorted(set(zh) - set(en)) or "无")
print("  仅 en:", sorted(set(en) - set(zh)) or "无")
dzh = [k for k, c in collections.Counter(zh).items() if c > 1]
den = [k for k, c in collections.Counter(en).items() if c > 1]
print("  重复 zh:", dzh or "无", " en:", den or "无")

sys.exit(1 if (bad or dzh or den or set(zh) != set(en)) else 0)
