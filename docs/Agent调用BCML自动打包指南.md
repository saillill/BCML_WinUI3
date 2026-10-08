# BCML 自动打包集成指南

面向需要在无人值守条件下驱动 BCML 完成模组合并与产物导出的开发者。

---

## 一、目标与适用范围

本文说明如何通过脚本或程序自动完成下列工作：

1. 指定启用哪些模组、以及每个模组的选项；
2. 触发 BCML 合并；
3. 导出合并结果，形成可分发的「整合包」。

适用范围为《塞尔达传说：旷野之息》（Switch / Wii U）的模组合并。
产物形如：

```text
<产物目录>/
├── 01007EF00011E000/            # 游戏本体
│   └── romfs/
└── 01007EF00011F001/            # DLC
    └── romfs/
```

**产物仅包含被模组改动过的文件**，不含完整游戏数据。
其使用方式是将文件覆盖到同版本、同来源的游戏之上，
详见 §6。

---

## 二、运行环境与目录约定

### 2.1 运行时

必须使用与 BCML 一同分发的嵌入式 Python 解释器。
独立安装的 Python 环境通常不含 `bcml` 包。

```text
publish/win-x64/runtime/python39/python.exe
```

调用方式：

```bash
PY="publish/win-x64/runtime/python39/python.exe"
"$PY" tools/build_all_packs.py
```

### 2.2 目录约定

| 用途 | 路径 |
|---|---|
| BCML 数据根目录 | `%LOCALAPPDATA%/bcml` |
| 已安装模组 | `%LOCALAPPDATA%/bcml/mods_nx/` |
| 主模组 | `%LOCALAPPDATA%/bcml/mods_nx/9999_BCML/` |
| 合并输出 | `%LOCALAPPDATA%/bcml/merged_nx/` |
| 运行设置 | `%LOCALAPPDATA%/bcml/settings.json` |

设置文件中与本文相关的两个键：

```jsonc
{
  "game_dir_nx": "…/01007EF00011E000/romfs",   // 游戏本体数据目录
  "dlc_dir_nx":  "…/01007EF00011F001/romfs"    // DLC 数据目录
}
```

`game_dir_nx` 必须指向包含 `Pack/Dungeon000.pack` 的 `romfs` 目录，
否则 BCML 在读取设置时即抛出异常。

---

## 三、核心概念

正确驱动 BCML 的前提是理解以下四个概念。

### 3.1 模组与优先级

每个已安装模组对应 `mods_nx` 下的一个目录，命名为：

```text
<4 位优先级>_<安全名>
```

例如：

```text
0100_林可儿Mod3.0TheLinkleMod
0101_非官方林可儿3.0补丁精简版手臂修正
0118_少女动作包脚步声修正版GirlyAnimationPack10.6Fixed
```

其中「安全名」由模组 `info.json` 的 `name` 字段经 `get_safe_pathname()`
转换得到（去掉空格与标点）。

**优先级决定合并顺序**：当两个模组提供同一文件或同一 SARC 条目时，
优先级高（编号大）的一方生效。

举例：模组 A（优先级 100）与模组 B（优先级 101）都提供
`TitleBG.pack` 中的 `Model/Link.sbfres`，则最终取 B 的内容。

**因此，指代一个模组时不应使用带编号的完整目录名**，
因为优先级调整后编号会随之变化。推荐两种稳定标识：

```python
import re
from pathlib import Path

# 方式一：去掉编号前缀，按安全名匹配
def stable_key(dirname: str) -> str:
    return re.sub(r"^\d+_", "", dirname)

# 方式二：按能力匹配（此处判断模组是否定义了可选变体）
import json
def defines_options(mod: Path) -> bool:
    info = json.loads((mod / "info.json").read_text(encoding="utf-8"))
    opts = info.get("options") or {}
    return bool((opts.get("single") or []) or (opts.get("multi") or []))
```

### 3.2 选项（可选变体）

部分模组在 `info.json` 中定义了可选项，形如：

```jsonc
{
  "options": {
    "single": [
      { "name": "少女行走 *", "folder": "Walk_Girly" },
      { "name": "林克默认行走", "folder": "Walk_Default" }
    ],
    "multi": []
  }
}
```

- `single` 为互斥组，每组只能选择一项；
- `folder` 是变体在磁盘上的目录名，也是选择时使用的标识符；
- `name` 是界面显示名，`*` 表示默认项。

**单选项生效的过程**：BCML 安装模组时，会把被选中变体的文件注入模组根目录；
同名 SARC 采用合并而非覆盖。因此选项状态同时体现在
`options/` 目录、`logs/` 与模组根目录三处，三者由同一次安装产生。

