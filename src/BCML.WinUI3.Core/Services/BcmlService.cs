using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BCML.WinUI3.Core.Models;
using BCML.WinUI3.Core.Sidecar;

namespace BCML.WinUI3.Core.Services;

/// <summary>
/// 对 <see cref="SidecarClient"/> 的强类型封装：WinUI 只跟这里打交道，
/// 不直接碰 JSON-RPC 的细节。
/// </summary>
public sealed class BcmlService
{
    private readonly SidecarClient _client;

    /// <summary>合并类操作要跑几分钟，别用默认超时。</summary>
    public static readonly TimeSpan MergeTimeout = TimeSpan.FromMinutes(45);

    public BcmlService(SidecarClient client) => _client = client;

    public SidecarClient Client => _client;
    public SidecarInfo? Info { get; private set; }

    // 这里原来有个 `event EventHandler<string> LogLine { add => ...; remove { } }`：
    // add 每次往底层 client 挂一个匿名处理器，remove 是空的 —— 订阅方永远退不掉，
    // 而且全项目没有任何调用点。日志统一走 AppServices.LogAppended，直接删掉。

    // ------------------------------------------------------------------ 基础
    public async Task<SidecarInfo> InitializeAsync(CancellationToken ct = default)
    {
        var e = await _client.CallAsync("info", null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        Info = SidecarInfo.FromJson(e);
        return Info;
    }

    public async Task<IReadOnlyList<string>> GetLogsAsync(int tail = 300, CancellationToken ct = default)
    {
        var e = await _client.CallAsync("logs", new { tail }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("lines", out var lines))
        {
            return lines.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        }
        return Array.Empty<string>();
    }

    // ------------------------------------------------------------------ 模组
    public async Task<IReadOnlyList<ModInfo>> GetModsAsync(bool includeDisabled = true, CancellationToken ct = default)
    {
        var data = await _client.CallApiAsync("get_mods", new { disabled = includeDisabled }, ct: ct)
                               .ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<List<ModInfo>>(data.GetRawText(), Json.Options)
                   ?? new List<ModInfo>();
        return list.OrderBy(m => m.Priority).ToList();
    }

    public async Task<ModDetail> GetModInfoAsync(ModInfo mod, CancellationToken ct = default)
    {
        var data = await _client.CallApiAsync("get_mod_info", new { mod = mod.ToJsonDict() }, ct: ct)
                               .ConfigureAwait(false);
        return ModDetail.FromJson(data);
    }

    /// <summary>action: enable / disable / uninstall / reprocess</summary>
    /// <summary>
    /// 启用 / 禁用 / 卸载单个模组。
    ///
    /// 走 sys.modAction 而不是直接透传 BCML 的 api.mod_action：后者只认 path，
    /// 而 path 会因为排序重命名而过期（0100_xxx → 0116_xxx），
    /// 一旦界面还没刷新就会报 FileNotFoundError: ...\info.json。
    /// sys.modAction 会先按名字把 path 校正回来，并在真的找不到时给出可读提示。
    /// </summary>
    public async Task ModActionAsync(ModInfo mod, string action, CancellationToken ct = default)
    {
        await _client.CallAsync("sys.modAction",
                new { mod = mod.ToJsonDict(), action }, MergeTimeout, ct)
            .ConfigureAwait(false);
    }

    /// <summary>按给定顺序重排（priority 从 startPriority 连续编号），需要时一并执行安装。</summary>
    public async Task ReorderAsync(IReadOnlyList<ModInfo> orderedMods, int startPriority = 100,
                                   object[]? installs = null, CancellationToken ct = default)
    {
        var moves = orderedMods
            .Select((m, i) => new
            {
                mod = m.ToJsonDict(),
                priority = startPriority + i,
            })
            .ToArray();

        await _client.CallApiAsync("apply_queue",
                new { moves, installs = installs ?? Array.Empty<object>() }, MergeTimeout, ct)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 路径校验
    /// <summary>
    /// 校验某个目录是否符合 BCML 的要求。type 取值：
    /// game_dir / game_dir_nx（需含 Pack/Dungeon000.pack）、
    /// update_dir（Actor/Pack 下 sbactorpack 数 &gt; 7000）、
    /// dlc_dir / dlc_dir_nx（需含 Pack/AocMainField.pack）、
    /// cemu_dir（需含 ?emu*.exe）、export_dir / store_dir（只要求是目录）。
    /// </summary>
    public async Task<bool> DirExistsAsync(string folder, string type, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var data = await _client.CallApiAsync("dir_exists", new { folder, type }, TimeSpan.FromSeconds(30), ct)
                               .ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.True;
    }

    /// <summary>在所选目录及其上级里搜索特征文件，自动定位到正确的根目录。</summary>
    public async Task<string> DrillDirAsync(string folder, string type, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder)) return folder;
        var data = await _client.CallApiAsync("drill_dir", new { folder, type }, TimeSpan.FromMinutes(2), ct)
                               .ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.String ? data.GetString() ?? folder : folder;
    }

