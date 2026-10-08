# Agent 调用 BCML 自动打包指南

给「由脚本 / AI Agent 无界面驱动 BCML 完成模组合并与打包」的实操说明。
内容全部来自本仓库 `tools/` 下已跑通的脚本，以及踩过的坑。

适用范围：BotW（Switch）模组合并 → 导出 `01007EF00011E000/romfs` + `01007EF00011F001/romfs`
这类的「整合包」产物。Wii U 同理，只需把 `wiiu` 置真。

---

## 0. 一句话总览

```
写配置档案(JSON)  →  applyProfile  →  applyModOptions(有选项的模组)  →  remerge  →  归档 merged_nx
```

`remerge` 才是真正干活的一步：它会删掉主模组 `mods_nx/9999_BCML`，再按
**当前 `game_dir_nx` + 全部已装模组**重新合并一遍，输出到 `%LOCALAPPDATA%/bcml/merged_nx`。

---

## 1. 环境与路径约定

**必须用随包内嵌运行时**（工作区 Python 里没有 `bcml`）：

```
publish/win-x64/runtime/python39/python.exe
```

| 用途 | 路径 |
|---|---|
| BCML 数据根 | `%LOCALAPPDATA%/bcml` |
| 已装模组 | `%LOCALAPPDATA%/bcml/mods_nx/<4位优先级>_<安全名>/` |
| 主模组（游戏本体的映射，会被 remerge 删重建） | `%LOCALAPPDATA%/bcml/mods_nx/9999_BCML/` |
| 合并输出 | `%LOCALAPPDATA%/bcml/merged_nx/<appid>/romfs/...` |
| 设置 | `%LOCALAPPDATA%/bcml/settings.json`（`game_dir_nx` / `dlc_dir_nx`） |

产物只包含**被模组改过的那批文件**（典型 1200 个上下、500 MB 左右），
**不是完整游戏** —— 必须覆盖到同版本同来源的游戏上。
需要完整游戏做底时另行处理。

---

## 2. 三层 API，别搞混（这是最容易出错的地方）

| 层 | 入口 | 内容 |
|---|---|---|
| ① 本项目自定义 | `rpc_server._SIMPLE` | `applyProfile` / `applyModOptions` / `modOptions` / `reorderMods` / `createBnp` / `convertBnp` / `modAction` / `repairPriorities` / `shutdown` … |
| ② BCML 原生 `Api` | `rpc_server._call_api(method, params)` | `remerge` / `install_mod` / `uninstall_all` / `get_mods` / `export` … |
| ③ 直接 import bcml | `bcml.install` / `bcml.dev` / `bcml.util` | `install_mod` / `open_mod` / `refresh_merges` / `create_bnp_mod` … |

`_call_api` 只认 ②。拿 ① 的方法名去调它，会抛
`NoSuchMethodError(method)` —— **而这个异常的 `str()` 就是方法名本身**。
所以日志里只剩 `applyProfile` 三个字时，别以为是它内部出错，
而是「压根没找到这个方法」。

**统一的调用封装**（`tools/build_all_packs.py` 里的 `call()`）：

```python
import sys
sys.path.insert(0, "<仓库>/sidecar")
import rpc_server            # ← 导入即装上串行池，见 §5.5

def call(method: str, params=None):
    params = params or {}
    if method in rpc_server._SIMPLE:          # 先查自定义命名空间
        fn = rpc_server._SIMPLE[method]
        return fn(params) if fn else None
    result = rpc_server._call_api(method, params)   # 再回落 BCML Api
    if isinstance(result, dict):
        if result.get("error"):
            raise RuntimeError(f"{method}: {result['error']}")
        if "success" in result:
            return result.get("data")
    return result
```

**为什么同进程调用**：走 `stdin/stdout` 的 RPC 子进程会死锁 —— BCML 合并时
吐出海量进度输出，被包装成 `notify.log` 帧灌满管道，父子互等。
同进程直调没有这个问题。**若一定要起子进程，把 stdout 重定向到文件**，
不要用管道（管道不排空同样会卡住）。

