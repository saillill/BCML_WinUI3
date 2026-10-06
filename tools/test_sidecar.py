#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""sidecar 的自动化回归测试。

覆盖三块最容易悄悄坏掉、又最难靠手点发现的东西：

  1. **协议健壮性** —— 畸形帧不能把后端打死（一条 JSON 数组里塞个整数就崩掉整个
     sidecar 这种事故，靠人工是测不出来的）；
  2. **请求分流** —— 长任务（合并 / 统计改动）在跑的时候，`sys.logs` 这类纯内存读
     必须还能立刻回；这条是"界面看起来卡死"的根因，必须锁住；
  3. **输入校验** —— 路径穿越、`probePython` 的几道闸。

用法：
    python tools/test_sidecar.py            # 直接跑（unittest，无需额外依赖）
    python -m pytest tools/test_sidecar.py  # 装了 pytest 也能跑

需要一个可用的 Python 3.9 + bcml（内嵌运行时或本机装的都行）。
退出码 0 = 全部通过。
"""
from __future__ import annotations

import json
import os
import struct
import subprocess
import sys
import threading
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
RPC_SERVER = ROOT / "sidecar" / "rpc_server.py"


def find_python() -> str:
    """优先用随包的内嵌运行时，其次本机 Python 3.9。"""
    local = os.environ.get("LOCALAPPDATA", "")
    candidates = [
        ROOT / "publish" / "win-x64" / "runtime" / "python39" / "python.exe",
        Path(local) / "Programs" / "Python" / "Python39" / "python.exe",
        Path(r"C:\Python39\python.exe"),
    ]
    for c in candidates:
        if c.is_file():
            return str(c)
    raise SystemExit("找不到可用的 Python 3.9（内嵌运行时或本机安装）")


class Sidecar:
    """按定长帧跟 sidecar 说话的最小客户端。"""

    def __init__(self) -> None:
        env = dict(os.environ)
        env["PYTHONNOUSERSITE"] = "1"
        env["PYTHONUNBUFFERED"] = "1"
        env["PYTHONIOENCODING"] = "utf-8"
        self.proc = subprocess.Popen(
            [find_python(), str(RPC_SERVER)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            cwd=str(RPC_SERVER.parent), env=env,
        )
        self._lock = threading.Lock()
        self._next_id = 0
        self.notifications: list[dict] = []
        # stderr 必须持续抽干：sidecar 把 BCML 的所有 print 都转发到 stderr，
        # 管道写满（64KB）之后 sidecar 会阻塞在 write 上、从此不再应答。
        threading.Thread(target=self._drain_stderr, daemon=True).start()

    def _drain_stderr(self) -> None:
        for _ in iter(self.proc.stderr.readline, b""):
            pass

    def _write(self, obj) -> None:
        body = json.dumps(obj).encode("utf-8")
        with self._lock:
            self.proc.stdin.write(struct.pack("<I", len(body)) + body)
            self.proc.stdin.flush()

    def _read_frame(self):
        hdr = self.proc.stdout.read(4)
        if len(hdr) < 4:
            return None
        (n,) = struct.unpack("<I", hdr)
        raw = self.proc.stdout.read(n)
        if len(raw) < n:
            return None
        return json.loads(raw.decode("utf-8"))

    # --- 同步调用（跳过通知） ---
    def call(self, method, params=None, timeout=120):
        self._next_id += 1
        ident = self._next_id
        self._write({"jsonrpc": "2.0", "id": ident, "method": method, "params": params})
        deadline = time.time() + timeout
        while time.time() < deadline:
            msg = self._read_frame()
            if msg is None:
                raise RuntimeError("sidecar 意外退出")
            if "id" not in msg:
                self.notifications.append(msg)
                continue
            if msg.get("id") == ident:
                return msg
        raise TimeoutError(f"{method} 超时")

    def send_raw(self, payload) -> None:
        """发一段任意 JSON（可以是数组、字符串、数字 —— 用来测畸形输入）。"""
        self._write(payload)

    def read_until_ids(self, ids, timeout=180):
        """读到指定 id 全部到齐，返回 {id: 到达序号}。"""
        order: dict = {}
        seen = 0
        deadline = time.time() + timeout
        while time.time() < deadline and len(order) < len(ids):
            msg = self._read_frame()
            if msg is None:
                break
            if "id" not in msg:
                self.notifications.append(msg)
                continue
            if msg["id"] in ids and msg["id"] not in order:
                order[msg["id"]] = seen
                seen += 1
        return order

    def close(self) -> None:
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()


class SidecarTestCase(unittest.TestCase):
    """每个测试类共用一个 sidecar（冷启动 import bcml 要好几秒，别每题都起）。"""

    sidecar: Sidecar

    @classmethod
    def setUpClass(cls) -> None:
        cls.sidecar = Sidecar()
        cls.sidecar.call("sys.info", timeout=180)

    @classmethod
    def tearDownClass(cls) -> None:
        cls.sidecar.close()


class ProtocolTests(SidecarTestCase):
    """畸形请求不能让后端起不来。"""

    def test_ping(self):
        r = self.sidecar.call("sys.ping")
        self.assertTrue(r["result"]["ok"])
        self.assertGreater(r["result"]["pid"], 0)

    def test_batch_with_non_object_element_is_rejected_not_fatal(self):
        """数组里混进数字/字符串 —— 以前会 AttributeError 穿透主循环、整个进程带
        traceback 退出，界面表现为所有调用同时失败。"""
        ident = 90001
        self.sidecar.send_raw([
            {"jsonrpc": "2.0", "id": ident, "method": "sys.ping"},
            12345,
            "hello",
        ])
        order = self.sidecar.read_until_ids({ident}, timeout=30)
        self.assertIn(ident, order, "批量请求里的正常项应当照常应答")

        # 进程必须还活着，并且还能继续服务
        self.assertIsNone(self.sidecar.proc.poll(), "sidecar 不应因畸形输入退出")
        r = self.sidecar.call("sys.ping")
        self.assertTrue(r["result"]["ok"])

    def test_non_string_method_is_rejected(self):
        ident = 90002
        self.sidecar.send_raw({"jsonrpc": "2.0", "id": ident, "method": {"a": 1}})
        order = self.sidecar.read_until_ids({ident}, timeout=30)
        self.assertIn(ident, order)
        self.assertIsNone(self.sidecar.proc.poll())

    def test_unknown_method_is_32601(self):
        r = self.sidecar.call("api.not_a_real_method_at_all")
        self.assertEqual(r["error"]["code"], -32601)

    def test_needs_ui_is_32010(self):
        r = self.sidecar.call("api.get_folder")
        self.assertEqual(r["error"]["code"], -32010)

    def test_bad_params_is_32602(self):
        # get_user_langs 需要 dir 参数；不给会 TypeError
        r = self.sidecar.call("api.get_user_langs", {})
        self.assertIn(r.get("error", {}).get("code"), (-32602, -32000))


class DispatchTests(SidecarTestCase):
    """长任务不能把只读请求堵死。"""

    def test_logs_answers_while_slow_request_is_running(self):
        """先发一个慢请求（统计改动要遍历模组文件），紧接着发 sys.logs。

        worker 架构下 logs 走内存快路径，应当**先于**慢请求回来；
        旧实现里 logs 排在读循环后面，只能等慢请求跑完 —— 那正是"界面卡死"的来源。
        """
        mods = self.sidecar.call("api.get_mods", {"disabled": True})["result"]["data"]
        self.assertTrue(mods, "需要至少一个已安装模组")
        mod_path = mods[0]["path"]

        slow_id, fast_id = 91001, 91002
        self.sidecar.send_raw({"jsonrpc": "2.0", "id": slow_id,
                               "method": "sys.modDetails",
                               "params": {"mod": mod_path, "edits": True}})
        self.sidecar.send_raw({"jsonrpc": "2.0", "id": fast_id,
                               "method": "sys.logs", "params": {"tail": 20}})
        order = self.sidecar.read_until_ids({slow_id, fast_id}, timeout=300)

        self.assertIn(slow_id, order)
        self.assertIn(fast_id, order)
        self.assertLess(
            order[fast_id], order[slow_id],
            "sys.logs 应当先于长任务返回（否则长任务又把读循环堵死了）",
        )

    def test_worker_survives_after_slow_request(self):
        r = self.sidecar.call("sys.ping")
        self.assertTrue(r["result"]["ok"], "worker 不应在长任务后失效")


class RemergeTests(SidecarTestCase):
    """重新合并必须能跑完。

    这条是为一个真实故障加的回归：BCML 的 refresh_merges() 会开
    multiprocessing.Pool，而 spawn 出来的 worker 在 sidecar 里起不来，
    导致「重新合并」永远不返回、界面一直转圈（实测 >10 分钟）。
    修法是把进程池换成串行替身，之后 6 秒左右完成。
    所以这里**必须带超时**：只要又卡住就判失败。
    """

    def test_remerge_completes(self):
        t0 = time.time()
        r = self.sidecar.call("api.remerge", {"name": "all"}, timeout=300)
        elapsed = time.time() - t0
        self.assertIn("result", r,
                      f"remerge 没有正常返回（{elapsed:.1f}s）：{r.get('error')}")
        self.assertLess(elapsed, 300,
                        "remerge 用时过长，多半又卡在进程池上了")

    def test_worker_alive_after_remerge(self):
        r = self.sidecar.call("sys.ping")
        self.assertTrue(r["result"]["ok"], "remerge 之后后端必须还能响应")


class ModOptionsTests(SidecarTestCase):
    """Mod 选项重选：读得到、改得动、不残留、认非法输入。"""

    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        mods = cls.sidecar.call("api.get_mods", {"disabled": True})["result"]["data"]
        cls.with_options = []
        cls._original = {}
        for m in mods:
            d = cls.sidecar.call("sys.modOptions", {"mod": m["path"]})
            info = d.get("result") or {}
            if info.get("hasOptions"):
                cls.with_options.append(m)
                cls._original[m["path"]] = list(info.get("selected") or [])
        if not cls.with_options:
            raise unittest.SkipTest("本机没有带可选组件的模组")

    @classmethod
    def tearDownClass(cls):
        # 这些用例会真的改动 mod 的选项目录 —— 跑完必须还原，别把用户的模组改坏
        for path, original in getattr(cls, "_original", {}).items():
            if not original:
                continue
            try:
                cls.sidecar.call("sys.applyModOptions", {"mod": path, "selects": original})
            except Exception:
                pass
        super().tearDownClass()

    def test_reports_definition(self):
        mod = self.with_options[0]
        d = self.sidecar.call("sys.modOptions", {"mod": mod["path"]})["result"]
        self.assertTrue(d["hasOptions"])
        self.assertTrue(d["available"], "至少要列出可选的变体目录")
        # info.json 里声明的**每一个** folder 都必须出现在返回的清单里 ——
        # 这是这次修复的核心：BCML 安装时会删掉没选中的变体，
        # 若按磁盘枚举，用户就只看得到自己已经选过的那几个，根本换不了。
        declared = {i["folder"] for i in d["multi"]}
        for grp in d["single"]:
            declared |= {o["folder"] for o in grp["options"]}
        reported = {i["folder"] for i in d["multi"]}
        for grp in d["single"]:
            reported |= {o["folder"] for o in grp["options"]}
        self.assertEqual(declared, reported, "返回的选项清单必须与 info.json 的定义完全一致")

    def test_lists_pruned_variants_as_restorable(self):
        """被 BCML 删掉的变体，只要原始 bnp 还在，就应当标记成可选（exists=True）。"""
        mod = self.with_options[0]
        d = self.sidecar.call("sys.modOptions",
                              {"mod": mod["path"], "snapshot": True})["result"]
        on_disk = {p.name for p in (Path(mod["path"]) / "options").iterdir() if p.is_dir()}
        reportable = {i["folder"] for i in d["multi"]}
        for grp in d["single"]:
            reportable |= {o["folder"] for o in grp["options"]}
        pruned = reportable - on_disk
        if not pruned:
            self.skipTest("该模组的变体全都还在磁盘上，无从验证恢复能力")
        flags = {i["folder"]: i["exists"] for i in d["multi"]}
        for grp in d["single"]:
            for o in grp["options"]:
                flags[o["folder"]] = o["exists"]
        restore_ok = [f for f in pruned if flags.get(f)]
        self.assertTrue(
            restore_ok,
            f"磁盘上少了 {len(pruned)} 个变体，却一个都标不了可恢复 —— "
            f"说明快照没有从原始 bnp 拿到全量（missing={sorted(pruned)[:5]}）")

    def test_apply_subset_leaves_no_stale(self):
        """选一个子集之后，mod/options 下必须只剩选中的那些 —— 这是"不残留旧配置"的判据。"""
        mod = self.with_options[0]
        before = self.sidecar.call("sys.modOptions", {"mod": mod["path"], "snapshot": True})["result"]
        available = before["available"]
        if len(available) < 2:
            self.skipTest("该模组只有一个变体，无法验证子集")

        keep = [available[0]]
        applied = self.sidecar.call(
            "sys.applyModOptions", {"mod": mod["path"], "selects": keep})["result"]
        self.assertEqual(sorted(applied["applied"]), sorted(keep))

        on_disk = sorted(p.name for p in (Path(mod["path"]) / "options").iterdir() if p.is_dir())
        self.assertEqual(on_disk, sorted(keep), "未选中的变体应当已经从 mod/options 里清掉")

    def test_reselect_restores_from_snapshot(self):
        """再选回来时必须能恢复 —— 证明快照生效、且取消不等于永久删除。"""
        mod = self.with_options[0]
        info = self.sidecar.call("sys.modOptions", {"mod": mod["path"], "snapshot": True})["result"]
        available = info["available"]
        if len(available) < 2:
            self.skipTest("该模组只有一个变体")

        want = available[:2]
        self.sidecar.call("sys.applyModOptions", {"mod": mod["path"], "selects": want})
        on_disk = sorted(p.name for p in (Path(mod["path"]) / "options").iterdir() if p.is_dir())
        self.assertEqual(on_disk, sorted(want), "重选后应当包含恢复回来的变体")

    def test_rejects_unknown_option(self):
        mod = self.with_options[0]
        r = self.sidecar.call("sys.applyModOptions",
                              {"mod": mod["path"], "selects": ["__not_a_real_option__"]})
        self.assertIn("error", r, "不属于该模组的选项名必须被拒绝")


class ReorderGuardTests(SidecarTestCase):
    """排序重命名必须**要么全做、要么不做**。

    这组测试来自一次真实的数据损坏：早期版本只校验「完整目录名是否重复」，
    没校验 `NNNN_` 前缀这个真正的优先级数字。于是一次只覆盖部分模组的排序，
    让两个目录都变成了 0100_、同时 0108_ 消失 —— 之后界面拿着的路径失效，
    「禁用模组」直接报 FileNotFoundError。
    """

    @staticmethod
    def _suffix(dirname: str) -> str:
        """去掉 `NNNN_` 前缀 —— 改名不变的那部分，用来跨改名认人。"""
        head, sep, rest = dirname.partition("_")
        return rest if (sep and head.isdigit()) else dirname

    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        cls.mods_dir = Path(os.path.expandvars(r"%LOCALAPPDATA%\bcml\mods_nx"))
        cls.original = cls.current()

    @classmethod
    def current(cls) -> list:
        """**当前**磁盘上的模组目录名（按编号排序）。"""
        return sorted(d.name for d in cls.mods_dir.iterdir()
                      if d.is_dir() and not d.name.startswith("9999"))

    @classmethod
    def tearDownClass(cls):
        # 排序会改目录名，跑完必须还原。
        #
        # 注意**不能**直接拿 setUpClass 时记下的名字去重排：那些名字此刻已经不存在了
        # （0100_xxx 早变成 0116_xxx），后端会判定"请求没覆盖全部模组"而拒绝执行，
        # 结果就是每跑一次测试就把用户的模组顺序搅乱一次。
        # 这里按「去掉编号前缀后的名字」把当前目录名映射回原始顺序再送过去。
        try:
            now = {cls._suffix(d.name): d.name for d in cls.mods_dir.iterdir()
                   if d.is_dir() and not d.name.startswith("9999")}
            order = [now[cls._suffix(o)] for o in cls.original if cls._suffix(o) in now]
            if len(order) == len(now):
                cls.sidecar.call("sys.reorderMods", {"order": order}, timeout=180)
            else:
                print(f"  !! 还原被跳过：映射到 {len(order)} 个 / 实际 {len(now)} 个")
        except Exception as exc:                       # noqa: BLE001
            print(f"  !! 还原顺序失败：{exc}")
        super().tearDownClass()

    def prefixes(self):
        return sorted(d.name.split("_")[0] for d in self.mods_dir.iterdir()
                      if d.is_dir() and not d.name.startswith("9999"))

    def test_partial_order_is_rejected_and_disk_untouched(self):
        before = self.prefixes()
        r = self.sidecar.call("sys.reorderMods", {"order": self.current()[:2]})
        self.assertIn("error", r, "只覆盖部分模组的排序请求必须被拒绝")
        self.assertIn("没有覆盖全部模组", r["error"]["message"])
        self.assertEqual(self.prefixes(), before, "被拒绝的请求不能改动磁盘")

    def test_reorder_keeps_prefixes_unique_and_contiguous(self):
        """整表倒序排一次，编号仍必须唯一且连续 —— 这正是损坏时被破坏的不变量。

        用**当前**目录名（而不是 setUpClass 记下的），否则前面的用例改过名之后
        这里送的就是一批不存在的名字。
        """
        now = self.current()
        n = len(now)
        if n < 3:
            self.skipTest("模组太少，测不出编号冲突")
        self.sidecar.call("sys.reorderMods", {"order": list(reversed(now))}, timeout=180)
        pre = self.prefixes()
        self.assertEqual(len(pre), len(set(pre)), f"出现重复编号：{pre}")
        self.assertEqual(pre, [f"{100 + i:04d}" for i in range(n)], f"编号不连续：{pre}")

    def test_repair_priorities_is_idempotent(self):
        r = self.sidecar.call("sys.repairPriorities", timeout=180)
        self.assertIn("result", r, r.get("error"))
        pre = self.prefixes()
        self.assertEqual(len(pre), len(set(pre)), f"修复后仍有重复编号：{pre}")


class StalePathTests(SidecarTestCase):
    """界面拿着**过期路径**发动作时，后端要按名字把它救回来。

    真实场景：排序把 0100_xxx 改名成 0116_xxx，界面还没刷新就点了「禁用」，
    原来会直接 `FileNotFoundError: ...\\0100_xxx\\info.json`。
    """

    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        cls.mods_dir = Path(os.path.expandvars(r"%LOCALAPPDATA%\bcml\mods_nx"))
        cls.mods = cls.sidecar.call("api.get_mods", {"disabled": True})["result"]["data"]
        if not cls.mods:
            raise unittest.SkipTest("本机没有已安装的模组")

    @staticmethod
    def _strip_prefix(dirname: str) -> str:
        head, sep, rest = dirname.partition("_")
        return rest if (sep and head.isdigit()) else dirname

    def test_stale_path_is_resolved_by_name(self):
        """把真实路径的编号前缀改掉（模拟改名后的过期路径），动作仍应成功。

        用未知 action 当探针：BCML 的 mod_action 对不认识的 action 什么都不做，
        但**在此之前**会 `BcmlMod.from_json(params["mod"])` —— 正是原来报错的那一步。
        这样既验证了解析，又不会真的改模组状态。
        """
        real = self.mods[0]
        real_path = Path(real["path"])
        stale_name = "0000_" + self._strip_prefix(real_path.name)
        stale = str(real_path.parent / stale_name)
        self.assertFalse(Path(stale).exists(), "前提：这个过期路径本来就不该存在")

        r = self.sidecar.call("sys.modAction",
                              {"mod": {"path": stale, "name": real.get("name", "")},
                               "action": "__probe__"})
        self.assertNotIn("error", r, f"过期路径应当被解析回来，却报错了：{r.get('error')}")

    def test_unknown_mod_gives_readable_error(self):
        r = self.sidecar.call("sys.modAction",
                              {"mod": {"path": str(self.mods_dir / "0100_根本没这个模组")},
                               "action": "__probe__"})
        self.assertIn("error", r, "不存在的模组必须报错")
        msg = r["error"]["message"]
        self.assertIn("找不到模组目录", msg)
        self.assertIn("刷新", msg, "错误信息要告诉用户下一步怎么做")


class ProbePythonTests(SidecarTestCase):
    """probePython 的三道闸。"""

    def test_rejects_empty(self):
        r = self.sidecar.call("sys.probePython", {"path": ""})
        self.assertFalse(r["result"]["ok"])

    def test_rejects_missing_file(self):
        r = self.sidecar.call("sys.probePython", {"path": r"C:\definitely\nope.exe"})
        self.assertFalse(r["result"]["ok"])

    def test_rejects_non_python_name(self):
        """随便指个系统程序不该被执行。"""
        exe = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "notepad.exe"
        if not exe.exists():
            self.skipTest("本机没有 notepad.exe")
        r = self.sidecar.call("sys.probePython", {"path": str(exe)})
        self.assertFalse(r["result"]["ok"])
        self.assertIn("Python", r["result"]["reason"] + "Python")

    def test_rejects_non_pe(self):
        """一个 .py 文件即使名字像 python 也不能被当成解释器执行。"""
        tmp = Path(os.environ.get("TEMP", ".")) / "python_fake_probe.exe"
        tmp.write_text("not an executable", encoding="utf-8")
        try:
            r = self.sidecar.call("sys.probePython", {"path": str(tmp)})
            self.assertFalse(r["result"]["ok"])
            self.assertIn("MZ", r["result"]["reason"])
        finally:
            try:
                tmp.unlink()
            except OSError:
                pass

    def test_accepts_real_interpreter(self):
        """这条同时守住一个曾经长期存在的 bug：
        子进程如果继承了 sidecar 的 RPC stdin（fd 0），`python -c <code>` 会永久挂起，
        于是「Python 路径校验」永远报「45 秒内没有返回」。必须确保 probePython 真的能通。"""
        r = self.sidecar.call("sys.probePython", {"path": find_python()}, timeout=180)
        self.assertTrue(r["result"]["ok"],
                        f"真实解释器应当通过：{r['result']}")

    def test_second_probe_also_works(self):
        """连做两次 —— 确认第一次没有把 worker 卡死、后端起不来。"""
        r1 = self.sidecar.call("sys.probePython", {"path": find_python()}, timeout=180)
        self.assertTrue(r1["result"]["ok"])
        r2 = self.sidecar.call("sys.ping")
        self.assertTrue(r2["result"]["ok"], "probe 之后 worker 必须还能干活")


class AtomicWriteTests(unittest.TestCase):
    """_write_json_atomic / _pack_dir_to_archive 的行为（源码级提取，不起进程）。"""

    @classmethod
    def setUpClass(cls) -> None:
        src = RPC_SERVER.read_text(encoding="utf-8")
        start = src.index("def _write_json_atomic")
        end = src.index("def _pack_bnp")
        ns = {"json": json, "os": os, "Path": Path}
        exec(src[start:end], ns)
        # staticmethod 包装：直接挂到类上会变成绑定方法，self 会被当成第一个位置参数传进去
        cls.write_json_atomic = staticmethod(ns["_write_json_atomic"])

    def test_write_is_atomic_and_leaves_no_tmp(self):
        tmpdir = Path(os.environ.get("TEMP", ".")) / "bcml_atomic_test"
        tmpdir.mkdir(exist_ok=True)
        target = tmpdir / "options.json"
        try:
            self.write_json_atomic(target, {"disable": [], "options": {"a": 1}})
            self.assertTrue(target.exists())
            self.assertEqual(json.loads(target.read_text("utf-8"))["options"], {"a": 1})
            self.assertFalse((tmpdir / "options.json.tmp").exists(),
                             "临时文件应当已被替换掉，不能留在磁盘上")

            # 覆盖已有文件也要能成功（os.replace 语义）
            self.write_json_atomic(target, {"disable": ["x"], "options": {}})
            self.assertEqual(json.loads(target.read_text("utf-8"))["disable"], ["x"])
        finally:
            for p in tmpdir.glob("*"):
                try:
                    p.unlink()
                except OSError:
                    pass
            try:
                tmpdir.rmdir()
            except OSError:
                pass


if __name__ == "__main__":
    unittest.main(verbosity=2)
