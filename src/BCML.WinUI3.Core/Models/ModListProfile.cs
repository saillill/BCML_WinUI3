using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCML.WinUI3.Core.Models;

/// <summary>
/// 「模组清单」导入导出文件（对应 sidecar 的 <c>sys.exportProfile</c>）。
///
/// 它只记录清单层面的状态 —— 排序、启用/禁用、合并器选项、已启用的选项目录 ——
/// **不含任何 mod 文件**，因此一份通常只有几 KB，可以随手备份、发给别人。
/// </summary>
public sealed class ModListProfile
{
    public const string ExpectedFormat = "bcml-winui3-modlist";

    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("exportedAt")] public string? ExportedAt { get; set; }
    [JsonPropertyName("modCount")] public int ModCount { get; set; }
    [JsonPropertyName("mods")] public List<ModListEntry> Mods { get; set; } = new();

    public bool LooksValid => Format == ExpectedFormat && Mods.Count > 0;
}

public sealed class ModListEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>导出时的目录名。仅作参考 —— 目录名会随 priority 变，匹配一律按 name。</summary>
    [JsonPropertyName("dir")] public string? Dir { get; set; }

    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("disabled")] public bool Disabled { get; set; }

    /// <summary>该 mod 的 options.json 原样内容。</summary>
    [JsonPropertyName("options")] public JsonElement Options { get; set; }

    /// <summary>导出时该 mod 的 options/ 下还存在哪些选项目录。</summary>
    [JsonPropertyName("optionFolders")] public List<string> OptionFolders { get; set; } = new();
}

/// <summary>对应 sidecar 的 <c>sys.applyProfile</c> 返回。</summary>
public sealed class ApplyProfileResult
{
    [JsonPropertyName("applied")] public int Applied { get; set; }

    /// <summary>清单里有、本机没装的 mod（需要重装才能补齐）。</summary>
    [JsonPropertyName("missing")] public List<string> Missing { get; set; } = new();

    /// <summary>选项目录有差异的 mod —— 本地无法还原，只能重装。</summary>
    [JsonPropertyName("optionFolderChanges")] public List<OptionFolderChange> OptionFolderChanges { get; set; } = new();
}

public sealed class OptionFolderChange
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("want")] public List<string> Want { get; set; } = new();
    [JsonPropertyName("current")] public List<string> Current { get; set; } = new();
}