---

## 3. 最小可用示例

```python
# -*- coding: utf-8 -*-
"""最小可用：按一份配置档案合并并把产物拷到 out/。"""
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(r"<仓库根>")
BCML = Path.home() / "AppData" / "Local" / "bcml"
OUT = Path(r"<产物目录>")

sys.path.insert(0, str(ROOT / "publish/win-x64/runtime/python39/Lib/site-packages"))
sys.path.insert(0, str(ROOT / "sidecar"))
import rpc_server          # 导入即装上串行池（必须，见 §5.5）


def call(method: str, params=None):
    """先查 _SIMPLE，再回落 BCML Api —— 两个命名空间别搞混（见 §2）。"""
    params = params or {}
    if method in rpc_server._SIMPLE:
        fn = rpc_server._SIMPLE[method]
        return fn(params) if fn else None
    r = rpc_server._call_api(method, params)
    if isinstance(r, dict):
        if r.get("error"):
            raise RuntimeError(f"{method}: {r['error']}")
        if "success" in r:
            return r.get("data")
    return r


def has_options(mod: Path) -> bool:
    """按**能力**判断该模组有没有可选变体，而不是维护一份名单（见 §5.10）。"""
    try:
        j = json.loads((mod / "info.json").read_text(encoding="utf-8"))
    except Exception:
        return False
    o = j.get("options") or {}
    return bool((o.get("single") or []) or (o.get("multi") or []))


def main() -> int:
    cfg_path = Path("配置.json")
    raw = cfg_path.read_text(encoding="utf-8")

    # ① 导入清单（只写记录：重排序号 / 写 .disabled / 原子写 options.json）
    call("applyProfile", {"profileJson": raw})

    # ② 对**有选项**的模组重选选项 —— 少了这步选项不会生效（见 §5.1）
    doc = json.loads(raw)
    for m in doc["mods"]:
        d = m.get("dir")
        if not d:
            continue
        mod = BCML / "mods_nx" / d
        if not mod.is_dir() or not has_options(mod):
            continue
        selects = (m.get("options") or {}).get("selects") or []
        call("applyModOptions", {"mod": str(mod), "selects": list(selects)})

    # ③ 合并：重建 9999_BCML，输出到 %LOCALAPPDATA%/bcml/merged_nx
    call("remerge", {"name": "all"})

    # ④ 归档（产物只含被改动过的文件，直接放目录、不压缩）
    merged = BCML / "merged_nx"
    for appid in ("01007EF00011E000", "01007EF00011F001"):
        src = merged / appid
        if not src.is_dir():
            continue
        dst = OUT / appid
        if dst.exists():
            shutil.rmtree(dst, ignore_errors=True)
        shutil.copytree(src, dst)
    return 0


if __name__ == "__main__":          # ← 这个守卫不是可选项，见 §5.6
    raise SystemExit(main())
```

---

## 4. 配置档案（`配置.json`）的形状

```jsonc
{
  "format": "bcml-winui3-modlist",
  "version": 1,
  "modCount": 19,
  "mods": [
    {
      "name": "林可儿 Mod 3.0（The Linkle Mod）",
      "dir": "0100_林可儿Mod3.0TheLinkleMod",   // ← 真实目录名，必须存在
      "priority": 100,
      "disabled": false,
      "options": {
        "disable": [],                            // 禁用的合并器
        "options": {},                            // 按合并器分组的选项字典
        "selects": ["Mannequin Fix", "Royal Weapons"]   // 本项目自加，BCML 不读
      }
    }
  ]
}
```

- `disabled: true` 的模组会被写 `.disabled` 标志（BCML 合并时跳过）。
- `options.selects` 是**本项目自己的字段**，只用于回显与驱动 `applyModOptions`；
  **BCML 从不读它**，别指望它单独生效。
- `dir` 必须是磁盘上真实存在的目录名（带当前编号）。

---

## 5. ★ 必须知道的坑（每条都会让你白跑几十分钟）

### 5.1 `applyProfile` 只写记录，不做任何重建

