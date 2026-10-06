#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BCML sidecar —— 在 stdin/stdout 上提供 JSON-RPC 2.0 服务，把请求转发给 bcml._api.Api。

为什么要做 stdout 重定向：
    BCML 内部大量使用 print()/util.vprint() 输出进度。如果这些内容混进 stdout，
    就会冲垮 RPC 帧流。所以在启动时：
      1) os.dup(1) 保存一份原始 stdout 句柄专供 RPC 写帧；
      2) sys.stdout 换成转发器（写到 stderr，并顺带发出 notify.log 通知）；
      3) os.dup2(2, 1) 把「原生 fd 1」也指向 stderr ——
         这样 multiprocessing 子进程（spawn）里任何 print 也不会污染 RPC 通道。

帧格式：4 字节小端 uint32 长度 + UTF-8 JSON 正文（与 LSP 一致）。

请求:  {"jsonrpc":"2.0","id":1,"method":"api.get_mods","params":{"disabled":false}}
响应:  {"jsonrpc":"2.0","id":1,"result":{...}}
        {"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"...","data":"traceback"}}
通知:  {"jsonrpc":"2.0","method":"notify.log","params":{"text":"正在扫描..."}}

错误码约定：
    -32700 解析错误   -32601 方法不存在   -32602 参数错误
    -32000 后端异常   -32010 该操作需要界面（由 C# 侧用原生对话框实现）
"""
from __future__ import annotations

import base64
import datetime
import io
import json
import os
import queue
import shutil
import struct
import sys
import threading
import traceback
from collections import deque
from pathlib import Path

# --------------------------------------------------------------------------
# 0) 抢占 RPC 通道（必须在 import bcml 之前做完，避免任何东西先写 stdout）
# --------------------------------------------------------------------------
_RPC_OUT = os.fdopen(os.dup(1), "wb", 0)          # 独占的原始 stdout
_RPC_IN = os.fdopen(os.dup(0), "rb", 0)           # 独占的原始 stdin
_RPC_LOCK = threading.Lock()

# 日志环形缓冲用 deque(maxlen=...)：
#   · 满了自动丢最旧的，不用像 list 那样「append 完再手工 del 头部」（原来那段
#     头部裁剪在多线程下是要出问题的）；
#   · deque 的 append 是原子操作，多个生产者线程同时写不会破坏结构。
# 注意：deque **不支持切片**，所以取尾部要显式 list() 转一下。
_LOG_RING: deque[str] = deque(maxlen=2000)

# 取快照时仍然要加锁：BCML 的工作线程在往里 append，
# 而 `list(deque)` 是迭代，边迭代边改会抛 RuntimeError("deque mutated during
# iteration")。锁只在 append 和取快照这两处，竞争极低。
_LOG_LOCK = threading.Lock()


def _log_append(line: str) -> None:
    with _LOG_LOCK:
        _LOG_RING.append(line)


def _log_snapshot(tail: int) -> list:
    with _LOG_LOCK:
        return list(_LOG_RING)[-tail:]


def _jsonable(obj):
    """把 Path / set / bytes / oead 对象等转成可 JSON 化的值。"""
    if isinstance(obj, (str, int, float, bool)) or obj is None:
        return obj
    if isinstance(obj, Path):
        return str(obj)
    if isinstance(obj, bytes):
        return base64.b64encode(obj).decode("ascii")
    if isinstance(obj, (set, frozenset, tuple)):
        return [_jsonable(x) for x in obj]
    if isinstance(obj, dict):
        return {str(k): _jsonable(v) for k, v in obj.items()}
    if isinstance(obj, (list,)):
        return [_jsonable(x) for x in obj]
    for attr in ("to_json", "to_dict"):
        f = getattr(obj, attr, None)
        if callable(f):
            try:
                return _jsonable(f())
            except Exception:
                pass
    return str(obj)


def _write_frame(obj) -> None:
    body = json.dumps(obj, ensure_ascii=False, default=_jsonable).encode("utf-8")
    with _RPC_LOCK:
        try:
            _RPC_OUT.write(struct.pack("<I", len(body)))
            _RPC_OUT.write(body)
            _RPC_OUT.flush()
        except (BrokenPipeError, ValueError, OSError):
            # 前端已退出
            os._exit(0)


def notify(method: str, params) -> None:
    _write_frame({"jsonrpc": "2.0", "method": method, "params": params})


def reply(req_id, result) -> None:
    _write_frame({"jsonrpc": "2.0", "id": req_id, "result": result})


def fail(req_id, code: int, message: str, data: str | None = None) -> None:
    err = {"code": code, "message": message}
    if data:
        err["data"] = data
    _write_frame({"jsonrpc": "2.0", "id": req_id, "error": err})


class _StdoutForwarder(io.TextIOBase):
    """接住 BCML 的 print 输出：写 stderr + 发 notify.log 通知 + 进环形缓冲。"""

    def __init__(self):
        super().__init__()
        self._buf = ""
        self._err = os.fdopen(os.dup(2), "w", encoding="utf-8", errors="replace", buffering=1)

    def writable(self):
        return True

    def write(self, s: str) -> int:
        try:
            self._err.write(s)
        except Exception:
            pass
        self._buf += s
        while "\n" in self._buf:
            line, self._buf = self._buf.split("\n", 1)
            self._line(line)
        return len(s)

    def _line(self, line: str) -> None:
        line = line.replace("VERBOSE", "").rstrip()
        if not line:
            return
        _log_append(line)
        notify("notify.log", {"text": line})

    def flush(self):
        try:
            self._err.flush()
        except Exception:
            pass


# 先把 python 层的 stdout 换掉，再把原生 fd 1 指向 stderr
sys.stdout = _StdoutForwarder()
sys.stderr = sys.stderr  # 保持
os.dup2(2, 1)
# 保险：有些库会缓存 sys.__stdout__
sys.__stdout__ = sys.stdout


# --------------------------------------------------------------------------
# 1) 导入 BCML
# --------------------------------------------------------------------------
_STARTUP_ERROR = None
try:
    from bcml import util as bcml_util
    from bcml._api import Api
    from bcml.__version__ import VERSION as BCML_VERSION
    BCML_OK = True
except Exception as exc:                                # pragma: no cover
    BCML_OK = False
    BCML_VERSION = None
    _STARTUP_ERROR = "".join(traceback.format_exception_only(type(exc), exc)).strip()
    Api = None                                          # type: ignore
    bcml_util = None                                    # type: ignore


class NeedsUiError(RuntimeError):
    """后端想调 webview 窗口能力 —— 由 C# 侧用原生控件实现。"""


class NoSuchMethodError(AttributeError):
    """请求的方法在 Api 上不存在。

    单独一个类型是为了跟「方法存在、但实现内部抛了 AttributeError」区分开：
    以前两者都会被映射成 -32601「方法不存在」，结果后端自己的 bug 会被报成
    「你的调用写错了」，排查方向一开始就是歪的。
    """


class _SerialPool:
    """BCML 进程池的**串行**替身。

    为什么要有这个东西：

        BCML 的 `util.start_pool()` 是 `multiprocessing.Pool(processes=min(63, cpu_count))`。
        在 sidecar 里它会 spawn 几十个 worker，而每个 worker 都会以 **rpc_server.py 作为
        `__mp_main__`** 重新导入一遍本模块 —— 于是顶层那句 `from bcml._api import Api`
        会在几十个进程里并发执行，各自去加载 20MB 的 `oead.pyd`。

        实测现象：spawn 出来的 worker 全部停在 ~9MB（还没把 oead 载入），主进程 CPU
        只有几个百分点，而「重新合并」十几分钟不返回 —— 界面就一直转圈。
        把池调小到 2/4 也一样卡，所以不是规模问题，是这个 spawn 模式在本进程里就跑不通。

        串行替身把 BCML 用到的那几个方法（`map` / `starmap` / `imap_unordered` /
        `close` / `join` / `terminate`）都在**本进程**里顺序执行。慢一点，但一定能跑完，
        而且异常会直接冒出来，不会再变成"永远转圈"。

    想恢复并行可以用环境变量 `BCML_SIDECAR_PARALLEL=1`（仅在你确认本机没问题时使用）。
    """

    def map(self, func, iterable, chunksize=None):
        return [func(x) for x in iterable]

    def starmap(self, func, iterable, chunksize=None):
        return [func(*x) for x in iterable]

    def imap(self, func, iterable, chunksize=None):
        return iter([func(x) for x in iterable])

    def imap_unordered(self, func, iterable, chunksize=None):
        return iter([func(x) for x in iterable])

    def apply(self, func, args=(), kwds=None):
        return func(*(args or ()), **(kwds or {}))

    def apply_async(self, func, args=(), kwds=None, callback=None, error_callback=None):
        """同步执行，但返回一个带 get()/ready()/successful() 的句柄 —— 保持接口兼容。"""

        class _Result:
            def __init__(self, value=None, error=None):
                self._v, self._e = value, error

            def get(self, timeout=None):
                if self._e is not None:
                    raise self._e
                return self._v

            def ready(self):
                return True

            def successful(self):
                return self._e is None

            def wait(self, timeout=None):
                return True

        try:
            value = func(*(args or ()), **(kwds or {}))
        except BaseException as exc:                 # noqa: BLE001
            res = _Result(error=exc)
            if error_callback is not None:
                error_callback(exc)
            return res
        res = _Result(value=value)
        if callback is not None:
            callback(value)
        return res

    def close(self):
        pass

    def join(self):
        pass

    def terminate(self):
        pass

    def __enter__(self):
        return self

    def __exit__(self, *_exc):
        return False


def _install_serial_pool() -> None:
    """把 BCML 的 start_pool 换成串行替身（除非显式要求并行）。"""
    if os.environ.get("BCML_SIDECAR_PARALLEL") == "1":
        return
    if not BCML_OK or bcml_util is None:
        return
    try:
        bcml_util.start_pool = lambda: _SerialPool()
    except Exception:                                # noqa: BLE001
        pass


class _NoWindow:
    def __getattr__(self, name):
        raise NeedsUiError(f"该操作需要界面交互（{name}），已交由 WinUI 侧处理")

    def __bool__(self):
        return False


# 这些方法在 Windows 上直接调 tkinter 弹原生对话框（不走 self.window），
# 在无界面进程里会永久阻塞，必须显式拦掉，交给 C# 侧实现。
_NEEDS_UI_ALWAYS = {"get_folder"}

_install_serial_pool()


_API = None
_API_LOCK = threading.Lock()


def get_api():
    """取 Api 单例。

    加锁是防御性的：目前请求是单线程串行跑的，不会真的并发进来；
    但一旦将来把长任务挪到 worker 线程，这个 check-then-act 就是竞态 ——
    `Api(host="")` 会重复构造、`window` 会被覆盖。成本极低，先锁上。
    """
    global _API
    if _API is not None:
        return _API
    if not BCML_OK:
        raise RuntimeError(f"BCML 导入失败：{_STARTUP_ERROR}")
    with _API_LOCK:
        if _API is None:
            api = Api(host="")
            api.window = _NoWindow()      # type: ignore[assignment]
            _API = api
    return _API


# --------------------------------------------------------------------------
# 2) 方法分发
# --------------------------------------------------------------------------
def _describe() -> dict:
    cfg = {}
    if BCML_OK:
        try:
            cfg = {
                "store_dir": str(bcml_util.get_storage_dir()),
                "mods_dir": str(bcml_util.get_modpack_dir()),
                "merged_dir": str(bcml_util.get_master_modpack_dir()),
                "profiles_dir": str(bcml_util.get_profiles_dir()),
                "settings": _jsonable(bcml_util.get_settings()),
            }
        except Exception as exc:
            cfg = {"error": str(exc)}
    return {
        "bcmlVersion": BCML_VERSION,
        "bcmlOk": BCML_OK,
        "bcmlError": _STARTUP_ERROR,
        "python": sys.version.split()[0],
        "executable": sys.executable,
        "pid": os.getpid(),
        **cfg,
    }


def _drill(fn):
    """BCML 的装饰器（win_or_lose / install.refresher）都用 (*args, **kwargs) 包裹，
    且没有 functools.wraps，所以 inspect.signature 只能看到 *args。
    这里沿着「闭包里第一个 callable」一路下钻，直到拿到有具名位置参数的真正实现。

    返回 (实现函数, 是否需要去掉开头的 self)。
    """
    import inspect

    cur = fn
    drilled = False
    seen: set[int] = set()
    for _ in range(8):
        if id(cur) in seen:
            break
        seen.add(id(cur))

        wrapped = getattr(cur, "__wrapped__", None)
        if wrapped is not None:
            cur, drilled = wrapped, True
            continue

        named = [
            p for p in inspect.signature(cur).parameters.values()
            if p.kind in (p.POSITIONAL_ONLY, p.POSITIONAL_OR_KEYWORD)
        ]
        if named:
            break  # 已经是真实实现（或已绑定 self）

        nxt = None
        for cell in (getattr(cur, "__closure__", None) or ()):
            try:
                v = cell.cell_contents
            except ValueError:
                continue
            if callable(v):
                nxt = v
                break
        if nxt is None:
            break
        cur, drilled = nxt, True
    return cur, drilled


def _needs_params(fn) -> bool:
    import inspect

    real, drilled = _drill(fn)
    params = [
        p for p in inspect.signature(real).parameters.values()
        if p.kind in (p.POSITIONAL_ONLY, p.POSITIONAL_OR_KEYWORD)
    ]
    if drilled and params and params[0].name == "self":
        params = params[1:]
    return any(p.default is inspect.Parameter.empty for p in params)


def _call_api(method: str, params):
    if method in _NEEDS_UI_ALWAYS:
        raise NeedsUiError(f"「{method}」需要界面交互，已交由 WinUI 侧处理")

    api = get_api()
    fn = getattr(api, method, None)
    if fn is None or not callable(fn):
        raise NoSuchMethodError(method)

    result = fn(params or {}) if _needs_params(fn) else fn()

    # @win_or_lose 包过的函数会返回 {success,data} / {error}，直接透传；
    # 其它函数手工套一层，让 C# 侧只需要处理一种形状。
    # 判定依据是装饰器包的 wrapper 名（bcml 里那个装饰器叫 status_run）。
    enveloped = getattr(fn, "__name__", "") == "status_run"
    return result if enveloped else {"success": True, "data": result}


def _safe_child_dir(root: Path, name: str) -> Path:
    """把用户传来的目录名解析成 root 下的直接子目录，并挡住路径穿越。

    `root / "..\\..\\Windows"` 这种写法在 Windows 上会被 Path 当成合法路径，
    直接拿去做 rename / rmtree 是危险的。这里只接受「不含分隔符、不是 . 或 ..」
    的纯目录名，并且再解析一次确认父目录就是 root。
    """
    raw = str(name or "")
    if not raw or raw in (".", ".."):
        raise ValueError(f"非法的目录名：{raw!r}")
    if any(ch in raw for ch in ("/", "\\", ":", "\0")) or Path(raw).name != raw:
        raise ValueError(f"目录名不能包含路径分隔符：{raw!r}")
    root_r = root.resolve()
    full = (root_r / raw)
    if full.parent != root_r:
        raise ValueError(f"目录不在模组根目录下：{raw!r}")
    return full


def _resequence(mdir, order, start=100):
    """按 order 给 mod 目录重新编号（两阶段改名，避免优先级互换时目录名撞车）。

    只动 `info.json` 的 priority/id 与目录名，**不碰 mod 内容**。返回新目录名列表。

    分三步，先把所有可能失败的事情做完，再动文件系统：

      1. 规划：校验目录名合法且确实存在，读掉所有 info.json，算出目标名；
         目标名重复、或目标名被本批次之外的目录占着 —— 直接在这一步报错退出，
         此时**磁盘上还没有任何改动**。
      2. 改名到临时名：全部先变成 `__tmp_XXXX`，这样优先级互换也不会互相覆盖。
      3. 写回 info.json 并落到最终名。

    第 2/3 步万一中途失败（磁盘满、文件被占用、权限不足），会把已经动过的目录
    **整体回滚**回原来的名字，并把 info.json 恢复成原文 —— 不会留下半截状态。
    """
    mdir = Path(mdir).resolve()

    # ---- 阶段 1：规划（此阶段不碰磁盘）----
    plan = []
    for dirname in order:
        src = _safe_child_dir(mdir, dirname)
        if not src.is_dir():
            continue
        info_path = src / "info.json"
        if not info_path.exists():
            raise ValueError(f"{src.name} 里没有 info.json，无法重排")
        try:
            info = json.loads(info_path.read_text("utf-8"))
        except Exception as exc:                       # noqa: BLE001
            raise ValueError(f"{src.name} 的 info.json 无法解析：{exc}") from exc
        mod_name = info.get("name")
        if not mod_name:
            raise ValueError(f"{src.name} 的 info.json 缺少 name 字段")
        plan.append({
            "src": src,
            "orig": src.name,
            "info": info,
            "raw_info": info_path.read_text("utf-8"),
            "priority": start + len(plan),
            "final": f"{start + len(plan):04d}_{bcml_util.get_safe_pathname(mod_name)}",
        })

    if not plan:
        return []

    # ---- 校验 1：请求必须覆盖**全部**模组目录 ----
    # 只排一部分是危险的：没被列到的目录会原地不动，而列表里的会被重新编号，
    # 两者可能撞到同一个 NNNN_ 前缀（真实踩过：两个目录都变成 0100_，0108_ 消失）。
    def _prefix_of(dirname: str) -> str:
        head, sep, _ = dirname.partition("_")
        return head if sep and head.isdigit() else ""

    existing = {d.name for d in mdir.iterdir() if d.is_dir() and not d.name.startswith("9999")}
    missing = sorted(existing - {p["orig"] for p in plan})
    if missing:
        raise ValueError(
            "排序请求没有覆盖全部模组，已拒绝执行（否则没被列到的模组会和别人撞号）。"
            "缺：" + "、".join(missing) + "。请刷新列表后重试。")

    finals = [p["final"] for p in plan]
    dupes = sorted({f for f in finals if finals.count(f) > 1})
    if dupes:
        raise ValueError("重排后有目录会同名，请先修改这些模组的名称：" + "、".join(dupes))

    # ---- 校验 2：优先级编号（NNNN_ 前缀）必须全局唯一 ----
    # 只比完整目录名是不够的：BCML 真正读的是前缀这个数字。
    # 两个目录叫 0100_A / 0100_B 时全名不同、却都是优先级 100。
    by_prefix: dict = {}
    for p in plan:
        pre = _prefix_of(p["final"])
        if pre in by_prefix:
            raise ValueError(
                f"重排后会有两个模组共用优先级编号 {pre}："
                f"{by_prefix[pre]} 与 {p['final']}")
        by_prefix[pre] = p["final"]

    moving = {p["orig"] for p in plan}
    for d in mdir.iterdir():
        if not d.is_dir() or d.name in moving:
            continue
        pre = _prefix_of(d.name)
        if pre and pre in by_prefix:
            raise ValueError(
                f"优先级编号 {pre} 已被 {d.name} 占用，"
                f"{by_prefix[pre]} 排不进去。请刷新列表后重试。")

    occupied = sorted(f for f in finals if f not in moving and (mdir / f).exists())
    if occupied:
        raise ValueError("目标目录名已被其它目录占用，请先处理：" + "、".join(occupied))

    # ---- 阶段 2：全部先改成临时名 ----
    for i, p in enumerate(plan):
        tmp = mdir / f"__tmp_{i:04d}"
        if tmp.exists():
            raise RuntimeError(f"临时目录已存在，可能有另一次排序未完成：{tmp.name}")
        p["tmp"] = tmp

    staged = []
    try:
        for p in plan:
            p["src"].rename(p["tmp"])
            staged.append(p)
    except BaseException:
        _rollback_resequence(mdir, plan)
        raise

    # ---- 阶段 3：写 info.json + 落最终名 ----
    applied = []
    try:
        for p in plan:
            p["info"]["priority"] = p["priority"]
            p["info"]["id"] = p["final"]
            (p["tmp"] / "info.json").write_text(
                json.dumps(p["info"], ensure_ascii=False, indent=2), encoding="utf-8")
            p["tmp"].rename(mdir / p["final"])
            p["done"] = True
            applied.append(p["final"])

        # ---- 收尾自检：全目录扫一遍，编号必须唯一 ----
        # 前面的校验是"按计划"推出来的；这里是"按磁盘"实际验一遍。
        # 万一还是撞了，就整体回滚 —— 宁可恢复原状，也不能留下两个 0100_。
        seen: dict = {}
        for d in mdir.iterdir():
            if not d.is_dir():
                continue
            pre = _prefix_of(d.name)
            if not pre:
                continue
            if pre in seen:
                raise RuntimeError(
                    f"重排后出现重复的优先级编号 {pre}：{seen[pre]} 与 {d.name}，已回滚")
            seen[pre] = d.name
        return applied
    except BaseException:
        _rollback_resequence(mdir, plan)
        raise


def repair_priorities(params=None) -> dict:
    """把 mods_nx 下所有模组的优先级重新连续编号（0100 起），修掉撞号。

    什么情况下需要它：早期版本的排序只校验"完整目录名是否重复"，
    没校验 NNNN_ 前缀，于是可能出现两个 0100_、同时某个号段消失。
    编号重复后 BCML 自己解析优先级会出错，界面也会拿着过期路径做动作。

    顺序按「当前编号 → 目录名」稳定排序，所以对没坏的情况是幂等的。
    """
    if not BCML_OK:
        raise RuntimeError(f"BCML 导入失败：{_STARTUP_ERROR}")
    mdir = Path(bcml_util.get_modpack_dir()).resolve()

    def prefix_of(dirname: str) -> int:
        head, sep, _ = dirname.partition("_")
        return int(head) if (sep and head.isdigit()) else 10 ** 9

    mods = sorted(
        (d for d in mdir.iterdir() if d.is_dir() and not d.name.startswith("9999")),
        key=lambda d: (prefix_of(d.name), d.name),
    )
    if not mods:
        return {"renamed": 0, "order": []}

    dupes = {}
    for d in mods:
        dupes.setdefault(prefix_of(d.name), []).append(d.name)
    duplicated = {k: v for k, v in dupes.items() if len(v) > 1}

    order = [d.name for d in mods]
    moved = _resequence(mdir, order, 100)
    _log_append(f"[repairPriorities] 重新编号 {len(moved)} 个目录"
                f"（修复前重复编号：{duplicated or '无'}）")
    return {
        "renamed": len(moved),
        "duplicatedBefore": duplicated,
        "order": moved,
    }


def _strip_priority_prefix(dirname: str) -> str:
    """去掉 `NNNN_` 前缀，拿到稳定的模组标识（改名不动它）。"""
    head, sep, rest = dirname.partition("_")
    return rest if (sep and head.isdigit()) else dirname


def _resolve_mod_dict(mod: dict) -> dict:
    """把界面传来的 mod 字典里的 path 修正成磁盘上真实存在的目录。

    界面拿到的 path 会**过期**：排序/拖动会把目录改名（0100_xxx → 0116_xxx），
    如果界面还没来得及刷新，动作就会拿着旧路径去读 info.json，报
    `FileNotFoundError: ...\\0100_xxx\\info.json`。

    这里按「去掉编号前缀后的名字」重新解析一次。要求**唯一命中**：
    命中多个说明有歧义（比如两个同名模组），宁可报错也不能猜。
    """
    if not BCML_OK or not isinstance(mod, dict):
        return mod

    raw = str(mod.get("path") or "")
    if raw and Path(raw).is_dir():
        return mod          # 没过期，原样用

    mdir = Path(bcml_util.get_modpack_dir())
    want = Path(raw).name if raw else ""
    if not want:
        return mod

    target = _strip_priority_prefix(want)
    hits = [d for d in mdir.iterdir()
            if d.is_dir() and not d.name.startswith("9999")
            and _strip_priority_prefix(d.name) == target]

    if len(hits) == 1:
        _log_append(f"[modAction] 界面路径已过期，按名字重新解析：{want} → {hits[0].name}")
        fixed = dict(mod)
        fixed["path"] = str(hits[0])
        fixed["id"] = hits[0].name
        return fixed
    if not hits:
        raise FileNotFoundError(
            f"找不到模组目录（可能已被重命名或删除）：{want}。"
            "请点右上角「刷新」重新读取列表后重试。")
    raise RuntimeError(
        f"有 {len(hits)} 个目录都匹配 {target}，无法确定要对哪个操作。请刷新列表后重试。")


def mod_action_headless(params: dict) -> dict:
    """启用 / 禁用 / 卸载单个模组。

    没有直接用 BCML 的 `Api.mod_action`：它信任界面传来的 path，而 path 会过期
    （见 _resolve_mod_dict）。这里先把 path 校正，再交给原来的逻辑。
    """
    _require_bcml()
    params = dict(params or {})
    if isinstance(params.get("mod"), dict):
        params["mod"] = _resolve_mod_dict(params["mod"])
    return get_api().mod_action(params)


def _rollback_resequence(mdir: Path, plan: list) -> None:
    """把 `_resequence` 动过的目录还原回原来的名字与 info.json 内容。

    先统一挪到 `__rb_XXXX`，再挪回原名 —— 两步走可以避免「A 的原名此刻被 B 占着」
    这类还原过程中的撞车。
    """
    # 第一步：把每个条目从「当前位置」挪到一个中性的回滚名
    for i, p in enumerate(plan):
        cur = mdir / p["final"] if p.get("done") else p["tmp"]
        rb = mdir / f"__rb_{i:04d}"
        try:
            if cur.exists():
                if rb.exists():
                    shutil.rmtree(rb, ignore_errors=True)
                cur.rename(rb)
                p["rb"] = rb
        except Exception:
            pass

    # 第二步：挪回原名并恢复 info.json 原文
    for p in plan:
        rb = p.get("rb")
        if rb is None or not rb.exists():
            continue
        try:
            (rb / "info.json").write_text(p["raw_info"], encoding="utf-8")
        except Exception:
            pass
        try:
            target = mdir / p["orig"]
            if target.exists():
                shutil.rmtree(target, ignore_errors=True)
            rb.rename(target)
        except Exception:
            pass


def _scan_mods(mdir):
    """返回 {mod 名字: {"dir": 目录名, "priority": int, "path": Path}}（跳过 9999_BCML）。"""
    out = {}
    if not mdir.is_dir():
        return out
    for d in sorted(mdir.iterdir()):
        if not d.is_dir() or d.name == "9999_BCML":
            continue
        info_path = d / "info.json"
        if not info_path.exists():
            continue
        try:
            info = json.loads(info_path.read_text("utf-8"))
        except Exception:
            continue
        name = str(info.get("name", ""))
        if not name:
            continue
        out[name] = {"dir": d.name, "priority": int(info.get("priority", 0)), "path": d}
    return out


def _read_json(path, default):
    try:
        if path.exists():
            return json.loads(path.read_text("utf-8"))
    except Exception:
        pass
    return default


def _option_folders(path):
    od = path / "options"
    if not od.is_dir():
        return []
    try:
        return sorted(x.name for x in od.iterdir() if x.is_dir())
    except Exception:
        return []


def export_profile(params=None) -> dict:
    """把当前「已安装模组清单」导出成轻量 JSON。

    只记录清单层面的状态（排序 / 启用 / 合并器选项 / 已启用的选项目录），
    **不复制任何 mod 文件**，所以一份配置通常只有几 KB。
    """
    if not BCML_OK:
        raise RuntimeError(f"BCML 导入失败：{_STARTUP_ERROR}")
    from pathlib import Path as _P

    mdir = _P(bcml_util.get_modpack_dir())
    mods = []
    for name, meta in _scan_mods(mdir).items():
        p = meta["path"]
        mods.append({
            "name": name,
            "dir": meta["dir"],
            "priority": meta["priority"],
            "disabled": (p / ".disabled").exists(),
            "options": _read_json(p / "options.json", {}),
            "optionFolders": _option_folders(p),
        })
    mods.sort(key=lambda m: m["priority"])
    return {
        "format": "bcml-winui3-modlist",
        "version": 1,
        "exportedAt": datetime.datetime.now().isoformat(timespec="seconds"),
        "modCount": len(mods),
        "mods": mods,
    }


def apply_profile(params) -> dict:
    """按导入的清单恢复：排序 + 启用/禁用 + 合并器选项（options.json）。

    不安装/卸载任何 mod —— 清单里有、本机没有的会列在 `missing` 里，
    需要重装才能补齐。选项目录（options/<名>）也**不会**被创建或删除：
    未勾选的目录在安装时就已被 BCML 删除，只能重装恢复，所以差异会列在
    `optionFolderChanges` 里由界面提示。
    """
    if not BCML_OK:
        raise RuntimeError(f"BCML 导入失败：{_STARTUP_ERROR}")
    from pathlib import Path as _P

    raw = (params or {}).get("profileJson")
    if not raw:
        raise ValueError("没有提供清单内容")
    doc = json.loads(raw) if isinstance(raw, str) else raw
    wanted = list(doc.get("mods") or [])
    if not wanted:
        raise ValueError("清单里没有任何模组")

    mdir = _P(bcml_util.get_modpack_dir())
    current = _scan_mods(mdir)
    wanted_names = [str(w.get("name", "")) for w in wanted]

    missing = [n for n in wanted_names if n and n not in current]

    order = [current[n]["dir"] for n in wanted_names if n in current]
    rest = sorted(
        (meta["dir"] for name, meta in current.items() if name not in set(wanted_names)),
        key=lambda dn: next(m["priority"] for m in current.values() if m["dir"] == dn),
    )
    order += rest

    start = int(doc.get("start", 100) or 100)
    _resequence(mdir, order, start)

    # 改名之后重新扫描，拿到新的目录名
    after = _scan_mods(mdir)

    applied = 0
    opt_changes = []
    for w in wanted:
        name = str(w.get("name", ""))
        meta = after.get(name)
        if meta is None:
            continue
        p = meta["path"]
        applied += 1

        # 启用 / 禁用
        flag = p / ".disabled"
        if w.get("disabled"):
            if not flag.exists():
                flag.write_bytes(b"")
        elif flag.exists():
            flag.unlink()

        # 合并器选项
        options = w.get("options")
        if isinstance(options, dict):
            # 原子写：这里写坏一次，这个模组下次就加载不了
            _write_json_atomic(p / "options.json", options)

        # 选项目录差异（只报告，不改动）
        want_folders = sorted(w.get("optionFolders") or [])
        cur_folders = _option_folders(p)
        if want_folders and want_folders != cur_folders:
            opt_changes.append({
                "name": name,
                "want": want_folders,
                "current": cur_folders,
            })

    return {
        "applied": applied,
        "missing": missing,
        "optionFolderChanges": opt_changes,
    }


def reorder_mods(params: dict) -> dict:
    """只重写优先级与目录名，**不触发合并**。

    为什么不用 Api.apply_queue：它末尾会跑 install.refresh_merges()（几分钟）。
    拖动排序是高频轻操作，应当只改元数据，让用户自己决定何时重新合并。

    params: {"order": ["0100_xxx", "0101_yyy", ...], "start": 100}
    """
    if not BCML_OK:
        raise RuntimeError(f"BCML 导入失败：{_STARTUP_ERROR}")
    from pathlib import Path as _P

    mdir = _P(bcml_util.get_modpack_dir())
    order = [str(x) for x in (params or {}).get("order") or []]
    start = int((params or {}).get("start", 100) or 100)
    if not order:
        return {"moved": 0, "paths": []}
    moved = _resequence(mdir, order, start)
    return {"moved": len(moved), "paths": moved}


def convert_bnp_headless(params) -> dict:
    """无界面的 BNP 平台转换：解包 → dev.convert_mod → 重新打包成 .bnp。

    为什么不用 Api.convert_bnp：它内部用 self.window.create_file_dialog 选保存路径，
    在 sidecar（无界面进程）里会走到 -32010。这里改成由界面把 output 传进来。
    """
    if not BCML_OK:
        raise RuntimeError(f"BCML 导入失败：{_STARTUP_ERROR}")
    from pathlib import Path as _P

    from bcml import dev, install

    src = _P(params["mod"])
    if not src.exists():
        raise FileNotFoundError(f"找不到 BNP 文件：{src}")
    out = _P(params["output"])
    if not out.name.lower().endswith(".bnp"):
        out = out.with_suffix(".bnp")
    to_wiiu = bool(params.get("wiiu", False))
    warn_only = str(params.get("warn", "warn")) != "fail"

    mod_dir = install.open_mod(src)
    warnings = []
    try:
        warnings = list(dev.convert_mod(mod_dir, to_wiiu, warn_only) or [])
        _pack_dir_to_archive(mod_dir, out)
    finally:
        shutil.rmtree(mod_dir, ignore_errors=True)

    return {
        "output": str(out),
        "warnings": warnings,
        "size": out.stat().st_size if out.exists() else 0,
    }


def open_master_modpack(params=None) -> dict:
    """返回合并输出目录的路径（是否在资源管理器里打开由界面决定，后端不做这事）。"""
    if not BCML_OK:
        raise RuntimeError(f"BCML 导入失败：{_STARTUP_ERROR}")
    path = bcml_util.get_master_modpack_dir()
    return {"path": str(path), "exists": path.exists()}


def update_mod_headless(params) -> dict:
    """用一个新的 .bnp 替换已安装的同名模组，保留优先级 / 启用状态 / 合并器选项。

    原版 Api.update_mod 内部用 self.file_pick 选文件（无界面进程不可用），
    这里改成由界面把 bnp 路径传进来。与 update_mod 一致：**不自动合并**。

    旧目录不是直接删，而是先**改名留底**；新模组装成功后才删掉底稿。
    装到一半失败就把底稿改回来 —— 否则用户会同时失去旧版和新版。
    """
    if not BCML_OK:
        raise RuntimeError(f"BCML 导入失败：{_STARTUP_ERROR}")
    from pathlib import Path as _P

    from bcml import install

    bnp = _P(params["bnp"])
    mod_dir = _P(params["mod"])
    if not bnp.exists():
        raise FileNotFoundError(f"找不到 BNP 文件：{bnp}")
    if not mod_dir.is_dir():
        raise FileNotFoundError(f"找不到已安装的模组目录：{mod_dir}")

    # 只允许替换「模组根目录下的直接子目录」，并且必须真的像个已安装的模组。
    # 这个函数会 rmtree，路径传错一次就是不可逆的数据丢失。
    modpack_dir = _P(bcml_util.get_modpack_dir()).resolve()
    mod_dir = mod_dir.resolve()
    if mod_dir.parent != modpack_dir:
        raise ValueError(f"只能替换模组根目录下的模组：{mod_dir}")
    if not (mod_dir / "info.json").exists():
        raise ValueError(f"{mod_dir.name} 里没有 info.json，看起来不是已安装的模组")

    options = _read_json(mod_dir / "options.json", {"disable": [], "options": {}})
    priority = 0
    try:
        priority = int(json.loads((mod_dir / "info.json").read_text("utf-8")).get("priority", 0))
    except Exception:
        pass
    was_disabled = (mod_dir / ".disabled").exists()

    # 旧目录留底：改名而不是删除，装完再清
    keep = mod_dir.with_name(f"__old_{mod_dir.name}")
    if keep.exists():
        shutil.rmtree(keep, onerror=install.force_del)
    mod_dir.rename(keep)

    installed_dir = None
    try:
        install.install_mod(bnp, options=options, insert_priority=priority,
                            merge_now=False, updated=True)
        # install_mod 会按优先级重算目录名，回来后按前缀找它
        prefix = f"{priority:04d}_"
        for d in modpack_dir.iterdir():
            if d.is_dir() and d.name.startswith(prefix):
                installed_dir = d
                break
    except BaseException:
        # 装失败：把底稿改回来，并把可能已经落下的半个新目录清掉
        try:
            if installed_dir is not None and installed_dir.exists():
                shutil.rmtree(installed_dir, onerror=install.force_del)
        except Exception:
            pass
        try:
            if keep.exists() and not mod_dir.exists():
                keep.rename(mod_dir)
        except Exception:
            _log_append(f"[updateMod] 回滚失败，旧模组留在 {keep}")
        raise

    # 装成功，清掉底稿
    shutil.rmtree(keep, ignore_errors=True)

    if installed_dir is not None and was_disabled:
        (installed_dir / ".disabled").write_bytes(b"")
    return {"installed": str(installed_dir) if installed_dir else "", "disabled": was_disabled}


def probe_python(params) -> dict:
    """检查某个解释器能否 import bcml / oead —— 设置页里校验 Python 路径用。

    安全说明：这个函数会**真的把用户给的路径当程序执行**。设置页的用法是「用文件选择器
    挑一个 python.exe」，但 RPC 本身是暴露的，所以这里加了三道最便宜的闸：

      1. 文件名必须像 Python 解释器（python.exe / pythonw.exe / python3.9.exe …）；
      2. 必须是 Windows PE（`MZ` 头）——挡掉拿 .txt / .lnk 来碰运气的情况；
      3. 只做一次固定命令的调用，带超时，且不弹窗。

    这不是沙箱（真要执行还是能执行），目的是把「误选 / 手滑 / 被当成跳板」的常见情况挡在门外。
    """
    import subprocess
    from pathlib import Path as _P

    exe = str((params or {}).get("path") or "").strip()
    if not exe:
        return {"ok": False, "reason": "路径为空"}

    p = _P(exe)
    if not p.exists():
        return {"ok": False, "reason": "文件不存在"}
    if not p.is_file():
        return {"ok": False, "reason": "不是文件"}

    # 闸 1：文件名得像 Python 解释器
    stem = p.stem.lower()
    if not (stem.startswith("python") or stem.startswith("py")):
        return {"ok": False, "reason": f"文件名不像 Python 解释器：{p.name}"}
    if p.suffix.lower() not in (".exe", ".bat", ".cmd"):
        return {"ok": False, "reason": f"不是可执行文件：{p.name}"}

    # 闸 2：必须是 PE 可执行文件
    try:
        with open(p, "rb") as fh:
            if fh.read(2) != b"MZ":
                return {"ok": False, "reason": "不是有效的 Windows 可执行文件（缺少 MZ 头）"}
    except Exception as exc:                                # noqa: BLE001
        return {"ok": False, "reason": f"无法读取该文件：{exc}"}

    # 闸 3：固定命令 + 超时 + 不弹窗 + **不继承 RPC 管道作为 stdin**
    try:
        run_kwargs = _child_kwargs()
        run_kwargs.update(capture_output=True, text=True, timeout=45)
        r = subprocess.run([exe, "-c", "import bcml,oead;print('ok')"], **run_kwargs)
    except Exception as exc:                                # noqa: BLE001
        msg = str(exc)
        if "timed out" in msg:
            msg = "该程序在 45 秒内没有返回，可能不是 Python 解释器。"
        return {"ok": False, "reason": msg}

    if r.returncode == 0 and (r.stdout or "").strip().endswith("ok"):
        return {"ok": True, "reason": ""}
    tail = ((r.stderr or "") + (r.stdout or "")).strip()
    return {"ok": False, "reason": tail[-400:]}


def _require_bcml() -> None:
    if not BCML_OK:
        raise RuntimeError(f"BCML 导入失败：{_STARTUP_ERROR}")


def _known_folder(shell_folder_name: str, fallback: str) -> Path:
    """查 Windows 的「已知文件夹」真实路径（桌面 / 开始菜单可能会被 OneDrive 重定向）。

    直接用 `~/Desktop` 在开了 OneDrive 桌面同步的机器上会指向一个空目录，
    快捷方式就"建了但看不见"。优先读注册表里的实际位置。
    """
    try:
        import winreg

        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
        ) as key:
            value, _ = winreg.QueryValueEx(key, shell_folder_name)
            p = Path(os.path.expandvars(str(value)))
            if p.is_dir():
                return p
    except Exception:
        pass
    return Path(fallback).expanduser()


