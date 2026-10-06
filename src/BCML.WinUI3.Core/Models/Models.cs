using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BCML.WinUI3.Core.Services;

namespace BCML.WinUI3.Core.Models;

/// <summary>sidecar 自报家门的信息（sys.info 的返回）。</summary>
public sealed class SidecarInfo
{
    [JsonPropertyName("bcmlVersion")] public string? BcmlVersion { get; set; }
    [JsonPropertyName("bcmlOk")] public bool BcmlOk { get; set; }
    [JsonPropertyName("bcmlError")] public string? BcmlError { get; set; }
    [JsonPropertyName("python")] public string? Python { get; set; }
    [JsonPropertyName("executable")] public string? Executable { get; set; }
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("store_dir")] public string? StoreDir { get; set; }
    [JsonPropertyName("mods_dir")] public string? ModsDir { get; set; }
    [JsonPropertyName("merged_dir")] public string? MergedDir { get; set; }
    [JsonPropertyName("profiles_dir")] public string? ProfilesDir { get; set; }
    [JsonPropertyName("settings")] public JsonElement Settings { get; set; }

    public static SidecarInfo FromJson(JsonElement e) =>
        JsonSerializer.Deserialize<SidecarInfo>(e.GetRawText(), Json.Options) ?? new SidecarInfo();
}

/// <summary>对应 BCML 的 <c>BcmlMod.to_json()</c>。</summary>
public sealed class ModInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("disabled")] public bool Disabled { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }

    /// <summary>回传给后端时要还原成 to_json 的形状。</summary>
    public Dictionary<string, object?> ToJsonDict() => new()
    {
        ["name"] = Name,
        ["date"] = Date,
        ["priority"] = Priority,
        ["path"] = Path,
        ["disabled"] = Disabled,
        ["id"] = Id,
    };
}

/// <summary>对应 <c>api.get_mod_info</c> 的返回。</summary>
public sealed class ModDetail
{
    [JsonPropertyName("changes")] public List<string> Changes { get; set; } = new();
    [JsonPropertyName("desc")] public string? Description { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("processed")] public bool Processed { get; set; }
    /// <summary>base64 的缩略图（可能为空）。</summary>
    [JsonPropertyName("image")] public string? ImageBase64 { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }

    public static ModDetail FromJson(JsonElement e) =>
        JsonSerializer.Deserialize<ModDetail>(e.GetRawText(), Json.Options) ?? new ModDetail();
}

/// <summary>BCML 的「配置档案」。</summary>
public sealed class ProfileInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string? Path { get; set; }
}

/// <summary>BCML 的备份（对应 api.get_backups 的 {name, num, path}）。</summary>
public sealed class BackupInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>备份里包含的模组数量。</summary>
    [JsonPropertyName("num")] public int Num { get; set; }

    /// <summary>备份 .7z 的完整路径。</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }

    // ---- 下面两个由 RefreshFileInfo() 一次性填好 ----
    //
    // 原来这里有个 `Meta` 属性 getter，在里面做 File.Exists / FileInfo.Length /
    // GetLastWriteTime，还拼了中文字符串。问题有两个：
    //   · getter 里做 I/O —— XAML 绑定每次求值都可能打一次磁盘，且异常只能吞掉；
    //   · Core 层拼中文 —— 这一层没有本地化能力，英文界面下永远显示中文。
    // 现在改成：加载时算一次原始值，展示文案交给界面层拼。

    /// <summary>备份文件大小（字节）；文件读不到时为 null。</summary>
    public long? SizeBytes { get; private set; }

    /// <summary>备份文件最后修改时间；读不到时为 null。</summary>
    public DateTime? ModifiedAt { get; private set; }

    /// <summary>
    /// 读一次文件元信息。由 <c>BcmlService.GetBackupsAsync</c> 在后台线程上调用
    /// （不在属性 getter 里、也不在 UI 线程上做）。
    /// </summary>
    public void RefreshFileInfo()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Path))
            {
                SizeBytes = null;
                ModifiedAt = null;
                return;
            }
            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                SizeBytes = null;
                ModifiedAt = null;
                return;
            }
            SizeBytes = info.Length;
            ModifiedAt = info.LastWriteTime;
        }
        catch
        {
            // 文件被占用 / 权限不足都无所谓，界面少显示一项而已
            SizeBytes = null;
            ModifiedAt = null;
        }
    }

    /// <summary>把目录名里的下划线换成空格，纯展示用。</summary>
    public string DisplayName => Name.Replace('_', ' ');
}