这带来两条约束：

1. 切换选项应通过**重新安装**完成，不应在已装好的目录上手工增删文件；
2. 多选项模组的重装依赖原始 `.bnp` 源包。

### 3.3 主模组与合并输出

- **主模组**（`mods_nx/9999_BCML`）是游戏本体的映射，由合并过程生成。
  每次执行合并前应先删除它，以强制完整重建。
- **合并输出**（`merged_nx`）是合并结果，即最终产物来源。

### 3.4 配置档案

配置档案是一份 JSON 文档，描述「启用哪些模组、各模组选择哪些选项」。

```jsonc
{
  "format": "bcml-winui3-modlist",
  "version": 1,
  "modCount": 2,
  "mods": [
    {
      "name": "林可儿 Mod 3.0（The Linkle Mod）",
      "dir": "0100_林可儿Mod3.0TheLinkleMod",
      "priority": 100,
      "disabled": false,
      "options": {
        "disable": [],
        "options": {},
        "selects": ["Mannequin Fix", "Royal Weapons"]
      }
    },
    {
      "name": "少女动作包・脚步声修正版",
      "dir": "0118_少女动作包脚步声修正版GirlyAnimationPack10.6Fixed",
      "priority": 118,
      "disabled": true,
      "options": { "disable": [], "options": {}, "selects": [] }
    }
  ]
}
```

字段说明：

| 字段 | 含义 |
|---|---|
| `dir` | 模组目录名，必须与磁盘上实际名称一致 |
| `priority` | 优先级，须与目录名前缀一致 |
| `disabled` | 为 `true` 时该模组不参与合并 |
| `options.disable` | 禁用的合并器列表（BCML 原生字段） |
| `options.options` | 按合并器分组的选项字典（BCML 原生字段） |
| `options.selects` | 本系统扩展字段，记录选择的变体，**BCML 本身不读取** |

`options.selects` 仅用于回显与驱动后续的选项应用，不会单独生效。

---

## 四、调用接口

### 4.1 命名空间划分

BCML 的自动化入口分为三层，调用时须区分：

| 层次 | 入口 | 内容 |
|---|---|---|
| 项目自定义方法 | `rpc_server._SIMPLE` | `applyProfile`、`applyModOptions`、`modOptions`、`reorderMods`、`createBnp`、`convertBnp`、`modAction`、`repairPriorities`、`shutdown` 等 |
| BCML 原生 API | `rpc_server._call_api(method, params)` | `remerge`、`install_mod`、`uninstall_all`、`export` 等 |
| BCML 内部模块 | `bcml.install`、`bcml.dev`、`bcml.util` | `install_mod`、`open_mod`、`refresh_merges`、`create_bnp_mod` 等 |

`_call_api` 只识别第二层的方法名。若传入第一层的方法名，
将抛出 `NoSuchMethodError(method)`。

该异常的字符串表示即方法名本身，日志中不会附带其他信息。
排查时若发现异常消息与方法名完全相同，应优先怀疑命名空间错误。

### 4.2 统一调用封装

建议以如下函数统一封装两类调用：

```python
import sys
from pathlib import Path

sys.path.insert(0, str(Path("<仓库根>") / "sidecar"))
import rpc_server


def call(method: str, params=None):
    params = params or {}
    if method in rpc_server._SIMPLE:
        fn = rpc_server._SIMPLE[method]
        return fn(params) if fn else None
    result = rpc_server._call_api(method, params)
    if isinstance(result, dict):
        if result.get("error"):
            raise RuntimeError(f"{method}: {result['error']}")
        if "success" in result:
            return result.get("data")
    return result
```

**调用应在同一进程内完成。** 若通过标准输入输出以子进程方式通信，
合并过程产生的大量进度输出会填满管道，导致父子进程互相等待。
如确需子进程，应将其标准输出重定向至文件而非管道。

### 4.3 主要方法

| 方法 | 层 | 参数 | 说明 |
|---|---|---|---|
| `applyProfile` | `_SIMPLE` | `{"profileJson": "<配置档案原文>"}` | 导入配置：重排序号、写 `.disabled`、写 `options.json` |
| `applyModOptions` | `_SIMPLE` | `{"mod": "<模组路径>", "selects": [...]}` | 按选择重新安装该模组；未传选择时不执行 |
| `modOptions` | `_SIMPLE` | `{"mod": "<模组路径>"}` | 读取该模组的选项定义与当前选择 |
| `reorderMods` | `_SIMPLE` | 见源码 | 调整优先级 |
| `remerge` | `_call_api` | `{"name": "all"}` | 执行合并，重建主模组与合并输出 |
| `createBnp` | `_SIMPLE` | 见源码 | 由目录或压缩包生成 `.bnp` |