def make_app_shortcut(params) -> dict:
    """为**本应用**（BCML-WinUI3.exe）创建快捷方式。

    注意别和 `api.make_shortcut` 搞混：那个是原版的接口，建的是「命令行版 BCML」
    （pythonw.exe + bcml 包）的快捷方式 —— 对用这个 WinUI 界面的人来说没有意义。

    复用 BCML 自带编译模块里的 `manager.create_shortcut`（成熟的 .lnk 写入器），
    这样就不必在 C# 侧再引一套 COM（IShellLink）互操作。

    params: {"exe": 本应用 exe 绝对路径, "desktop": true=桌面 / false=开始菜单}
    """
    _require_bcml()
    from bcml import bcml as rsext

    raw_exe = str((params or {}).get("exe") or "").strip()
    if not raw_exe:
        raise ValueError("缺少 exe 参数（本应用可执行文件的绝对路径）")
    # 注意顺序：空串的 Path().resolve() 会变成当前工作目录，
    # 那样就会给 sidecar 目录建个快捷方式 —— 必须先把空值挡掉。
    exe = Path(raw_exe).resolve()
    if not exe.is_file():
        raise FileNotFoundError(f"找不到本应用的可执行文件：{exe}")

    desktop = bool((params or {}).get("desktop", True))
    if desktop:
        folder = _known_folder("Desktop", "~/Desktop")
    else:
        folder = _known_folder("Programs",
                               str(Path(os.environ.get("APPDATA", "")) /
                                   "Microsoft" / "Windows" / "Start Menu" / "Programs"))
    lnk = folder / "BCML-WinUI3.lnk"
    folder.mkdir(parents=True, exist_ok=True)

    # 图标直接用 exe 自身：csproj 的 ApplicationIcon 已经把图标编译进 exe 资源了
    rsext.manager.create_shortcut(str(exe), str(exe), str(lnk))
    return {
        "path": str(lnk),
        "exists": lnk.exists(),
        "desktop": desktop,
        "target": str(exe),
    }


