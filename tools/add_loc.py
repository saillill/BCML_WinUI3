#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""往 Loc.cs 的两张词表里**正确地**加/删词条。

为什么专门写个工具：手工用字符串下标插过三次，三次都把 en 词条插到了类级别
（`[En] = new()` 那一行之前），直接把文件写成语法错误。这里的做法是
先定位 `[Zh] = new()` / `[En] = new()`，再用**花括号配平**找到各自的结束位置，
只在块内插入。

用法：
    python tools/add_loc.py add  <key> <zh文本> <en文本>
    python tools/add_loc.py del  <key>
    python tools/add_loc.py list
"""
import re
import sys
from pathlib import Path

CORE = Path(__file__).resolve().parent.parent / "src" / "BCML.WinUI3.Core" / "Services" / "Loc.cs"


def read() -> str:
    return CORE.read_text(encoding="utf-8")


def block_end(src: str, start: int) -> tuple[int, int]:
    """从 `[X] = new()` 的位置出发，返回 (块内插入点, 块结束位置)。

    插入点 = 结束花括号所在行的行首；两个下标都在 src 的字符坐标上。
    """
    brace = src.index("{", start)          # `new()` 后面那个 `{`
    depth = 0
    i = brace
    while i < len(src):
        c = src[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                line_start = src.rfind("\n", 0, i) + 1
                return line_start, i
        i += 1
    raise ValueError("花括号没有配平，Loc.cs 结构可能被改坏了")


def insert(src: str, marker: str, line: str) -> str:
    at = src.index(marker)
    at, _ = block_end(src, at)
    indent = " " * 12
    return src[:at] + indent + line + "\n" + src[at:]


def find_block_end(src: str, marker: str) -> int:
    return block_end(src, src.index(marker))[1]


def js(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def cmd_add(key: str, zh: str, en: str) -> None:
    src = read()
    entry_zh = f'["{key}"] = {js(zh)},'
    entry_en = f'["{key}"] = {js(en)},'
    if f'["{key}"]' in src:
        print(f"已存在，未改动：{key}")
        return
    src = insert(src, "[En] = new()", entry_en)     # 先插 En（后面的下标不受影响）
    src = insert(src, "[Zh] = new()", entry_zh)
    CORE.write_text(src, encoding="utf-8")
    print(f"已添加 {key}")


def cmd_del(key: str) -> None:
    src = read()
    want = f'["{key}"]'
    kept, removed = [], 0
    for line in src.split("\n"):
        if want in line and line.strip().startswith(want.strip().replace('"]', '"]')) and "=" in line and line.rstrip().endswith(","):
            removed += 1
            continue
        kept.append(line)
    CORE.write_text("\n".join(kept), encoding="utf-8")
    print(f"已删除 {removed} 行（{key}）")


def cmd_list() -> None:
    src = read()
    i = src.index("[En] = new()")
    zh = re.findall(r'^\s*\["([^"]+)"\]\s*=', src[:i], re.M)
    en = re.findall(r'^\s*\["([^"]+)"\]\s*=', src[i:], re.M)
    print(f"zh {len(zh)} / en {len(en)}")
    print("  仅 zh:", sorted(set(zh) - set(en)) or "无")
    print("  仅 en:", sorted(set(en) - set(zh)) or "无")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    op = sys.argv[1]
    if op == "add":
        cmd_add(sys.argv[2], sys.argv[3], sys.argv[4])
    elif op == "del":
        cmd_del(sys.argv[2])
    elif op == "list":
        cmd_list()
    else:
        print(__doc__)
        sys.exit(2)