---

## 五、标准打包流程

完整流程为四步。缺任一步都会导致结果与预期不符。

```text
① applyProfile   →  ② applyModOptions  →  ③ remerge  →  ④ 归档
```

### 5.1 导入配置

```python
raw = Path("配置.json").read_text(encoding="utf-8")
call("applyProfile", {"profileJson": raw})
```

本步**只写入记录**：重排序号、写 `.disabled` 标志、写 `options.json`。

它**不会**：重建 `options/` 目录、撤销已注入模组本体的选项内容、
重新生成 `logs/`。而合并读取的是模组根目录文件与 `logs/`，
因此仅执行本步不足以使选项生效，必须继续执行 5.2。

### 5.2 应用选项

对所有**定义了选项**的模组执行一次：

```python
import json

doc = json.loads(raw)
for m in doc["mods"]:
    mod = Path.home() / "AppData/Local/bcml/mods_nx" / m["dir"]
    if not mod.is_dir() or not defines_options(mod):
        continue
    selects = (m.get("options") or {}).get("selects") or []
    call("applyModOptions", {"mod": str(mod), "selects": list(selects)})
```

`applyModOptions` 的内部行为等价于下列操作，即 BCML 自身的更新流程：

```python
from shutil import rmtree
from bcml import install

rmtree(mod_path)                                   # 先删除旧目录
install.install_mod(
    source_bnp,                                    # 原始 .bnp 源包
    options=existing_options,                      # 保留原有合并器选项
    selects=new_selects,                           # 本次选择的变体
    insert_priority=current_priority,              # 保持原优先级
    updated=True,
)
```

**删除旧目录这一步不可省略。** `install_mod` 的落盘方式为
`shutil.move(临时目录, 目标目录)`，而当目标目录已存在时，
`shutil.move` 的行为是「移入其中」而非「替换」。
省略该步会在模组目录内产生 `tmpXXXXXX/` 子目录，旧内容保持不变，
表现为选项切换未生效。

**源包的定位方式**：以 `info.json` 的 `name` 字段为准，
在各候选目录中查找 `info.json` 名称相同的 `.bnp`。

```python
from bcml import install as I

def find_source_bnp(name: str, roots: list[Path]) -> Path | None:
    for root in roots:
        for bnp in sorted(root.rglob("*.bnp")):
            try:
                if I.extract_mod_meta(bnp).get("name") == name:
                    return bnp
            except Exception:
                continue
    return None
```

### 5.3 执行合并

```python
call("remerge", {"name": "all"})
```

本步会先删除主模组 `mods_nx/9999_BCML`，
再依据当前 `game_dir_nx` 与全部已启用模组重新生成合并输出。
输出的位置为 `%LOCALAPPDATA%/bcml/merged_nx`。

### 5.4 归档产物

```python
import shutil

merged = Path.home() / "AppData/Local/bcml/merged_nx"
for appid in ("01007EF00011E000", "01007EF00011F001"):
    src = merged / appid
    if not src.is_dir():
        continue
    dst = out_dir / appid
    if dst.exists():
        shutil.rmtree(dst, ignore_errors=True)
    shutil.copytree(src, dst)
```

产物以目录形式保存即可，无需压缩。

---

## 六、多游戏版本打包

### 6.1 设置只在进程内读取一次

BCML 的 `util.get_settings` 将设置文件读入函数属性缓存：

```python
def get_settings(name: str = ""):
    if not hasattr(get_settings, "settings"):
        ...
```

因此单个进程内设置只生效一次，**在同一进程内修改设置文件不会影响已运行的部分**。

### 6.2 按版本分进程执行

每个游戏版本应使用独立进程，并在**导入 `bcml` 之前**写入对应的数据目录。

```python
# 必须在 import bcml 之前执行
import json
from pathlib import Path

def switch_game_version(game_dir: Path, dlc_dir: Path) -> None:
    settings_path = Path.home() / "AppData/Local/bcml/settings.json"
    data = json.loads(settings_path.read_text(encoding="utf-8"))
    data["game_dir_nx"] = str(game_dir).replace("\\", "/")
    data["dlc_dir_nx"] = str(dlc_dir).replace("\\", "/")
    settings_path.write_text(
        json.dumps(data, ensure_ascii=False, indent=4), encoding="utf-8"
    )

switch_game_version(Path("…/解包/1.9.0/1.9.0/01007EF00011E000/romfs"),
                    Path("…/解包/1.9.0/1.9.0/01007EF00011F001/romfs"))

# 之后才导入 bcml 相关模块
import rpc_server
```