# --------------------------------------------------------------------------
# Mod 选项重选
# --------------------------------------------------------------------------
#
# 背景：BNP 里带可选组件的 mod（例如「林可儿」「少女动作包」）安装后，mod 目录里会留下
# 一个 options/<变体名>/ 子目录树，每个子目录是同一组件的不同版本。
#
# BCML 自己在安装时用 install_mod(..., selects={...}) 处理：不在 selects 里的变体
# 直接 rmtree，在的就把文件硬链到 mod 根目录。但**装完之后就没有入口再改了**，
# 所以这里补一套「重选」。
#
# 关键点：find_modded_files() 会把整个 mod 目录（含 options/）都扫进去参与合并，
# 所以想控制生效范围，只要让 mod/options/ 下**只留下被选中的变体**即可。
#
# 要做到"可反复重选、且不残留旧配置"，就不能删掉变体本身 —— 那样下次想换回去就没了。
# 因此第一次重选时先在应用自己的存储里留一份 options/ 的**原始快照**（硬链，几乎不占空间），
# 之后每次重选都是「从快照整棵重建 → 只保留选中的」。
# 这样旧选择的文件不可能残留，因为目录是从快照重新长出来的。

_SNAPSHOT_ROOT_NAME = "mod-options"


def _rmtree_robust(path: Path) -> None:
    """删目录，扛得住 Windows 上的只读属性与短暂占用。

    `shutil.rmtree(p, ignore_errors=True)` 会**静默失败**：看起来删干净了，
    其实旧文件还在，后面重选选项时就会"旧配置残留"。这里显式清只读 + 重试。
    """
    if not path.exists():
        return

    def _on_error(_func, file_path, _exc):
        try:
            os.chmod(file_path, 0o666)
        except OSError:
            pass
        try:
            os.remove(file_path)
        except OSError:
            pass

    import time as _t
    for attempt in range(4):
        try:
            shutil.rmtree(path, onerror=_on_error)
        except OSError:
            pass
        if not path.exists():
            return
        _t.sleep(0.3 * (attempt + 1))