/// <summary>可被合并器处理的「mod 选项」定义。</summary>
public sealed class ModOptionsMeta
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("desc")] public string? Description { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("options")] public JsonElement Options { get; set; }
}

// ---------------------------------------------------------------------------
// Mod 选项（可重选）
// ---------------------------------------------------------------------------

/// <summary>一个可选的选项变体（对应 mod 目录下 options/&lt;folder&gt;/）。</summary>
public sealed class ModOptionItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("desc")] public string Description { get; set; } = "";

    /// <summary>变体目录名 —— 提交给后端的就是这个值。</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";

    /// <summary>当前是否选中。</summary>
    [JsonPropertyName("selected")] public bool Selected { get; set; }

    /// <summary>
    /// 这个变体现在能不能被选中。
    ///
    /// 不是「磁盘上有没有这个目录」—— BCML 安装时会把没选中的变体删掉，
    /// 所以磁盘只剩当前这一套；只要快照（从原始 bnp 解出来的那份）里有它，
    /// 就可以重新选回来。真的两者都没有（快照降级过、又找不到原始 bnp）才会是 false。
    /// </summary>
    [JsonPropertyName("exists")] public bool Exists { get; set; }

    [JsonPropertyName("default")] public bool Default { get; set; }
}

/// <summary>「单选」组：同组内只能选一个。</summary>
public sealed class ModOptionGroup
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("desc")] public string Description { get; set; } = "";
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("options")] public List<ModOptionItem> Options { get; set; } = new();
}

/// <summary>某个 mod 的全部可选项与当前选择。</summary>
public sealed class ModOptionsInfo
{
    [JsonPropertyName("mod")] public string Mod { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("hasOptions")] public bool HasOptions { get; set; }

    /// <summary>可多选（复选框）。</summary>
    [JsonPropertyName("multi")] public List<ModOptionItem> Multi { get; set; } = new();

    /// <summary>单选组（每组一个单选按钮组）。</summary>
    [JsonPropertyName("single")] public List<ModOptionGroup> Single { get; set; } = new();

    /// <summary>当前已生效的变体目录名。</summary>
    [JsonPropertyName("selected")] public List<string> Selected { get; set; } = new();

    /// <summary>所有可用变体（含上一轮被取消、但快照里还留着的）。</summary>
    [JsonPropertyName("available")] public List<string> Available { get; set; } = new();

    /// <summary>
    /// info.json 里定义过、但既没在磁盘也没进快照的变体 —— 多半是原始 bnp 找不到了。
    /// 这些选项在对话框里要显示成"不可选"并说明原因，而不是干脆不显示
    /// （不显示的话用户会以为这个 mod 本来就没有那一项）。
    /// </summary>
    [JsonPropertyName("unavailable")] public List<string> Unavailable { get; set; } = new();
}

/// <summary>Compare Mods：单个合并器分组的差异。</summary>
public sealed class ModCompareGroup
{
    [JsonPropertyName("merger")] public string Merger { get; set; } = "";
    [JsonPropertyName("onlyLeft")] public List<string> OnlyLeft { get; set; } = new();
    [JsonPropertyName("onlyRight")] public List<string> OnlyRight { get; set; } = new();
    [JsonPropertyName("shared")] public List<string> Shared { get; set; } = new();

    public int DiffCount => OnlyLeft.Count + OnlyRight.Count;
    public bool HasDiff => DiffCount > 0;
    public string Summary => $"{Merger}  +{OnlyLeft.Count} / -{OnlyRight.Count}";
}

/// <summary>Compare Mods 的完整结果。</summary>
public sealed class ModCompareResult
{
    [JsonPropertyName("left")] public string? Left { get; set; }
    [JsonPropertyName("right")] public string? Right { get; set; }
    [JsonPropertyName("groups")] public List<ModCompareGroup> Groups { get; set; } = new();
    [JsonPropertyName("totalLeft")] public int TotalLeft { get; set; }
    [JsonPropertyName("totalRight")] public int TotalRight { get; set; }
}