同时建议删除 `mods_nx/9999_BCML` 与 `merged_nx`，强制完整重建。

调度示例：

```bash
for ver in 1.6.0 1.8.2 1.9.0; do
  "$PY" tools/build_all_packs.py --game "$ver" --only "$ver"
done
```

### 6.3 判断版本是否真正切换

不同版本的产物**可能相同**，这不一定意味着切换失败。

例如 1.8.2 与 1.9.0 的游戏本体 `Pack/TitleBG.pack` 可能逐字节一致
（二者 sha1 相同、条目差异为 0），此时两版产物相同属正常结果。

判断切换是否生效，应比较**游戏本体**，而非产物。
比较时应选取版本之间存在差异的条目作为指纹。

---

## 七、注意事项

以下条目均对应具体的错误现象，列出以避免重复。

### 7.1 选项状态不可手工维护

选项的 `options/`、`logs/` 与模组根目录由同一次安装产生，三者需保持一致。

在已装好的模组上手工增删文件时，对 SARC 的判断必须细化到**条目**级：

```text
应移除的条目 = 未被选中变体提供的条目 − 被选中变体提供的条目
```

- 按整个文件判断是不正确的。只要任一被选中变体提供了某个 SARC，
  该文件就会整体跳过清理，其中的条目级清理逻辑不会执行；
- 需减去「被选中变体提供的条目」，因为同一条目可能同时被选中与
  未选中的变体提供；
- 所有变体都不提供的条目属于模组基础内容，不应进入移除集合。

更稳妥的做法是采用 §5.2 的重新安装方式。

### 7.2 处理跨模组同名条目

多个模组提供同一 SARC 条目时，最终结果由优先级决定。
若两方内容不同，可能出现模型与动作来自不同来源的情况。

处理方式：先列出该条目的全部提供者及其优先级，再确定应由哪一方生效。

```python
# 列出 TitleBG.pack 中某个条目的全部提供者
TARGET = "Model/Link.sbfres"
for mod in sorted(mods_root.iterdir()):
    pack = mod / "01007EF00011E000/romfs/Pack/TitleBG.pack"
    if not pack.is_file():
        continue
    entries = read_sarc_entries(pack)
    if TARGET in entries:
        print(mod.name, entries[TARGET][:12])
```

### 7.3 进程池配置

BCML 默认以 `multiprocessing.Pool` 启动进程池：

```python
def start_pool():
    return multiprocessing.Pool(processes=min(63, os.cpu_count()))
```

在 Windows 上，子进程会重新导入主模块。若主模块不含
`if __name__ == "__main__":` 守卫，将导致脚本被重复执行。

两种处理方式，建议同时采用：

```python
# 一、导入 sidecar，其模块级代码会把进程池替换为串行实现
import rpc_server

# 二、主模块使用守卫
if __name__ == "__main__":
    raise SystemExit(main())
```

### 7.4 卸载方法的参数语义

```python
install.uninstall_mod(mod, wait_merge=False)   # 会触发一次完整合并
install.uninstall_mod(mod, wait_merge=True)    # 不触发合并
```

进行「安装后卸载」的验证时，应传 `wait_merge=True`。

同理，仅安装而不合并应直接调用 `bcml.install.install_mod(..., merge_now=False)`，
而不经由 `Api.install_mod`，后者带有自动合并的装饰器。

### 7.5 命令行参数解析

将首个位置参数直接作为输出目录的写法，会在传入开关参数时出错：

```python
# 不正确：传入 --dry-run 时，输出目录会被解析为 "--dry-run"
out = Path(sys.argv[1])

# 正确：仅取非开关参数
positionals = [a for a in sys.argv[1:] if not a.startswith("-")]
out = Path(positionals[0]) if positionals else default_dir
```

### 7.6 产物与游戏的版本对应

产物只包含被改动过的文件，必须覆盖到**同版本、同来源**的游戏上。

即使版本号相同，不同来源或区域的游戏数据，
其 `Bootup*.pack` 与 `ResourceSizeTable.product.srsizetable` 也可能不同。
版本不匹配的典型现象为卡在加载界面或启动报错。

核对方式：比较游戏目录与本地解包目录中的若干高区分度文件。

```bash
"$PY" tools/check_game_dump.py "<游戏 romfs 路径>"
```

---

## 八、产物校验

校验的目的是确认产物确实反映了预期配置。

### 8.1 先取得本体基准

**没有基准无法判断归属。** 三个版本的本体路径分别为：