def _snapshot_root() -> Path:
    import os as _os

    base = _os.environ.get("LOCALAPPDATA") or _os.path.expanduser("~")
    return Path(base) / "BCML-WinUI3" / _SNAPSHOT_ROOT_NAME


def _safe_key(text: str) -> str:
    import hashlib as _hashlib

    keep = "".join(c if (c.isalnum() or c in "-_.") else "_" for c in text)[:60]
    digest = _hashlib.sha1(text.encode("utf-8")).hexdigest()[:8]
    return f"{keep}-{digest}"


def _link_or_copy(src: Path, dst: Path) -> None:
    """优先硬链（同卷、瞬时、不占空间），失败退回复制。"""
    dst.parent.mkdir(parents=True, exist_ok=True)
    if dst.exists():
        try:
            dst.unlink()
        except OSError:
            pass
    try:
        os.link(src, dst)
    except OSError:
        shutil.copy2(src, dst)


def _copy_tree_linked(src: Path, dst: Path) -> int:
    """把 src 整棵树硬链到 dst，返回文件数。"""
    n = 0
    for dp, _dirnames, filenames in os.walk(src):
        rel = Path(dp).relative_to(src)
        (dst / rel).mkdir(parents=True, exist_ok=True)
        for f in filenames:
            _link_or_copy(Path(dp) / f, dst / rel / f)
            n += 1
    return n


def _option_folders(mod: Path) -> list:
    od = mod / "options"
    if not od.is_dir():
        return []
    try:
        return sorted(d.name for d in od.iterdir() if d.is_dir())
    except OSError:
        return []


def _read_options_json(mod: Path) -> dict:
    data = _read_json(mod / "options.json", {})
    return data if isinstance(data, dict) else {}


def _option_definition(mod: Path) -> dict:
    """读 info.json 里的 options 定义（multi / single 两组）。"""
    info = _read_json(mod / "info.json", {})
    if not isinstance(info, dict):
        return {"multi": [], "single": []}
    opts = info.get("options") or {}
    if not isinstance(opts, dict):
        return {"multi": [], "single": []}
    return {
        "multi": opts.get("multi") or [],
        "single": opts.get("single") or [],
    }


def _snapshot_dir(mod: Path) -> tuple:
    """返回 (快照目录, 状态文件路径)。"""
    root = _snapshot_root() / _safe_key(mod.name)
    return root, root / "state.json"


def _tokens_for_match(text: str) -> set:
    """把名字切成可比较的词元（都转小写、丢掉纯数字和单字符）。

    要能对上 "少女动作包脚步声修正版GirlyAnimationPack10.6Fixed"
    ↔ "girly_animation_pack_10_6_footstepsound_fixed" 这种
    「中文显示名 + 拼音化英文」的差异，所以先按 camelCase / 数字边界拆词。
    """
    import re as _re

    s = _re.sub(r"([a-z])([A-Z])", r"\1 \2", text)   # camelCase 拆开
    s = _re.sub(r"(\D)(\d)", r"\1 \2", s)            # 字母/数字边界
    s = _re.sub(r"(\d)(\D)", r"\1 \2", s)
    toks = _re.split(r"[^a-z0-9]+", s.lower())
    return {t for t in toks if len(t) >= 2 and not t.isdigit()}


def _match_score(a: str, b: str) -> float:
    """两个名字的词元重叠度（0~1）。"""
    ta, tb = _tokens_for_match(a), _tokens_for_match(b)
    if not ta or not tb:
        return 0.0
    return len(ta & tb) / max(len(ta), len(tb))