/// <summary>单模组改动清单里的一个合并器分组。</summary>
public sealed class ModEditGroup
{
    [JsonPropertyName("merger")] public string Merger { get; set; } = "";
    [JsonPropertyName("name")] public string? MergerName { get; set; }
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();

    public string Header => $"{Merger}  ({Files.Count})";
}

/// <summary>单模组的合并器改动清单。</summary>
public sealed class ModEditsResult
{
    [JsonPropertyName("mod")] public string? Mod { get; set; }
    [JsonPropertyName("groups")] public List<ModEditGroup> Groups { get; set; } = new();

    public int Total => Groups.Sum(g => g.Files.Count);
}

/// <summary>
/// 模组详情页用的元数据 —— 对应 <c>sys.modDetails</c> 返回的 <c>meta</c>。
/// 只包含 <c>info.json</c> 里界面要展示的字段（<c>options</c> 那种大块定义不带过来）。
/// </summary>
public sealed class ModMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>switch / wiiu，可能为空（旧版 info.json 没写）。</summary>
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("depends")] public List<string> Depends { get; set; } = new();
    [JsonPropertyName("hasImage")] public bool HasImage { get; set; }
    [JsonPropertyName("disabled")] public bool Disabled { get; set; }
    [JsonPropertyName("processed")] public bool Processed { get; set; }
    /// <summary>该模组目录下实际存在的选项目录（安装时未勾选的会被 BCML 删掉）。</summary>
    [JsonPropertyName("optionFolders")] public List<string> OptionFolders { get; set; } = new();
    /// <summary>options.json 里被显式关闭的合并器名字。</summary>
    [JsonPropertyName("disabledMergers")] public List<string> DisabledMergers { get; set; } = new();
}

/// <summary>模组详情页的完整数据：元数据 + 改动清单（一次 RPC 拿全）。</summary>
public sealed class ModDetailsResult
{
    [JsonPropertyName("mod")] public string? Mod { get; set; }
    [JsonPropertyName("meta")] public ModMeta? Meta { get; set; }
    [JsonPropertyName("edits")] public List<ModEditGroup> Edits { get; set; } = new();
    [JsonPropertyName("total")] public int Total { get; set; }

    public static ModDetailsResult FromJson(JsonElement e) =>
        JsonSerializer.Deserialize<ModDetailsResult>(e.GetRawText(), Json.Options) ?? new ModDetailsResult();
}

/// <summary>BNP Creator 表单里的一个「选项」条目（复选/单选）。</summary>
public sealed class BnpOptionDraft
{
    private bool _multi = true;

    /// <summary>选项组名，对应 info.json 里 options.groups 的键。</summary>
    public string GroupName { get; set; } = "";

    /// <summary>true = 复选（可多选），false = 单选。</summary>
    public bool Multi
    {
        get => _multi;
        set
        {
            if (_multi == value) return;
            _multi = value;
            MultiIndex = value ? 0 : 1;
        }
    }

    /// <summary>绑定 ComboBox.SelectedIndex 用（0 复选 / 1 单选）。</summary>
    public int MultiIndex
    {
        get => _multi ? 0 : 1;
        set => _multi = value == 0;
    }

    // ComboBox 的两个候选项在 DataTemplate 里，无法从 code-behind 按名字取到，
    // 所以文案挂在模型上、用 x:Bind 绑过去，跟着界面语言走。
    public string MultiText => Loc.T("tools.optionMulti");
    public string SingleText => Loc.T("tools.optionSingle");

    /// <summary>精简编辑格式：<c>组名=值1,值2,值3</c>。</summary>
    public string Raw { get; set; } = "";

    /// <summary>把 <see cref="Raw"/> 拆成 (组名, 候选值列表)。</summary>
    public (string Group, List<string> Values) Parse()
    {
        var raw = (Raw ?? "").Trim();
        var eq = raw.IndexOf('=');
        if (eq < 0) return (raw, new List<string>());
        var group = raw[..eq].Trim();
        var values = raw[(eq + 1)..]
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        return (group, values);
    }
}



 /// <summary>全局 JSON 选项：宽松命名 + 容忍注释/尾逗号。</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
