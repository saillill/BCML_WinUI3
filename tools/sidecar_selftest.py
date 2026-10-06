#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""sidecar 自测：拉起 rpc_server.py，走一遍定长帧 RPC，打印结果。

用法:
    python sidecar_selftest.py [--py <解释器路径>]
"""
import json
import os
import struct
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SERVER = os.path.join(ROOT, "sidecar", "rpc_server.py")
DEFAULT_PY = r"C:\Users\z2637\AppData\Local\Programs\Python\Python39\python.exe"


class Client:
    def __init__(self, py):
        self.p = subprocess.Popen(
            [py, SERVER],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            cwd=ROOT, bufsize=0,
        )
        self._id = 0
        self._lock = threading.Lock()
        self.notifications = []
        self.stderr_lines = []
        threading.Thread(target=self._pump_stderr, daemon=True).start()

    def _pump_stderr(self):
        for raw in self.p.stderr:
            line = raw.decode("utf-8", "replace").rstrip()
            if line:
                self.stderr_lines.append(line)
                if len(self.stderr_lines) > 200:
                    del self.stderr_lines[:100]

    def _send(self, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        with self._lock:
            self.p.stdin.write(struct.pack("<I", len(body)))
            self.p.stdin.write(body)
            self.p.stdin.flush()

    def _read_exact(self, n):
        buf = b""
        while len(buf) < n:
            c = self.p.stdout.read(n - len(buf))
            if not c:
                return b""
            buf += c
        return buf

    def call(self, method, params=None, timeout=180):
        self._id += 1
        my_id = self._id
        self._send({"jsonrpc": "2.0", "id": my_id, "method": method, "params": params})
        deadline = time.time() + timeout
        while time.time() < deadline:
            hdr = self._read_exact(4)
            if len(hdr) < 4:
                raise RuntimeError("sidecar 提前退出")
            (n,) = struct.unpack("<I", hdr)
            msg = json.loads(self._read_exact(n).decode("utf-8"))
            if msg.get("id") == my_id:
                if "error" in msg:
                    raise RuntimeError(f"{msg['error']['code']}: {msg['error']['message']}")
                return msg["result"]
            if msg.get("method"):
                self.notifications.append(msg)
        raise TimeoutError(method)

    def close(self):
        try:
            self.p.stdin.close()
        except Exception:
            pass
        try:
            self.p.wait(timeout=5)
        except Exception:
            self.p.kill()


def main():
    py = DEFAULT_PY
    if "--py" in sys.argv:
        py = sys.argv[sys.argv.index("--py") + 1]
    print(f"解释器: {py}")
    c = Client(py)
    try:
        print("\n--- sys.ping ---")
        print(c.call("sys.ping", timeout=120))
        print("\n--- sys.info ---")
        info = c.call("sys.info")
        for k, v in info.items():
            if k == "settings":
                print(f"  settings: <{len(v)} 项>")
            else:
                print(f"  {k}: {v}")
        print("\n--- api.get_mods ---")
        res = c.call("api.get_mods", {"disabled": True})
        data = res.get("data") if isinstance(res, dict) else res
        print(f"  success={res.get('success')}  mods={len(data)}")
        for m in data[:6]:
            print(f"   {m['priority']:04d} {'禁用' if m['disabled'] else '启用'} {m['name']}")
        print("\n--- 需要界面的方法应返回 -32010 ---")
        try:
            c.call("api.get_folder", timeout=20)
            print("  ✗ 没有报错（意外）")
        except RuntimeError as e:
            print(f"  ✓ {e}")
        print("\n--- 不存在的方法应返回 -32601 ---")
        try:
            c.call("api.not_a_real_method")
            print("  ✗ 没有报错（意外）")
        except RuntimeError as e:
            print(f"  ✓ {e}")
        print(f"\n收到的通知: {len(c.notifications)} 条")
        for n in c.notifications[:3]:
            print(f"   {n['method']}")
        print(f"\nstderr 尾部:")
        for line in c.stderr_lines[-5:]:
            print(f"   {line}")
    finally:
        c.close()


if __name__ == "__main__":
    main()