它只做三件事：重排序号、写 `.disabled`、原子写 `options.json`。
**不做**：重建 `options/`、撤销已注入本体的选项、重算 `logs/`。

所以「导入清单」之后**必须**对每个有选项的模组调一次 `applyModOptions`，
否则选项不会真正生效。

### 5.2 换游戏版本必须换进程

BCML 的 `util.get_settings` 把 `settings.json` 读进**函数属性缓存**
（`if not hasattr(get_settings, "settings")`），**一个进程内只读一次**。
同进程里改 `settings.json` 对已运行的部分无效。

正确做法：一个版本一个子进程，且**在 `import bcml` 之前**改写
`game_dir_nx` / `dlc_dir_nx`，再删掉 `mods_nx/9999_BCML`
（也可一并删 `merged_nx`）强制走完整重建。

> 反例：曾经三个版本「合并一次、复制三份」，7/7 配置产物 sha1 完全相同 ——
> 等于把 1.9.0 的内容发给 1.6.0。

**注意**：`1.8.2` 与 `1.9.0` 的游戏本体 `Pack/TitleBG.pack` 可能**逐字节相同**，
两版产物一致属正常。判断「版本有没有真正切换」要**对比本体**，不能对比产物。

### 5.3 `install_mod` 之前必须先 `rmtree`

BCML 自己的 `Api.update_mod` 是这么写的，照抄：

```python
rmtree(mod.path)                                   # ← 少了这句就静默失效
install.install_mod(archive, options=..., selects=...,
                    insert_priority=mod.priority, updated=True)
```

原因：`install_mod` 末尾是 `shutil.move(tmp_dir, mod_dir)`，而 **`shutil.move`
的目标已存在时，语义是「把 src 移进 dst 里面」而不是替换**。
少了 `rmtree` 就会在模组目录里堆出 `tmpXXXXXX/` 子目录、旧内容原封不动 ——
表现为「重选选项完全没生效」，而且越点越多 `tmp`。

### 5.4 源包按 **name 匹配**，不要信快照里的 `sourceArchive`

重装需要一个 `.bnp` 源包。用 `install.extract_mod_meta(bnp)["name"]`
与模组 `info.json` 的 `name` 做**精确比对**。

```python
import bcml.install as I
for bnp in sorted(alt_root.rglob("*.bnp")):
    if I.extract_mod_meta(bnp).get("name") == target_name:
        return bnp
```

> 实测：某模组快照里记录的 `sourceArchive` 是**非汉化**那份，
> 而实际装的是**汉化**那份（两者 `name` 与目录名都不同）。

### 5.5 必须装上「串行池」，否则会掀出进程风暴

BCML 的 `util.start_pool()` 是 `multiprocessing.Pool(min(63, cpu_count))`。
在 Windows 下 worker 会以**本脚本**为 `__mp_main__` 重新导入 —— 直接递归爆炸。
实测掀出 40+ 个 `python.exe` 卡死，得 `taskkill /F /IM python.exe /T` 收场。

**解法：`import rpc_server`**。它在模块层调 `_install_serial_pool()`，
把 `bcml.util.start_pool` 换成串行替身（`_SerialPool`，支持上下文管理器）。

### 5.6 加 `if __name__ == "__main__":` 守卫

同理，Windows 用 spawn 启动子进程时会重新导入主模块；没有守卫就会
把整个脚本再执行一遍。

### 5.7 `uninstall_mod` 的 `wait_merge` 是反直觉的

```python
uninstall_mod(mod, wait_merge=False)   # → 会调 refresh_merges()，触发一次完整合并
uninstall_mod(mod, wait_merge=True)    # → 不合并
```

做「装了又卸」的可逆测试时**必须传 `wait_merge=True`**，否则会白白触发
几分钟的合并。同理，只装不合并请直接调 `bcml.install.install_mod(..., merge_now=False)`，
不要走 `Api.install_mod`（它带 `@install.refresher` 会自动合并）。

### 5.8 撤销选项注入：SARC 要判到「**条目级**」