```text
解包/1.6.0/1.6.0/01007EF00011E000/romfs
解包/1.8.2/The Legend of Zelda Breath of the Wild 1.8.2/APP+UPD/romfs
解包/1.9.0/1.9.0/01007EF00011E000/romfs
```

### 8.2 按内容判断，而非按条目是否存在

许多条目在游戏本体中已经存在，因此「条目存在」不能作为模组生效的依据。

判断某个模组是否覆盖了本体，应同时满足两个条件：

```text
产物中的条目内容 == 模组提供的内容
产物中的条目内容 != 游戏本体中的内容
```

示例：

```python
base  = read_entry(base_titlebg, "Model/Link.sbfres")     # 本体
patch = read_entry(patch_titlebg, "Model/Link.sbfres")    # 模组
prod  = read_entry(prod_titlebg, "Model/Link.sbfres")     # 产物

assert prod == patch, "模组未覆盖本体"
assert prod != base,  "产物仍是本体内容，模组未生效"
```

### 8.3 条目名称相同时内容可能不同

部分替换（如模型替换）不会改变条目名称，仅改变同名条目的内容。
此时比较「条目名称集合」会得出「未生效」的错误结论。
应逐个比较条目内容。

### 8.4 合并器可能改写条目内容

少数条目会被 BCML 的合并器结构化重写，例如
`Actor/Pack/GameROMPlayer.sbactorpack` 会与游戏本体的 actor 包合并。
此类条目的内容不会等于模组源文件，校验判据应改为「与本体不同」。

### 8.5 可复现性校验

删除 `mods_nx/9999_BCML` 与 `merged_nx` 后重新执行合并，
产物应与上次逐字节一致。若不一致，说明存在未清理的残留状态。

---

## 九、工具与命令速查

以下脚本均以嵌入式运行时执行。

| 脚本 | 用途 | 示例 |
|---|---|---|
| `tools/make_profiles_8.py` | 生成配置档案 | `"$PY" tools/make_profiles_8.py`<br>`--pure-only` 仅生成部分包型<br>`--dry-run` 仅列出计划 |
| `tools/build_all_packs.py` | 按配置逐个合并并归档 | `"$PY" tools/build_all_packs.py --only 1.9.0`<br>`--pkg 纯净包` 限定包型<br>`--game-dir <romfs>` 指定游戏目录 |
| `tools/build_all_versions.py` | 逐版本以独立进程执行，完成后还原设置 | `"$PY" tools/build_all_versions.py`<br>`--only 1.6.0`<br>`--pkg 纯净包` |
| `tools/check_game_dump.py` | 核对游戏目录与解包目录是否同源 | `"$PY" tools/check_game_dump.py "<romfs>"` |
| `tools/make_linkle_arm_patch_bnp.py` | 从模组选项中提取内容，生成独立 `.bnp` | `"$PY" tools/make_linkle_arm_patch_bnp.py [输出目录]` |
| `tools/test_sidecar.py` | sidecar 自检 | 修改 `sidecar/rpc_server.py` 后应执行 |

典型完整流程：

```bash
PY="publish/win-x64/runtime/python39/python.exe"

# 1. 生成或刷新配置档案
"$PY" tools/make_profiles_8.py

# 2. 逐版本打包（各版本独立进程，结束后自动还原设置）
"$PY" tools/build_all_versions.py

# 3. 产物位于 ~/Downloads/塞尔达传说 旷野之息/MOD整合包/<版本>/<包型>/<伞型>/<动作>/
```

单版本 8 组约 2 分钟，三版本 24 组约 7 分钟。

---

## 十、附录：`.bnp` 文件格式

`.bnp` 是 7z 压缩包，根目录包含 `info.json` 与内容目录：

```text
<模组名>.bnp
├── info.json
├── 01007EF00011E000/romfs/…
├── 01007EF00011F001/romfs/…        （可选）
├── options/                        （可选，可选变体）
│   └── <变体名>/…
└── logs/                           （合并日志，可省略，安装时会重新生成）
```

生成应调用 BCML 提供的接口，而非手工压缩：

```python
from bcml import dev

dev.create_bnp_mod(
    mod=mod_dir,          # 模组目录，须含 01007EF00011E000/romfs
    output=out_bnp,
    meta={                # 至少包含 name 与 version
        "name": "模组名称",
        "version": "1.0.0",
        "desc": "说明",
        "depends": [],
    },
)
```

该接口负责写入 `info.json`（补全 `id` 与 `platform`）、打包 SARC、
生成 `logs/`，最后以 7z 封包。

其中 `id` 由 `base64(name==version)` 生成，用于唯一标识该模组。