def _find_source_archive(mod: Path) -> "Path | None":
    """回头找回这个 mod 的原始 bnp。

    BCML 安装模组时（install.py:377-380）会把没选中的 options/* 变体删掉，
    磁盘上的 mod 目录里找不回它们了；唯一还留着全量变体的地方就是**原始 bnp**。

    mod 目录名和 bnp 文件名往往对不上（前者常是中文显示名，后者是英文原文件名），
    所以用**词元重叠度**打分挑最像的那个，而不是简单子串匹配。
    只有分数过线（>=0.6）才认，宁可不匹配也不要拿错 bnp 去解包 ——
    解错会把别的 mod 的文件灌进 options/，比"换不了"严重得多。

    找不到就返回 None —— 调用方据此走降级路径（只列磁盘现存变体），不会报错。
    """
    import os as _os

    # 目录名形如 "0117_少女动作包脚步声修正版GirlyAnimationPack10.6Fixed"，
    # 去掉开头的优先级编号再比
    raw = mod.name
    keyword = raw.split("_", 1)[1] if "_" in raw else raw

    roots = [
        Path(_os.environ.get("USERPROFILE") or _os.path.expanduser("~")) / "Downloads",
        Path(_os.environ.get("USERPROFILE") or _os.path.expanduser("~")) / "Desktop",
        Path(_os.environ.get("LOCALAPPDATA") or "") / "BCML-WinUI3" / "sources",
    ]

    best, best_score = None, 0.0
    for root in roots:
        if not root.is_dir():
            continue
        for p in root.rglob("*.bnp"):
            # 汉化包（_CN）里也带全量 options，但体积/内容可能被改过；
            # 原版优先，所以对 _CN 结果扣一点分。
            sc = _match_score(keyword, p.stem)
            if p.stem.lower().endswith("_cn"):
                sc -= 0.05
            if sc > best_score:
                best, best_score = p, sc

    if best is None or best_score < 0.6:
        return None
    return best


def _populate_from_archive(mod: Path, archive: Path, pristine: Path) -> int:
    """把原始 bnp 里 options/ 的全量变体解到 pristine，返回变体目录数。

    **产出布局固定为 `pristine/options/<变体>/...`** —— 与 mod 目录一致，
    这样 `_option_folders(pristine)` 和 `_copy_tree_linked` 都能直接复用。

    用 BCML 自己的 7z（`install.get_7z_path()`）解包，避免依赖外部 PATH。

    两个坑（实测踩过）：
      · bnp 内部的路径分隔符是**反斜杠**（`options\\Battle_Girly\\...`），
        7z 的包含过滤器 `options/*` 匹配不上，会一个文件都不解 → 必须解全量；
      · 不要加 `-bsp1`：某些 7z 版本会把进度写进 stdout，与 capture 同用会让
        解包提前中止（实测：加 -bsp1 → 0 个目录；不加 → 75 个）。
    """
    import subprocess as _sp

    _rmtree_robust(pristine)
    pristine.mkdir(parents=True, exist_ok=True)

    try:
        from bcml.install import get_7z_path as _g7z     # type: ignore
        seven = _g7z()
    except Exception as exc:                             # noqa: BLE001
        _log_append(f"[modOptions] 找不到 7z，无法从 bnp 恢复变体：{exc}")
        return 0
    if not seven:
        return 0

    kwargs = _child_kwargs()
    args = [str(seven), "x", str(archive), f"-o{pristine}", "-y"]
    proc = _sp.run(args, stdout=_sp.PIPE, stderr=_sp.PIPE,
                   creationflags=kwargs.get("creationflags", 0), check=False)

    if not (pristine / "options").is_dir():
        _log_append(
            f"[modOptions] 从 {archive.name} 解包后没有 options/ 目录"
            f"（7z 退出码 {proc.returncode}）："
            f"{proc.stderr.decode('utf-8', 'replace')[:200]}")
        return 0

    # 7z 把整个归档都解开了，把无关的顶层（01007EF00011E000 / logs / info.json …）
    # 清掉，只留 options/ —— 快照只需要变体本身，多留一份 romfs 白占几百 MB。
    for entry in list(pristine.iterdir()):
        if entry.name != "options":
            if entry.is_dir():
                _rmtree_robust(entry)
            else:
                try:
                    entry.unlink()
                except OSError:
                    pass

    return len(_option_folders(pristine))


def _snapshot_options_dir(mod: Path) -> Path:
    """快照里存放变体的目录（**永远是 `pristine/options/`**）。

    统一布局是这次修复的关键之一：老版本建快照时是平的 `pristine/<变体>/`，
    新版本要求 `pristine/options/<变体>/`。集中在这个函数里返回，
    再配一个 `_repair_snapshot_layout()`，就不会出现"读的地方一套、写的地方另一套"。
    """
    return _snapshot_dir(mod)[0] / "pristine" / "options"


def _repair_snapshot_layout(mod: Path) -> bool:
    """把老版平的快照 `pristine/<变体>/` 迁成 `pristine/options/<变体>/`。

    返回 True 表示确实改了（调用方据此知道 state 需要重写）。
    迁移失败（比如 pristine 根本不存在）返回 False，不抛异常 ——
    调用方随后会因为"快照不完整"走重建路径，结果一样是对的。
    """
    snap = _snapshot_dir(mod)[0]
    pristine = snap / "pristine"
    if not pristine.is_dir():
        return False
    if (pristine / "options").is_dir():
        return False                      # 已经是新布局
    # 平布局：pristine 下的目录本身就是变体
    variants = [d for d in pristine.iterdir() if d.is_dir()]
    if not variants:
        return False
    target = pristine / "options"
    target.mkdir(parents=True, exist_ok=True)
    moved = 0
    for d in variants:
        dest = target / d.name
        if not dest.exists():
            try:
                d.rename(dest)
                moved += 1
            except OSError:
                pass
    if moved:
        _log_append(f"[modOptions] 已迁移 {mod.name} 的旧版快照布局"
                    f"（{moved} 个变体 → pristine/options/）")
    return moved > 0