模组的选项状态是 `install_mod` **一次安装**的产物。若你在已装好的目录上
手工「删掉不再选中的文件」，SARC（`.pack` 等）必须按**条目**判断：

```
该撤的条目 = 未选变体提供的 − 已选变体提供的
```

- 按「整份文件」判断是错的：`TitleBG.pack` 只要有**任意一个**选中变体提供，
  就被判为要保留，条目级清理整段变成死代码 → 上一轮注入的条目永久残留。
- 减去「已选变体提供的」是必须的（同一条目可能被选中与未选中的变体共用）。
- 「任何变体都不提供」的条目属于模组基础内容，永不进 remove 集合，天然受保护。

> **更稳的做法**：干脆别手工改，直接走 §5.3 的「删掉重装」。

### 5.9 跨模组同名条目：合并规则是「**优先级高者胜**」

两个模组提供同名条目（同名文件里的同名 SARC 条目）时，**编号大的赢**。
典型案例：林可儿的洋伞与某动画包的伞是两套实现，都提供
`Model/Item_Parastole2.sbfres`；同时启用 → 模型与动作来自不同来源 → 动作错乱。

调优先级的方式是 `reorderMods`，或在配置文件里给定 `priority`。
排查时先列出「谁提供这个条目、优先级各是多少」，再定谁该赢。

### 5.10 别用**带编号的目录名**指代模组

目录名是 `<4位优先级>_<安全名>`，优先级会随拖动重排而变。
写死的目录名在重排后会**静默指向别的模组**。

- 按**安全名**匹配：`re.sub(r"^\d+_", "", dirname)`（重排号不变）。
- 或者按**能力**判断：比如「这个模组有没有选项」直接读 `info.json` 的
  `options.single` / `options.multi`，而不是维护一份名单。

> 实测：加进一个新模组后全体顺移一位，写死的 `CORE` 清单与
> `OPTION_MODS` 同时失效，一个导致启用清单错位、一个导致选项静默不更新。

### 5.11 命令行参数别把选项当成位置参数

`out = Path(sys.argv[1])` 这种写法遇到 `--pure-only` 会把配置写进一个
名叫 `--pure-only` 的文件夹。取位置参数要过滤：

```python
positionals = [a for a in sys.argv[1:] if not a.startswith("-")]
```

---

## 6. 现成工具（优先复用，别重写）

全部用内嵌运行时执行，例：
`publish\win-x64\runtime\python39\python.exe tools\<脚本>`

| 脚本 | 用途 | 典型命令 |
|---|---|---|
| `make_profiles_8.py` | 生成配置档案 | `... tools/make_profiles_8.py`（全部）/ `--pure-only`（只纯净包）/ `--dry-run` |
| `build_all_packs.py` | 按配置逐个合并并归档 | `... tools/build_all_packs.py [--only 1.9.0] [--pkg 纯净包] [--game-dir <romfs>] [--dry-run]` |
| `build_all_versions.py` | **逐版本起独立子进程**（换版本的正确姿势），跑完自动还原用户设置 | `... tools/build_all_versions.py [--only 1.6.0] [--pkg 纯净包]` |
| `make_linkle_arm_patch_bnp.py` | 从某个模组的选项里抽出内容，打成独立 `.bnp` | `... tools/make_linkle_arm_patch_bnp.py [输出目录]` |
| `verify_linkle_arm_patch_bnp.py` | 校验产物 BNP（结构 / 内容等价 / 可装可卸） | `... tools/verify_linkle_arm_patch_bnp.py` |
| `check_game_dump.py` | 核对一份游戏 romfs 与 `解包/<版本>` 是否同源 | `... tools/check_game_dump.py "<romfs 路径>"` |
| `test_sidecar.py` | sidecar 自检（改了 `rpc_server.py` 后必跑） | `... tools/test_sidecar.py` |

**改过 `sidecar/rpc_server.py` 后务必跑 `test_sidecar.py`** —— 它有 27 个用例，
含协议形状、重排守卫、陈旧路径解析等。

---