    public Task MakeShortcutAsync(bool desktop, CancellationToken ct = default) =>
        _client.CallApiAsync("make_shortcut", new { desktop }, TimeSpan.FromMinutes(2), ct);

    /// <summary>
    /// 为**本应用**创建快捷方式（桌面或开始菜单）。返回 (生成的 .lnk 路径, 是否真的落盘)。
    ///
    /// 与 <see cref="MakeShortcutAsync"/> 的区别：那个是 BCML 原版接口，建的是
    /// 「命令行版 BCML」（pythonw.exe + bcml 包）的快捷方式；这个建的是指向
    /// BCML-WinUI3.exe 的，双击直接进这个界面。
    /// </summary>
    public async Task<(string Path, bool Exists)> MakeAppShortcutAsync(
        string exePath, bool desktop, CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.makeAppShortcut",
                new { exe = exePath, desktop }, TimeSpan.FromMinutes(2), ct)
            .ConfigureAwait(false);

        var path = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("path", out var p)
            ? p.GetString() ?? ""
            : "";
        var exists = data.ValueKind == JsonValueKind.Object
                     && data.TryGetProperty("exists", out var e) && e.ValueKind == JsonValueKind.True;
        return (path, exists);
    }

    /// <summary>环境自检（Python 版本、位数、路径是否可用）。返回 null 表示通过，否则是错误说明。</summary>
    public async Task<string?> SanityCheckAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.CallApiAsync("sanity_check", null, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            return null;
        }
        catch (SidecarException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>卸载全部已安装模组（BCML 端会清空 mods_nx）。</summary>
    public Task UninstallAllAsync(CancellationToken ct = default) =>
        _client.CallApiAsync("uninstall_all", null, MergeTimeout, ct);

    /// <summary>
    /// 用一个新的 .bnp 替换已安装的模组，保留优先级 / 启用状态 / 合并器选项。
    /// 与原版 update_mod 一致：**不自动合并**，由调用方提示用户手动重新合并。
    /// </summary>
    public async Task<bool> UpdateModAsync(string modPath, string bnpPath, CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.updateMod", new { mod = modPath, bnp = bnpPath },
                                           TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object;
    }

    /// <summary>检查某个解释器能否 import bcml / oead（设置页的 Python 路径校验）。</summary>
    public async Task<(bool Ok, string Reason)> ProbePythonAsync(string path, CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.probePython", new { path }, TimeSpan.FromMinutes(3), ct)
                               .ConfigureAwait(false);
        var ok = data.ValueKind == JsonValueKind.Object
                 && data.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        var reason = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reason", out var r)
            ? (r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : r.ToString())
            : "";
        return (ok, reason);
    }

    // ------------------------------------------------------------------ 工具
    /// <summary>
    /// BNP 平台转换。原版 Api.convert_bnp 内部要弹保存框（无界面进程里不可用），
    /// 所以走 sidecar 的 sys.convertBnp，输出路径由界面先选好。
    /// </summary>
    public async Task<(string Output, long Size, List<string> Warnings)> ConvertBnpAsync(
        string bnp, string output, bool toWiiU, bool warnOnly, CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.convertBnp",
            new { mod = bnp, output, wiiu = toWiiU, warn = warnOnly ? "warn" : "fail" },
            TimeSpan.FromMinutes(90), ct).ConfigureAwait(false);

        var outPath = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("output", out var o)
            ? o.GetString() ?? output : output;
        var size = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("size", out var sz) &&
                   sz.TryGetInt64(out var sv) ? sv : 0;

        var warnings = new List<string>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("warnings", out var w) &&
            w.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in w.EnumerateArray())
            {
                if (x.GetString() is { Length: > 0 } t) warnings.Add(t);
            }
        }
        return (outPath, size, warnings);
    }

    /// <summary>合并输出目录的完整路径。</summary>
    public async Task<string?> GetMasterModpackAsync(CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.masterModpack", null, TimeSpan.FromSeconds(30), ct)
                               .ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("path", out var p)
            ? p.GetString()
            : null;
    }

    // ------------------------------------------------------------------ 工具：打包 / 升级 / RSTB / 独立包

    /// <summary>BNP Creator：把模组目录打包成 .bnp，元数据由界面表单提供。</summary>
    public Task<JsonElement> CreateBnpAsync(
        string folder, string output, Dictionary<string, object?> meta,
        Dictionary<string, object?>? selects = null,
        Dictionary<string, object?>? options = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>(meta) { ["folder"] = folder, ["output"] = output };
        if (selects is { Count: > 0 }) payload["selects"] = selects;
        if (options is { Count: > 0 }) payload["options"] = options;
        return _client.CallAsync("sys.createBnp", payload, TimeSpan.FromMinutes(60), ct);
    }

    /// <summary>升级旧版 BNP：解包 → 转换 rules.txt → 重新打包。</summary>
    public Task<JsonElement> UpgradeBnpAsync(string bnp, string output, CancellationToken ct = default) =>
        _client.CallAsync("sys.upgradeBnp", new { mod = bnp, output },
                          TimeSpan.FromMinutes(60), ct);

    /// <summary>为指定模组目录生成 RSTB（只跑 rstb 合并器）。</summary>
    public Task<JsonElement> GenRstbAsync(string mod, CancellationToken ct = default) =>
        _client.CallAsync("sys.genRstb", new { mod }, MergeTimeout, ct);

    /// <summary>BNP → 独立图形包 / Atmosphere 包（zip）。</summary>
    public Task<JsonElement> BnpToStandaloneAsync(string bnp, string output, CancellationToken ct = default) =>
        _client.CallAsync("sys.bnpToStandalone", new { mod = bnp, output },
                          TimeSpan.FromMinutes(90), ct);

    /// <summary>对比两个模组的合并器改动。</summary>
    public async Task<ModCompareResult> CompareModsAsync(string left, string right, CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.compareMods", new { left, right },
                                           MergeTimeout, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ModCompareResult>(data.GetRawText(), Json.Options)
               ?? new ModCompareResult();
    }

    /// <summary>单个模组的合并器改动清单（模组详情页）。</summary>
    public async Task<ModEditsResult> GetModEditsAsync(string mod, CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.modEdits", new { mod },
                                           TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ModEditsResult>(data.GetRawText(), Json.Options)
               ?? new ModEditsResult();
    }

    /// <summary>
    /// 模组详情页的一站式数据：info.json 元数据 + 选项目录 + 合并器改动清单。
    /// 合成一次 RPC 是为了让界面只有一个加载态（分三次会转三次圈）。
    /// <paramref name="includeEdits"/> 为 false 时跳过文件遍历，只取元数据（很快）。
    /// </summary>
    public async Task<ModDetailsResult> GetModDetailsAsync(
        string mod, bool includeEdits = true, CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.modDetails",
                new { mod, edits = includeEdits }, TimeSpan.FromMinutes(10), ct)
            .ConfigureAwait(false);
        return ModDetailsResult.FromJson(data);
    }

    // ------------------------------------------------------------------ 清单导入导出
    /// <summary>导出当前「模组清单」（排序 / 启用状态 / 合并器选项 / 选项目录），不含 mod 文件。</summary>
    public async Task<string> ExportModListAsync(CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.exportProfile", null, TimeSpan.FromMinutes(2), ct)
                               .ConfigureAwait(false);
        return data.GetRawText();
    }

    /// <summary>按导入的清单恢复排序 / 启用状态 / 合并器选项，并返回差异报告。</summary>
    public async Task<ApplyProfileResult> ApplyModListAsync(string json, CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.applyProfile", new { profileJson = json }, MergeTimeout, ct)
                               .ConfigureAwait(false);
        return JsonSerializer.Deserialize<ApplyProfileResult>(data.GetRawText(), Json.Options)
               ?? new ApplyProfileResult();
    }

    /// <summary>
    /// 只重写优先级与目录名，**不触发合并** —— 拖动 / 排序用这个。
    ///
    /// 返回**按新顺序排列的目录名**。调用方拿到后可以直接把这些新名字套回内存里的行，
    /// 不必再跑一次完整列表读取 —— 「只是排个序却要转半天」的根子就是那次重读
    /// （17 个模组 + 封面 + 详情全部重来一遍）。
    /// </summary>
    public async Task<IReadOnlyList<string>> ReorderLocalAsync(
        IReadOnlyList<string> order, int startPriority = 100, CancellationToken ct = default)
    {
        var data = await _client.CallAsync("sys.reorderMods",
                new { order, start = startPriority }, TimeSpan.FromMinutes(5), ct)
            .ConfigureAwait(false);

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("paths", out var paths) &&
            paths.ValueKind == JsonValueKind.Array)
        {
            return paths.EnumerateArray()
                        .Select(x => x.GetString() ?? "")
                        .Where(s => s.Length > 0)
                        .ToList();
        }
        return Array.Empty<string>();
    }

    // ------------------------------------------------------------------ 选项
    /// <summary>列出某个已安装 mod 的选项目录（目录名即 BCML 的 selects 取值）。</summary>
    public async Task<IReadOnlyList<string>> GetOptionFoldersAsync(string modDirectory, CancellationToken ct = default)
    {
        var data = await _client.CallApiAsync("get_option_folders", new { mod = modDirectory }, ct: ct)
                               .ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return data.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
    }

    // ------------------------------------------------------------------ 配置档案
    public async Task<IReadOnlyList<ProfileInfo>> GetProfilesAsync(CancellationToken ct = default)
    {
        var data = await _client.CallApiAsync("get_profiles", null, ct: ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<ProfileInfo>>(data.GetRawText(), Json.Options)
               ?? new List<ProfileInfo>();
    }

    public Task SaveProfileAsync(string name, CancellationToken ct = default) =>
        _client.CallApiAsync("save_profile", new { profile = new { name } }, MergeTimeout, ct);

    public Task SetProfileAsync(string name, CancellationToken ct = default) =>
        _client.CallApiAsync("set_profile", new { profile = new { name } }, MergeTimeout, ct);

    public Task DeleteProfileAsync(string name, CancellationToken ct = default) =>
        _client.CallApiAsync("delete_profile", new { profile = new { name } }, ct: ct);

    public async Task<ProfileInfo?> GetCurrentProfileAsync(CancellationToken ct = default)
    {
        var data = await _client.CallApiAsync("get_current_profile", null, ct: ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object) return null;
        try { return JsonSerializer.Deserialize<ProfileInfo>(data.GetRawText(), Json.Options); }
        catch { return null; }
    }

    // ------------------------------------------------------------------ 备份 / 合并
    public async Task<IReadOnlyList<BackupInfo>> GetBackupsAsync(CancellationToken ct = default)
    {
        var data = await _client.CallApiAsync("get_backups", null, ct: ct).ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<List<BackupInfo>>(data.GetRawText(), Json.Options)
                   ?? new List<BackupInfo>();
        // 大小/时间在后台线程上算一次。放在属性 getter 里的话，XAML 每求值一次就打一次磁盘。
        foreach (var b in list) b.RefreshFileInfo();
        return list;
    }

    /// <summary>name 传 "all" 走完整重新合并；也可以是某个合并器的 friendly name。</summary>
    // ------------------------------------------------------------------ 备份
    /// <summary>新建备份。name 留空时 BCML 会用 BCML_Backup_年-月-日。</summary>
    public Task CreateBackupAsync(string name, CancellationToken ct = default) =>
        _client.CallApiAsync("create_backup", new { backup = name }, MergeTimeout, ct);

    /// <summary>用备份替换当前全部已装模组（BCML 端会先清空再解压，然后重新合并）。</summary>
    public Task RestoreBackupAsync(string backupPath, CancellationToken ct = default) =>
        _client.CallApiAsync("restore_backup", new { backup = backupPath }, MergeTimeout, ct);

    /// <summary>删除备份文件。</summary>
    public Task DeleteBackupAsync(string backupPath, CancellationToken ct = default) =>
        _client.CallApiAsync("delete_backup", new { backup = backupPath }, TimeSpan.FromMinutes(2), ct);

    public Task RemergeAsync(string name = "all", CancellationToken ct = default) =>
        _client.CallApiAsync("remerge", new { name }, MergeTimeout, ct);

    /// <summary>
    /// 把 mods_nx 下所有模组的优先级重新连续编号（0100 起）。
    ///
    /// 修的是「编号撞号」这种损坏：早期版本的排序只校验完整目录名是否重复，
    /// 没校验 `NNNN_` 前缀这个真正的优先级数字，于是可能出现两个 `0100_`、
    /// 同时某个号段消失 —— 之后界面拿着的路径就会失效。
    /// 对没坏的情况是幂等的（顺序按现有编号稳定排序）。
    /// </summary>
    public async Task<int> RepairPrioritiesAsync(CancellationToken ct = default)
    {
        var e = await _client.CallAsync("sys.repairPriorities", null, MergeTimeout, ct)
                              .ConfigureAwait(false);
        return e.TryGetProperty("renamed", out var n) && n.TryGetInt32(out var v) ? v : 0;
    }

    // ------------------------------------------------------------------ Mod 选项
    /// <summary>读某个已安装 mod 的可选项定义与当前选择。</summary>
    /// <param name="snapshot">
    /// 需要把「上一轮取消掉的变体」也列出来时传 true。会建立/校验 options/ 的原始快照，
    /// 比只读当前状态重得多，所以详情面板的可用性探测用 false。
    /// </param>
    public async Task<ModOptionsInfo?> GetModOptionsAsync(
        string mod, bool snapshot = false, CancellationToken ct = default)
    {
        var e = await _client.CallAsync("sys.modOptions", new { mod, snapshot }, ct: ct)
                              .ConfigureAwait(false);
        return JsonSerializer.Deserialize<ModOptionsInfo>(e.GetRawText(), Json.Options);
    }

    /// <summary>
    /// 按新的选择重建 mod/options/ 并写回 options.json。
    /// 只改文件布局 —— 让改动真正生效还需要 <see cref="RemergeAsync"/>。
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyModOptionsAsync(
        string mod, IEnumerable<string> selects, CancellationToken ct = default)
    {
        var e = await _client.CallAsync("sys.applyModOptions",
            new { mod, selects = selects.ToArray() }, MergeTimeout, ct).ConfigureAwait(false);
        if (e.TryGetProperty("applied", out var applied) &&
            applied.ValueKind == JsonValueKind.Array)
        {
            return applied.EnumerateArray()
                          .Select(x => x.GetString() ?? "")
                          .Where(s => s.Length > 0)
                          .ToList();
        }
        return Array.Empty<string>();
    }

    /// <summary>可选的合并器名字（用于「重新合并」下拉）。</summary>
    public async Task<(bool HasCemu, List<string> Mergers)> GetSetupAsync(CancellationToken ct = default)
    {
        var data = await _client.CallApiAsync("get_setup", null, ct: ct).ConfigureAwait(false);
        var mergers = new List<string>();
        var hasCemu = false;
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("hasCemu", out var hc)) hasCemu = hc.ValueKind == JsonValueKind.True;
            if (data.TryGetProperty("mergers", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in m.EnumerateArray())
                {
                    if (x.GetString() is { } s) mergers.Add(s);
                }
            }
        }
        return (hasCemu, mergers);
    }

    // ------------------------------------------------------------------ 设置
    public async Task<Dictionary<string, JsonElement>> GetSettingsAsync(CancellationToken ct = default)
    {
        var e = await _client.CallAsync("api.get_settings", new { }, ct: ct).ConfigureAwait(false);
        // get_settings 没被 @win_or_lose 包，sidecar 会套一层 {success,data}
        var payload = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("data", out var d) ? d : e;
        var dict = new Dictionary<string, JsonElement>();
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in payload.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        }
        return dict;
    }

    /// <summary>
    /// 写回设置。
    ///
    /// 这里集中剔除已下线的键 —— 目前是 <c>nsfw</c>。
    /// 上游 BCML 的默认值表里声明了这个键（<c>bcml/util.py</c>），读设置时会带回来，
    /// 如果我们不在写回前删掉，它就会被原样再存一次，永远赖在 settings.json 里。
    /// 它在本应用里没有任何消费者（没有在线搜索/安装界面），所以直接下线：
    /// 界面上不再出现，写回时也不再落盘。
    /// </summary>
    public Task SaveSettingsAsync(Dictionary<string, object?> settings, CancellationToken ct = default)
    {
        settings.Remove("nsfw");
        return _client.CallApiAsync("save_settings", new { settings }, TimeSpan.FromMinutes(5), ct);
    }

    public async Task<IReadOnlyList<string>> GetUserLanguagesAsync(string gameDir, CancellationToken ct = default)
    {
        var data = await _client.CallApiAsync("get_user_langs", new { dir = gameDir }, ct: ct)
                               .ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return data.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
    }

    public async Task<string?> GetVersionInfoAsync(CancellationToken ct = default)
    {
        var data = await _client.CallApiAsync("get_ver", null, ct: ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("version", out var v)
            ? v.GetString()
            : null;
    }
}