def _ensure_snapshot(mod: Path) -> dict:
    """确保存在 options/ 的**全量**快照；返回状态字典。

    为什么快照必须来自原始 bnp 而不是当前磁盘：

        BCML 装模组时会删掉没选中的变体目录。如果在这里 `_copy_tree_linked(mod/"options")`，
        拿到的就是"已经被剪过一遍"的残留（实测：少女动作包定义 72 项、磁盘只剩 18 项），
        快照等于没起作用，用户永远换不回那 54 个被删的变体。

    因此优先级：
      1. 已经有完整快照（folders 覆盖 info.json 里的全部定义）→ 直接用；
      2. 能找到原始 bnp → 解出全量 options/ 建快照；
      3. 都做不到 → 退回用磁盘现存目录建快照，并在 state 里记 degraded=True
         （`mod_options` 会据此把缺失项标成不可用，`apply_mod_options` 也会明确报错）。
    """
    snap, state_path = _snapshot_dir(mod)
    pristine = snap / "pristine"
    # 老的平布局先迁到 pristine/options/，免得后面到处判两种布局
    _repair_snapshot_layout(mod)
    options_root = _snapshot_options_dir(mod)

    state = _read_json(state_path, {})
    if not isinstance(state, dict):
        state = {}
    snap_folders = set(state.get("folders") or [])

    defined = _option_definition(mod)
    defined_folders = {
        (i.get("folder") or "") for i in defined["multi"] if isinstance(i, dict)
    }
    for grp in defined["single"]:
        if not isinstance(grp, dict):
            continue
        defined_folders |= {
            (s.get("folder") or "")
            for s in (grp.get("options") or []) if isinstance(s, dict)
        }
    defined_folders.discard("")

    current = set(_option_folders(mod))

    # 已有快照是否"够用"：必须覆盖 info.json 定义的全部 folder，
    # 且 mod 目录没被换过（modPath 对得上）
    complete = (
        options_root.is_dir()
        and state.get("modPath") == str(mod)
        and defined_folders.issubset(snap_folders)
        and not (current - snap_folders)
    )
    if complete:
        return state

    # 快照不够用 → 重建
    archive = _find_source_archive(mod)
    count = 0
    degraded = True
    if archive is not None:
        count = _populate_from_archive(mod, archive, pristine)
        if count and defined_folders.issubset(
                set(_option_folders(_snapshot_options_dir(mod)))):
            degraded = False

    if degraded:
        # 找不到 bnp 或解不出全量 —— 至少把磁盘现存的存下来，
        # 这样"重选"仍然可用（只是换不了被删掉的那些）。
        # 同样保持 pristine/options/<变体> 的布局。
        _rmtree_robust(pristine)
        (pristine / "options").mkdir(parents=True, exist_ok=True)
        src_options = mod / "options"
        count = (_copy_tree_linked(src_options, pristine / "options")
                 if src_options.is_dir() else 0)

    state = {
        "modPath": str(mod),
        "folders": sorted(_option_folders(pristine / "options")),
        "files": count,
        "degraded": degraded,
        "sourceArchive": str(archive) if archive is not None else "",
        "takenAt": datetime.datetime.now().isoformat(timespec="seconds"),
    }
    state_path.parent.mkdir(parents=True, exist_ok=True)
    state_path.write_text(
        json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")

    how = "原始 bnp " + archive.name if not degraded and archive else "磁盘现存目录"
    _log_append(
        f"[modOptions] 已为 {mod.name} 建立选项目录快照："
        f"{len(state['folders'])} 个变体 / {count} 个文件（来源：{how}"
        + ("，降级：定义中的部分变体已无法恢复）" if degraded else "）")
    )
    return state


def mod_options(params) -> dict:
    """读一个 mod 的选项定义 + 当前选择，供界面画「重选选项」对话框。

    **选项清单以 info.json 的定义为准，而不是磁盘上还剩哪些目录。**
    原因（这条踩过坑，务必别再改回按磁盘枚举）：

        BCML 安装模组时（install.py:377-380）会把 mod/options/ 下**没被选中的**
        变体目录直接 shutil.rmtree 掉。所以磁盘上 options/ 只剩下"当前这一套选择"。
        如果按磁盘枚举，用户打开"重选选项"就只看得到自己已经选过的那些项，
        根本换不了 —— 正是要修的那个 bug。

    好在 info.json 里 definition 是完整的（multi + single 全部 72 项都在），
    拿它当清单就能把被删掉的变体重新列出来。

    返回的每一项带三种状态：
      · `exists`    —— 目录现在就在磁盘上（能直接重建）；
      · `restorable`—— 定义里有、但目录已被 BCML 删掉，需要重新解包原始 bnp 才拿得回来；
      · `selected`  —— 属于当前 options.json 记录的这一套选择。

    `snapshot=True` 时额外建/读一份 options/ 的原始快照（第一次重选时建），
    用来保证 `apply_mod_options` 重建时不会残留旧选择的文件。
    """
    _require_bcml()
    mod = Path((params or {}).get("mod") or "")
    if not mod.is_dir():
        raise FileNotFoundError(f"找不到模组目录：{mod}")

    want_snapshot = bool((params or {}).get("snapshot", False))
    defined = _option_definition(mod)
    available = set(_option_folders(mod))

    if want_snapshot:
        snap_state = _ensure_snapshot(mod)
        snapshot_folders = set(snap_state.get("folders") or [])
    else:
        snapshot_folders = set()

    # 可作为重建来源的目录 = 磁盘现存 ∪ 快照里有的
    restorable_pool = available | snapshot_folders

    current = set(_read_options_json(mod).get("selects") or [])
    if not current:
        current = available                      # 没记录过 → 当前装着的就是现状

    def normalize(item, group_desc="", group_name=""):
        folder = item.get("folder") or ""
        return {
            "name": item.get("name") or folder,
            "desc": item.get("desc") or "",
            "folder": folder,
            "exists": folder in restorable_pool,
            "selected": folder in current,
            "default": bool(item.get("default", False)),
            "groupName": group_name or "",
            "groupDesc": group_desc or "",
        }

    multi = [normalize(i) for i in defined["multi"] if isinstance(i, dict)]
    single = []
    for grp in defined["single"]:
        if not isinstance(grp, dict):
            continue
        gname = grp.get("name") or ""
        gdesc = grp.get("desc") or ""
        # 组名经常是一串下划线当分隔线用，真正可读的是 desc
        label = gdesc if (not gname.strip("_ \t") and gdesc) else gname
        label = label.strip("_ ").strip() or gdesc
        single.append({
            "label": label,
            "desc": gdesc,
            "required": bool(grp.get("required")),
            "options": [
                normalize(sub, gdesc, label)
                for sub in (grp.get("options") or []) if isinstance(sub, dict)
            ],
        })

    info = _read_json(mod / "info.json", {})
    all_defined = {
        (item.get("folder") or "")
        for item in defined["multi"] if isinstance(item, dict)
    }
    for grp in defined["single"]:
        if not isinstance(grp, dict):
            continue
        all_defined |= {
            (sub.get("folder") or "")
            for sub in (grp.get("options") or []) if isinstance(sub, dict)
        }
    all_defined.discard("")

    return {
        "mod": str(mod),
        "name": (info.get("name") if isinstance(info, dict) else None) or mod.name,
        "hasOptions": bool(multi or any(g["options"] for g in single)),
        "multi": multi,
        "single": single,
        "selected": sorted(current),
        "available": sorted(restorable_pool),
        # 定义里出现、但既不在磁盘也没进快照的 —— 界面用来提示"需要重装原始 bnp"
        "unavailable": sorted(all_defined - restorable_pool),
    }


def apply_mod_options(params) -> dict:
    """按新的选择重建 mod/options/，并写入 options.json。

    只改文件布局，不触发合并 —— 调用方随后调 api.remerge 让它生效。
    """
    _require_bcml()
    mod = Path((params or {}).get("mod") or "")
    if not mod.is_dir():
        raise FileNotFoundError(f"找不到模组目录：{mod}")

    selects = params.get("selects") or []
    if not isinstance(selects, list):
        raise ValueError("selects 必须是字符串数组")

    state = _ensure_snapshot(mod)
    snap_folders = set(state.get("folders") or [])
    on_disk = set(_option_folders(mod))

    wanted = {str(s) for s in selects}

    # 能从快照或磁盘重建出来的那些；剩下的说明目录已被 BCML 安装时删掉，
    # 而快照又是在删除之后才建的（= 拿不回来），必须明确报错而不是静默跳过，
    # 否则用户以为换成了、其实 options.json 里只写了一半。
    resolvable = snap_folders | on_disk
    unknown = sorted(wanted - resolvable)
    if unknown:
        raise ValueError(
            "这些选项已经不在磁盘上、也无法从快照恢复："
            + ", ".join(unknown)
            + "（多半是安装时被 BCML 删掉了；请重新解包原始 bnp 安装该模组后再选）"
        )

    # 快照的布局是 pristine/options/<变体>/……，
    # 统一走 _snapshot_options_dir() 取，别再各处手拼路径。
    pristine = _snapshot_options_dir(mod)
    options_dir = mod / "options"

    # 1) 先把选中的变体从快照取到一个临时暂存区 —— **先取后删**，
    #    避免"删完才发现快照缺文件"这种把 options/ 弄成半残的局面。
    staging = _snapshot_dir(mod)[0] / f"staging-{os.getpid()}"
    _rmtree_robust(staging)
    staging.mkdir(parents=True, exist_ok=True)

    applied = []
    missing_source = []
    for folder in sorted(wanted):
        src = pristine / folder
        if src.is_dir():
            _copy_tree_linked(src, staging / folder)
            applied.append(folder)
        else:
            missing_source.append(folder)

    if missing_source:
        _rmtree_robust(staging)
        raise ValueError(
            "快照里没有这些选项的文件：" + ", ".join(missing_source)
            + "（快照可能是在这些目录被删除之后才建立的；请重装原始 bnp）"
        )

    # 2) 暂存齐了，现在才整棵替换 options/（先删干净，旧选择不可能残留）
    _rmtree_robust(options_dir)
    options_dir.mkdir(parents=True, exist_ok=True)
    try:
        for folder in applied:
            _copy_tree_linked(staging / folder, options_dir / folder)
    finally:
        _rmtree_robust(staging)

    # 3) 把选中的选项**真正注入模组内容**，并重算合并日志。
    #
    #    这一步当初漏了，直接导致「重选选项不起作用」：
    #    BCML 合并不看 options/ 目录里有什么，它看的是模组根目录下的文件 +
    #    logs/ 里的日志。原版 install_mod 在收到 selects 时做两件事：
    #      a) 把选中选项目录里的文件 hardlink 到模组根（同名 SARC 会做合并
    #         而不是覆盖，见 install.py 的 FileExistsError 分支）；
    #      b) 之后由 generate_logs 按 options 字典重算 logs/。
    #    我们只重建了 options/ 就又调 remerge，remerge 读到的还是安装时那份
    #    日志，于是选项等于没换。
    #
    #    注入沿用 BCML 的语义：先清掉「上次注入留下的、且这次不再选」的文件。
    #    哪些是上次注入的？options/ 目录里的文件与模组根同名同相对路径的那些。
    #    这样不动模组本体自己的文件（它们和任何选项目录都不同名）。
    #    同名 SARC（.pack 等）走**合并**而非覆盖 —— 见 _place_option_file 的说明；
    #    顺序沿用用户的选择顺序，与 install_mod 一致。
    _inject_option_files(mod, options_dir, applied, order=params.get("selects"))

    # 4) 记账：BCML 只认 `disable` / `options` 两个字段，**不读 `selects`**。
    #    `options` 是「按合并器分组的选项字典」，不是目录列表，所以不能拿
    #    applied 往里塞 —— 塞错了会让 merger.set_options 拿到 garbage。
    #    保留原有内容，只补 keep 一份 selects 供我们自己回显已选项。
    data = _read_options_json(mod)
    data.setdefault("disable", [])
    data.setdefault("options", {})
    data["selects"] = applied
    _write_json_atomic(mod / "options.json", data)

    # 5) 重算日志 —— 不做这步上面全白费（remerge 读的是 logs/）。
    #    失败要明确报出来：宁可让用户看到「选项没生效」，
    #    也好过悄悄跑完、结果还是默认选项。
    try:
        _regen_logs(mod, data)
    except Exception as err:  # noqa: BLE001 - 原样抛给上层展示
        raise RuntimeError(
            f"选项文件已更新，但重算合并日志失败，选项不会生效：{err}"
        ) from err

    _log_append(f"[modOptions] {mod.name}：应用 {len(applied)} 个选项"
                f"（快照共 {len(snap_folders)} 个可选，已注入并重算日志）")
    return {
        "mod": str(mod),
        "applied": applied,
        "available": sorted(snap_folders | on_disk),
    }


def _inject_option_files(mod: Path, options_dir: Path, applied, order=None) -> None:
    """把 `options/<已选变体>/` 里的文件铺到模组根目录（BCML install_mod 的语义）。

    为什么必须「先清后铺」：上一次重选可能选了别的变体，那些文件还留在模组根里，
    不清理就会和这次的选择叠在一起（旧变体的文件持续生效）。

    为什么要做 SARC 合并而不是覆盖（这条是「重选后游戏内容没变」的真正原因）：

        少女动作包这类模组，**多个变体各自带一份同名 `Pack/TitleBG.pack`**
        （实测 Walk_Girly 和 Run_Girly 各带一份）。原版 install_mod 铺文件时
        `os.link` 会撞 FileExistsError，它就在那个分支里把两份 SARC **合并**
        —— 保留旧 SARC 中不被新 SARC 覆盖的条目，再写入新 SARC 的全部条目。
        我们之前直接 unlink + link（纯覆盖），结果是后铺的那一份把先铺的整份吃掉，
        于是「重选选项」对 pack 层完全没有效果：根目录永远只有最后一份 pack。

    因此这里逐行对齐 install_mod 的分支：
        · 非 SARC 同名文件 → 覆盖（unlink + link）
        · SARC 同名文件   → 合并（无法解析的任一侧退化为覆盖 / 跳过）

    `order` 用于控制铺入顺序。原版按 `selects` 给出的顺序迭代，后铺的变体在
    SARC 合并中优先级更高（同名条目以它为准）。传 None 时退回目录名的字典序，
    与旧行为一致。
    """
    selected = set(applied)

    # 1) 清掉上一次注入、这次不再选的文件。
    #    注意：**只删「没有任何一个已选变体也提供」的文件**。
    #    两个已选变体带同名 pack 时，那个 pack 是它们的合并结果，属于"已选"，
    #    不能被这步删掉（否则每轮都要从零重建，且顺序敏感）。
    still_wanted = set()
    for name in selected:
        src_dir = options_dir / name
        if not src_dir.is_dir():
            continue
        for f in src_dir.rglob("*"):
            if f.is_file():
                still_wanted.add(f.relative_to(src_dir))

    for opt in sorted(p for p in options_dir.glob("*") if p.is_dir()):
        if opt.name in selected:
            continue
        for f in opt.rglob("*"):
            if not f.is_file():
                continue
            rel = f.relative_to(opt)
            if rel in still_wanted:
                continue
            target = mod / rel
            if target.exists():
                try:
                    target.unlink()
                except OSError:
                    pass

    # 2) 铺入这次选中的文件，顺序沿用调用方给的选择顺序
    names = list(order) if order else sorted(selected)
    for name in names:
        src_dir = options_dir / name
        if not src_dir.is_dir():
            continue
        for f in src_dir.rglob("*"):
            if not f.is_file():
                continue
            target = mod / f.relative_to(src_dir)
            target.parent.mkdir(parents=True, exist_ok=True)
            _place_option_file(f, target)


def _place_option_file(src: Path, target: Path) -> None:
    """把单个选项文件放到 target（对齐 install_mod 的 `os.link` 分支）。

    - target 不存在 → 直接 hardlink（跨卷退回复制）
    - target 已存在且是 SARC → **合并**，不是覆盖
    - target 已存在且不是 SARC → 覆盖
    """
    if not target.exists():
        _link_or_copy(src, target)
        return

    from bcml import util as _bcml_util  # noqa: PLC0415 - 延迟导入

    if src.suffix.lower() not in {e.lower() for e in _bcml_util.SARC_EXTS}:
        try:
            target.unlink()
        except OSError:
            pass
        _link_or_copy(src, target)
        return

    import oead  # noqa: PLC0415 - BCML 自带

    # 旧内容解析失败 → 就当它不可合并，直接用新文件顶上
    try:
        old_sarc = oead.Sarc(_bcml_util.unyaz_if_needed(target.read_bytes()))
    except Exception:  # noqa: BLE001 - BCML 原文也是宽 except
        try:
            target.unlink()
        except OSError:
            pass
        _link_or_copy(src, target)
        return

    # 新内容解析失败 → 保留旧的（跳过后铺这一份，与 BCML 一致）
    try:
        link_sarc = oead.Sarc(_bcml_util.unyaz_if_needed(src.read_bytes()))
    except Exception:  # noqa: BLE001
        del old_sarc
        return

    writer = oead.SarcWriter.from_sarc(link_sarc)
    incoming = {f.name for f in link_sarc.get_files()}
    for old_file in old_sarc.get_files():
        if old_file.name not in incoming:
            writer.files[old_file.name] = bytes(old_file.data)
    del old_sarc
    del link_sarc

    # 合并结果写回 target。此时 target 可能还硬链接着某个已选变体的源文件，
    # 而写 bytes 会改动同一 inode —— 先把链接解开，避免污染 options/ 里的原件。
    try:
        target.unlink()
    except OSError:
        pass
    target.write_bytes(writer.write()[1])
    del writer


def _link_or_copy(src: Path, target: Path) -> None:
    """优先硬链接（省磁盘，与 BCML 一致），不支持时退回复制。"""
    try:
        os.link(src, target)
    except OSError:
        shutil.copy2(src, target)


def _regen_logs(mod: Path, options: dict) -> None:
    """调 BCML 自己的 generate_logs 重算 logs/。

    必须复用 BCML 的实现而不是自己写：日志格式与合并器选项的消费方式都在它手里，
    自己写一份必然与 remerge 时读的那份对不上。

    logs/ 先删掉 —— generate_logs 是增量追加语义，留着旧日志会让已经不存在的
    旧选项残留下来（正是「换了选项但没生效」的另一半原因）。
    """
    _require_bcml()
    from bcml import install as _bcml_install  # noqa: PLC0415 - 延迟导入，缩短启动时间

    logs = mod / "logs"
    logs.mkdir(parents=True, exist_ok=True)
    for f in logs.glob("*"):
        try:
            if f.is_dir():
                shutil.rmtree(f, ignore_errors=True)
            else:
                f.unlink()
        except OSError:
            pass

    _bcml_install.generate_logs(tmp_dir=mod, options=options)


def _child_kwargs() -> dict:
    """**所有**子进程都必须带上这一组参数 —— 尤其是 `stdin=DEVNULL`。

    为什么 stdin 必须重定向（这条踩得很惨，务必别删）：

        本进程的 stdin 是跟 C# 之间的 RPC 管道。Windows 上 `subprocess` 默认
        `close_fds=True` 只关 fd>=3，**fd 0 会被子进程继承**。实测结果是：

            cmd /c echo hi            → 正常退出
            python -V                 → 正常退出
            python -c "print(1)"      → **永久挂起**
            同上 + stdin=DEVNULL      → 正常退出

        也就是说，只要子进程是「带 -c 跑代码的 Python」并且继承了这条 RPC 管道，
        它就会卡住不退出 —— 而本 sidecar 是单 worker 串行执行的，一个卡住的子进程
        会把**整个后端**一起拖死，界面表现是"点了没反应、之后所有操作都没反应"。

        受影响的是：`sys.probePython`（Python 路径校验）以及全部走 7z 的工具
        （BNP Creator / 平台转换 / 升级旧 BNP / 转独立包 / 生成 RSTB）——
        它们都会 spawn 子进程。

        另外一个隐患：子进程继承管道后如果去读 stdin，会把 RPC 请求帧偷走，
        协议直接错乱。DEVNULL 一并把这个问题堵掉。

    其余几项：不弹黑窗、明确 UTF-8 解码（避免中文 Windows 上 GBK 解码炸掉）、
    不因退出码抛异常（由调用方按需判定）。
    """
    import subprocess

    kwargs = dict(
        stdin=subprocess.DEVNULL,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    from bcml import util as _util
    if _util.SYSTEM == "Windows":
        kwargs["creationflags"] = _util.CREATE_NO_WINDOW
    return kwargs


def _check_7z(result, out_path) -> None:
    """判定一次 7z 打包是否真的成功。

    原来只看 `r.stderr` 非空就报错，两个方向都不对：
      · 7z 把「正在压缩 xxx」这类进度写到 stderr，成功也会被判成失败；
      · 反过来 7z 返回非 0（磁盘满、文件被占用）却因为 stderr 恰好为空而当作成功，
        最后拿到一个 0 字节或半截的 .bnp。
    正确判据是退出码，外加「产物确实存在且非空」。
    """
    if result.returncode != 0:
        detail = (result.stderr or result.stdout or "").strip()
        raise RuntimeError(f"7z 打包失败（返回码 {result.returncode}）：{detail[-400:]}")
    p = Path(out_path)
    if not p.exists() or p.stat().st_size == 0:
        raise RuntimeError(f"7z 报告成功，但产物不存在或为空：{p}")


def _run_7z(args):
    """跑一次 7z，统一「不弹窗 + 明确编码 + 不因编码问题崩掉」。
    """
    from subprocess import run as _run

    from bcml import util as _util

    kwargs = _child_kwargs()
    kwargs.update(capture_output=True, text=True)
    return _run([_util.get_7z_path(), *args], **kwargs)


def _pack_dir_to_archive(mod_dir, out) -> Path:
    """把 mod_dir 下的内容打包成归档，**原子落盘**。

    先打到同目录的临时名，成功后再 os.replace 顶掉目标。
    直接往目标写的话，一旦 7z 中途失败（磁盘满、源文件被占用），
    原本那个好的输出文件已经被 unlink 掉了 —— 用户既没拿到新的，也丢了旧的。
    """
    out = Path(out)
    out.parent.mkdir(parents=True, exist_ok=True)
    tmp = out.with_name(out.name + ".part")
    if tmp.exists():
        try:
            tmp.unlink()
        except OSError:
            pass
    try:
        _check_7z(_run_7z(["a", str(tmp), str(Path(mod_dir) / "*")]), tmp)
        os.replace(tmp, out)
    except BaseException:
        try:
            if tmp.exists():
                tmp.unlink()
        except OSError:
            pass
        raise
    return out


def _write_json_atomic(path, data) -> None:
    """JSON 原子写：先写 .tmp 再替换，避免中途崩溃留下半个文件。

    模组的 `options.json` 是合并器选项的状态，写坏一次这个模组就直接加载不了。
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(tmp, path)


def _pack_bnp(mod_dir, out) -> int:
    """把 mod_dir 下的内容用 7z 打包成 .bnp（与 dev.create_bnp_mod 的收尾一致）。"""
    out = Path(out)
    if out.suffix.lower() != ".bnp":
        out = out.with_suffix(".bnp")
    return _pack_dir_to_archive(mod_dir, out).stat().st_size


def _meta_from_params(params: dict) -> dict:
    """把界面送来的表单字段整理成 BCML 的 info.json 结构。

    只保留有值的字段：create_bnp_mod 会直接把这本字典写成 info.json，
    空字符串键会污染模组元数据（界面上表现为作者/网址是空行）。
    """
    def _s(key, default=""):
        v = (params or {}).get(key, default)
        return "" if v is None else str(v).strip()

    meta = {
        "name": _s("name") or "Unnamed Mod",
        "version": _s("version") or "1.0.0",
        "desc": _s("desc"),
        "url": _s("url"),
        "image": _s("image"),
    }
    depends = (params or {}).get("depends") or []
    if isinstance(depends, str):
        depends = [d.strip() for d in depends.replace(",", "\n").split("\n") if d.strip()]
    meta["depends"] = [str(d).strip() for d in depends if str(d).strip()]
    return meta


def create_bnp_headless(params) -> dict:
    """无界面的 BNP 打包（对应原版工具 BNP Creator / Api.create_bnp）。

    为什么不用 Api.create_bnp：它内部用 self.window.create_file_dialog 选保存路径，
    在 sidecar（无界面进程）里会直接抛异常。这里改成界面先选好 folder 与 output。

    params: {
      folder:  待打包的模组目录（或 .bnp，会自动解包）
      output:  输出 .bnp 路径
      name/version/desc/url/image/depends: 元数据
      options:  选项定义 {"options": {...}, "disable": [...]}
      selects:  每个选项的默认勾选（写进 meta.options）
    }
    """
    _require_bcml()
    from bcml import dev

    folder = Path((params or {}).get("folder") or "")
    if not folder.exists():
        raise FileNotFoundError(f"找不到模组目录：{folder}")
    out = (params or {}).get("output")
    if not out:
        raise ValueError("缺少输出路径 output")

    meta = _meta_from_params(params)
    meta["options"] = (params or {}).get("selects") or {}
    options = (params or {}).get("options") or {"options": {}, "disable": []}

    dev.create_bnp_mod(mod=folder, output=Path(out), meta=meta, options=options)
    final = Path(out)
    if final.suffix.lower() != ".bnp":
        final = final.with_suffix(".bnp")
    return {"output": str(final), "size": final.stat().st_size if final.exists() else 0,
            "name": meta["name"], "version": meta["version"]}


def upgrade_bnp_headless(params) -> dict:
    """把旧版 BNP 升级成当前 BCML 规范（对应原版工具 Upgrade Old BNP）。

    原版 upgrade_bnp 只是解包再打包，真正让它「升级」的是 install.open_mod 会顺带
    调用 upgrade.rules_to_info 把旧 rules.txt 转成 info.json。这里复用同一条链路。
    """
    _require_bcml()
    from bcml import install

    src = Path((params or {}).get("mod") or "")
    if not src.exists():
        raise FileNotFoundError(f"找不到 BNP 文件：{src}")
    out = (params or {}).get("output")
    if not out:
        raise ValueError("缺少输出路径 output")

    tmp = install.open_mod(src)
    try:
        upgraded = (Path(tmp) / "info.json").exists()
        size = _pack_bnp(tmp, out)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    # _pack_bnp 会把后缀补成 .bnp，这里必须回报**真实落盘的那个路径**，
    # 否则界面按返回值去操作会找不到文件。
    final = Path(out)
    if final.suffix.lower() != ".bnp":
        final = final.with_suffix(".bnp")
    return {"output": str(final), "size": size, "upgraded": upgraded}


def gen_rstb_headless(params) -> dict:
    """为指定模组生成 RSTB（对应原版工具 Generate RSTB for Mod / Api.gen_rstb）。

    原版从 self.get_folder() 取目录（无界面进程不可用），这里由界面传入 mod 目录。
    做法与 Api.gen_rstb 完全一致：临时上下文里只开 rstb 合并器跑一次合并，
    再把主合并目录里的 ResourceSizeTable 拷进该模组。
    """
    _require_bcml()
    from shutil import copyfile

    from bcml import install, mergers
    from bcml import util as _util

    mod = Path((params or {}).get("mod") or "")
    if not mod.is_dir():
        raise FileNotFoundError(f"找不到模组目录：{mod}")

    # 如果传进来的是个"裸内容目录"（既没有 info.json 也没有 rules.txt），
    # install_mod 需要一个元数据文件才能干活 —— 这里造一个临时的，
    # 但**用完必须删掉**：这是用户自己的模组目录，不该被塞进一份假元数据。
    # （之前是直接 write_text 到 mod 目录里就不管了，用户会莫名其妙多出一个 info.json。）
    synthetic_info = None
    if not ((mod / "info.json").exists() or (mod / "rules.txt").exists()):
        synthetic_info = mod / "info.json"
        synthetic_info.write_text(
            json.dumps({
                "name": "Temp", "desc": "Temp pack", "url": "",
                "id": "VGVtcD0wLjA=", "image": "", "version": "1.0.0",
                "depends": [], "options": {},
                "platform": "wiiu" if _util.get_settings("wiiu") else "switch",
            }), encoding="utf-8")

    try:
        with _util.TempModContext():
            install.install_mod(
                mod, merge_now=True,
                options={"options": {}, "disable": [
                    m.NAME for m in mergers.get_mergers() if m.NAME != "rstb"]},
            )
            dst_dir = mod / _util.get_content_path() / "System" / "Resource"
            dst_dir.mkdir(parents=True, exist_ok=True)
            src_file = (_util.get_master_modpack_dir() / _util.get_content_path()
                        / "System" / "Resource" / "ResourceSizeTable.product.srsizetable")
            copyfile(src_file, dst_dir / "ResourceSizeTable.product.srsizetable")
    finally:
        if synthetic_info is not None:
            try:
                synthetic_info.unlink()
            except OSError:
                _log_append(f"[genRstb] 临时 info.json 未能删除：{synthetic_info}")

    return {"mod": str(mod), "rstb": str(dst_dir / "ResourceSizeTable.product.srsizetable"),
            "size": (dst_dir / "ResourceSizeTable.product.srsizetable").stat().st_size}


def bnp_to_standalone_headless(params) -> dict:
    """BNP → 独立图形包 / Atmosphere 包（原版 Api.bnp_to_gfx）。

    原版先 file_pick 选 bnp、再弹保存框选 zip，两处都不可用于无界面进程。
    这里整条链路保留：TempModContext → install_mod(merge_now) → install.export(standalone)。
    """
    _require_bcml()
    from bcml import install
    from bcml import util as _util

    bnp = Path((params or {}).get("mod") or "")
    if not bnp.exists():
        raise FileNotFoundError(f"找不到 BNP 文件：{bnp}")
    out = (params or {}).get("output")
    if not out:
        raise ValueError("缺少输出路径 output")
    out = Path(out)
    if out.suffix.lower() != ".zip":
        out = out.with_suffix(".zip")

    with _util.TempModContext():
        install.install_mod(
            bnp, merge_now=True,
            options={"options": {"texts": {"all_langs": True}}, "disable": []},
        )
        install.export(out, standalone=True)
    return {"output": str(out), "size": out.stat().st_size if out.exists() else 0,
            "kind": "graphicpack" if _util.get_settings("wiiu") else "atmosphere"}


def compare_mods_headless(params) -> dict:
    """对比两个模组的合并器改动（原版 Compare Mods）。

    原版是独立的比对视图；这里把左右两侧的 get_mod_edits 结果按合并器分组，
    算出差集，界面负责并排渲染。
    """
    _require_bcml()
    from bcml import mergers
    from bcml.util import BcmlMod

    def _edits(path):
        # 注意：BcmlMod.from_json 收的是 **dict**（要有 "path" 键），不是路径字符串。
        # 原版 Api.get_mod_edits 也是这么调的（params["mod"] 本身是 to_json 出来的字典）。
        mod = BcmlMod.from_json({"path": str(path)})
        out = {}
        for merger in sorted({m() for m in mergers.get_mergers()}, key=lambda m: m.NAME):
            try:
                out[merger.friendly_name] = sorted(
                    {str(v) for v in merger.get_mod_edit_info(mod)})
            except Exception:
                out[merger.friendly_name] = []
        return out

    left_p = str((params or {}).get("left") or "")
    right_p = str((params or {}).get("right") or "")
    if not left_p or not right_p:
        raise ValueError("需要同时提供 left 与 right 两个模组")
    left, right = _edits(left_p), _edits(right_p)

    groups = []
    for name in sorted(set(left) | set(right)):
        l, r = set(left.get(name, [])), set(right.get(name, []))
        if not l and not r:
            continue
        groups.append({
            "merger": name,
            "onlyLeft": sorted(l - r),
            "onlyRight": sorted(r - l),
            "shared": sorted(l & r),
        })
    return {"left": left_p, "right": right_p, "groups": groups,
            "totalLeft": sum(len(v) for v in left.values()),
            "totalRight": sum(len(v) for v in right.values())}


def mod_edits_headless(params) -> dict:
    """单个模组的合并器改动清单（模组详情页用）。

    对应原版 Api.get_mod_edits —— 那个版本直接读 params["mod"]，本来就无界面依赖，
    但它把整个 BcmlMod 反序列化了一遍；这里加一层容错，让个别合并器报错不影响整体。
    """
    _require_bcml()
    from bcml import mergers
    from bcml.util import BcmlMod

    mod_p = str((params or {}).get("mod") or "")
    if not mod_p:
        raise ValueError("缺少模组路径 mod")
    mod = BcmlMod.from_json({"path": mod_p})
    out = {}
    for merger in sorted({m() for m in mergers.get_mergers()}, key=lambda m: m.NAME):
        try:
            out[merger.friendly_name] = sorted(
                {str(v) for v in merger.get_mod_edit_info(mod)})
        except Exception as exc:                       # noqa: BLE001
            out[merger.friendly_name] = []
            _log_append(f"[modEdits] {merger.NAME}: {exc}")
    return {"mod": mod_p,
            "groups": [{"merger": k, "files": v} for k, v in out.items() if v]}


def mod_details_headless(params) -> dict:
    """模组详情页专用：一次调用同时拿到「元数据 + 选项目录 + 合并器改动清单」。

    params: {
      mod:   模组目录绝对路径（必填）
      meta:  是否读 info.json 元数据（默认 True）
      edits: 是否遍历合并器改动清单（默认 True；这步会走一遍模组文件，可单独关掉）
    }

    为什么合成一个接口：详情页选中一个模组时要同时刷新元信息、选项与改动清单，
    分三次 RPC 会让界面出现三次"转圈"。合并成一次后只有一个加载态。
    单个合并器内部报错不影响整体 —— 出错的那组按空处理，并把原因记进日志环。
    """
    _require_bcml()

    mod_p = str((params or {}).get("mod") or "")
    if not mod_p:
        raise ValueError("缺少模组路径 mod")
    d = Path(mod_p)
    if not d.is_dir():
        raise FileNotFoundError(f"找不到模组目录：{d}")

    want_meta = bool((params or {}).get("meta", True))
    want_edits = bool((params or {}).get("edits", True))

    meta = None
    if want_meta:
        info = _read_json(d / "info.json", {}) or {}
        depends = info.get("depends")
        if not isinstance(depends, list):
            depends = []
        meta = {
            "name": str(info.get("name") or d.name),
            "version": str(info.get("version") or ""),
            "url": str(info.get("url") or ""),
            "platform": str(info.get("platform") or ""),
            "id": str(info.get("id") or ""),
            "priority": int(info.get("priority") or 0),
            "depends": [str(x) for x in depends if str(x).strip()],
            "hasImage": bool(str(info.get("image") or "").strip()),
            "disabled": (d / ".disabled").exists(),
            "processed": (d / ".processed").exists(),
            # 已启用的选项目录（安装时未勾选的目录会被 BCML 直接删掉）
            "optionFolders": _option_folders(d),
            # options.json 里的 disable 列表：被显式关掉的合并器
            "disabledMergers": [
                str(x) for x in (_read_json(d / "options.json", {}) or {}).get("disable") or []
            ],
        }

    edits = []
    if want_edits:
        from bcml import mergers as _mergers
        from bcml.util import BcmlMod as _BcmlMod

        # 注意：BcmlMod.from_json 收的是 dict（要有 "path" 键），不是路径字符串。
        mod = _BcmlMod.from_json({"path": mod_p})
        for merger in sorted({m() for m in _mergers.get_mergers()}, key=lambda m: m.NAME):
            try:
                files = sorted({str(v) for v in merger.get_mod_edit_info(mod)})
            except Exception as exc:                   # noqa: BLE001
                files = []
                _log_append(f"[modDetails] {merger.NAME}: {exc}")
            if files:
                edits.append({
                    "merger": merger.friendly_name,
                    "name": merger.NAME,
                    "files": files,
                })
        edits.sort(key=lambda g: (-len(g["files"]), g["merger"]))

    return {
        "mod": mod_p,
        "meta": meta,
        "edits": edits,
        "total": sum(len(g["files"]) for g in edits),
    }


def _clear_logs(params=None) -> dict:
    with _LOG_LOCK:
        _LOG_RING.clear()
    return {"ok": True}


_SIMPLE = {
    "ping": lambda p: {"ok": True, "pid": os.getpid()},
    "info": lambda p: _describe(),
    "logs": lambda p: {"lines": _log_snapshot(int((p or {}).get("tail", 300)))},
    "clearLogs": _clear_logs,
    "reorderMods": reorder_mods,
    "exportProfile": export_profile,
    "applyProfile": apply_profile,
    "convertBnp": convert_bnp_headless,
    "createBnp": create_bnp_headless,
    "upgradeBnp": upgrade_bnp_headless,
    "genRstb": gen_rstb_headless,
    "bnpToStandalone": bnp_to_standalone_headless,
    "compareMods": compare_mods_headless,
    "modEdits": mod_edits_headless,
    "modDetails": mod_details_headless,
    "updateMod": update_mod_headless,
    "makeAppShortcut": make_app_shortcut,
    "probePython": probe_python,
    "modOptions": mod_options,
    "modAction": mod_action_headless,
    "repairPriorities": repair_priorities,
    "applyModOptions": apply_mod_options,
    "masterModpack": open_master_modpack,
    "shutdown": None,      # 特殊处理
}


def handle(req: dict):
    # 报文形状先兜住：JSON-RPC 允许批量数组，数组里的元素不一定是对象。
    # 不校验的话，下面 req.get 会抛 AttributeError，直接穿透主循环把 sidecar 打挂
    # （表现为界面所有调用同时失败、后端进程消失）。
    if not isinstance(req, dict):
        fail(None, -32600, f"请求必须是 JSON 对象，收到 {type(req).__name__}")
        return

    req_id = req.get("id")
    method = req.get("method")
    if not isinstance(method, str):
        fail(req_id, -32600, "请求缺少 method 字段（或类型不是字符串）")
        return
    params = req.get("params")

    # 兼容两种调用写法：sys.info 与 info（客户端用带前缀的，早期脚本用不带前缀的）
    simple = method[4:] if method.startswith("sys.") else method
    if simple in _SIMPLE:
        if simple == "shutdown":
            reply(req_id, {"ok": True})
            threading.Timer(0.1, lambda: os._exit(0)).start()
            return
        try:
            reply(req_id, _SIMPLE[simple](params))
        except Exception as exc:
            fail(req_id, -32000, str(exc), traceback.format_exc())
        return

    if method.startswith("api."):
        name = method[4:]
        try:
            reply(req_id, _call_api(name, params))
        except NeedsUiError as exc:
            fail(req_id, -32010, str(exc))
        except NoSuchMethodError:
            # 只有「方法真的不存在」才是 -32601。
            # 注意顺序：NoSuchMethodError 继承自 AttributeError，必须排在它前面，
            # 否则方法存在但实现内部抛 AttributeError 的情况会被误报成「方法不存在」。
            fail(req_id, -32601, f"后端没有 {name} 这个方法")
        except TypeError as exc:
            fail(req_id, -32602, f"参数不正确：{exc}", traceback.format_exc())
        except Exception as exc:                      # noqa: BLE001
            fail(req_id, -32000, str(exc), traceback.format_exc())
        return

    fail(req_id, -32601, f"未知方法：{method}")


# --------------------------------------------------------------------------
# 请求分流
# --------------------------------------------------------------------------
# 为什么要有 worker 线程：
#   原来请求是读循环里同步处理的。一次 merge 要跑几分钟，这期间任何请求
#   （包括只想看看日志的 sys.logs）都得排在后面等 —— 界面看起来就是"卡死了"，
#   连"到底进行到哪一步"都问不出来。
#
# 为什么不是"全都并发跑"：
#   BCML 的 Api / mergers 大量共享内存状态（settings、缓存、mods_nix 目录），
#   并发跑只会互相踩。所以除少数几个**根本不碰 BCML 状态**的只读请求外，
#   其余请求仍然严格串行 —— 只是从"堵读循环"变成"堵 worker 队列"，
#   语义不变、顺序不变，但读循环始终是活的。
_FAST_INLINE = {"ping", "logs", "clearLogs"}

_request_q: "queue.Queue" = queue.Queue()


def _simple_name(method) -> str:
    """sys.info / info 都归到 info。"""
    if not isinstance(method, str):
        return ""
    return method[4:] if method.startswith("sys.") else method


def _dispatch(req) -> None:
    """纯内存读就地答，其余排队给 worker 串行执行。"""
    if isinstance(req, dict) and _simple_name(req.get("method")) in _FAST_INLINE:
        handle(req)
    else:
        _request_q.put(req)


def _worker_loop() -> None:
    while True:
        item = _request_q.get()
        try:
            if item is None:            # 主循环退出时投的毒丸
                return
            handle(item)
        except Exception as exc:        # noqa: BLE001
            # handle 内部已经把所有异常转成错误帧了；真漏出来也不能让 worker 死掉，
            # 否则后面所有请求都会永远排队等不到人。
            _log_append(f"[worker] 未捕获异常：{exc}")
            try:
                rid = item.get("id") if isinstance(item, dict) else None
                fail(rid, -32000, f"后端内部错误：{exc}", traceback.format_exc())
            except Exception:
                pass
        finally:
            _request_q.task_done()


# --------------------------------------------------------------------------
# 3) 主循环
# --------------------------------------------------------------------------
def _read_exact(n: int) -> bytes:
    buf = b""
    while len(buf) < n:
        chunk = _RPC_IN.read(n - len(buf))
        if not chunk:
            return b""
        buf += chunk
    return buf


def main() -> int:
    # multiprocessing 子进程会以 __mp_main__ 重新导入本文件；
    # 下面的 guard 保证它们不会启动第二个 RPC 服务。
    import multiprocessing

    try:
        multiprocessing.freeze_support()
        multiprocessing.set_start_method("spawn", force=True)
    except Exception:
        pass

    worker = threading.Thread(target=_worker_loop, name="rpc-worker", daemon=True)
    worker.start()

    notify("notify.ready", _describe())
    if not BCML_OK:
        notify("notify.error", {"text": f"BCML 导入失败：{_STARTUP_ERROR}"})

    while True:
        hdr = _read_exact(4)
        if len(hdr) < 4:
            break
        (n,) = struct.unpack("<I", hdr)
        if n == 0 or n > 64 * 1024 * 1024:
            break
        body = _read_exact(n)
        if len(body) < n:
            break
        try:
            req = json.loads(body.decode("utf-8"))
        except Exception as exc:                      # noqa: BLE001
            fail(None, -32700, f"JSON 解析失败：{exc}")
            continue
        if isinstance(req, list):
            for r in req:
                _dispatch(r)
        else:
            _dispatch(req)

    # 前端断开了：让 worker 收工，别留一个后台线程吊着
    _request_q.put(None)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