## 7. 怎么证明产物是对的（验收配方）

**① 先取本体基准，再判归属。** 没有基准会把正确产物误判成失败。

```text
# 游戏本体（注意三个版本的路径不同）
解包/1.6.0/1.6.0/01007EF00011E000/romfs                       （DLC: .../01007EF00011F001/romfs）
解包/1.8.2/The Legend of Zelda Breath of the Wild 1.8.2/APP+UPD/romfs   （DLC: .../DLC/romfs）
解包/1.9.0/1.9.0/01007EF00011E000/romfs                       （DLC: .../01007EF00011F001/romfs）
```

**② 按「内容」判定，不要按「条目名是否存在」。**

很多条目（如 `Model/Link.sbfres`）**本体里本来就有**，
所以「存在」证明不了模组生效。要判「补丁/模组是否真的覆盖了本体」：

```
产物值 == 模组源值  且  产物值 != 本体值
```

极个别条目会被 BCML 的**合并器重写**（如 `Actor/Pack/GameROMPlayer.sbactorpack`
会被 `actors` 合并器结构化重写），此时判据只能取「≠ 本体值」。

**③ 留意「同条目名换内容」。** 例如洋伞替换，条目集合可能**完全相同**，
差异只在同名条目的内容 —— 用「条目名集合」比对会误判为「没生效」。

**④ 版本类断言要用「可区分特征」。**
「产物 − 本体 = 合理增量」这种论证在两个版本本体本身接近时会失效
（1.8.2 与 1.9.0 的 `TitleBG.pack` 只差几十字节）。
要挑**版本间内容不同**的条目做指纹。

**⑤ 产物可复现性自检。** 清掉 `9999_BCML` + `merged_nx` 重跑一遍，
产物应逐字节完全一致（实测 10223 个文件 0 变化）。

---

## 8. 排错速查

| 症状 | 大概率原因 |
|---|---|
| 日志里只有方法名、没有别的信息 | `NoSuchMethodError` —— 调了 `_call_api` 但方法是 `_SIMPLE` 里的（§2/§5.1） |
| 「重选选项」完全没生效，目录里多了 `tmpXXXXXX/` | 少了 `rmtree`（§5.3） |
| 选项静默停在上次的选择 | 有选项的模组没收到 `applyModOptions`，或名单按编号写死失效了（§5.10） |
| 换版本后产物没变 | `get_settings` 进程内缓存 → 没换进程（§5.2） |
| 换版本后产物变了但游戏起不来 | 目标游戏与该版本解包不同源，或包被当成完整游戏用（§1） |
| 卡加载 / 报错 | `Bootup*.pack` 或 `ResourceSizeTable` 与游戏本体版本不匹配 |
| 动作错乱 / 走路头部乱转 | 跨模组同名条目被按优先级二选一，模型与动作来源不一致（§5.8/§5.9） |
| 掀出几十个 `python.exe` | 没装串行池；或没加 `__main__` 守卫（§5.5/§5.6） |
| 调用长时间不返回 | 走管道 RPC 且没排空（§2）；或 `uninstall_mod` 触发了合并（§5.7） |
| 配置档案里 `dir` 指向不存在的目录 | 模组被重排过号，配置是旧的（§5.10） |

---

## 9. 一条最短的生产路径

```bash
PY=publish/win-x64/runtime/python39/python.exe

# 1. 生成/刷新配置档案（按当前模组列表与所选选项）
"$PY" tools/make_profiles_8.py

# 2. 逐版本打包（每个版本一个独立进程；跑完自动还原 settings.json）
"$PY" tools/build_all_versions.py                 # 三个版本全跑
"$PY" tools/build_all_versions.py --only 1.9.0    # 只跑某个版本
"$PY" tools/build_all_versions.py --pkg 纯净包    # 只跑某个包型

# 3. 产物在 ~/Downloads/塞尔达传说 旷野之息/MOD整合包/<版本>/<包型>/<伞型>/<动作>/
```

单版本 8 轮约 2 分钟；三版本 24 轮约 7 分钟。
