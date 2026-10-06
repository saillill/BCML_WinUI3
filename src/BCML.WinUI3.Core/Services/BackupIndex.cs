using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BCML.WinUI3.Core.Models;

namespace BCML.WinUI3.Core.Services;

/// <summary>备份记录放在哪一类。</summary>
public enum BackupKind
{
    /// <summary>完整备份：整个 mods_nx 的 .7z（BCML 自己的备份，含全部 mod 文件与配置）。</summary>
    Full,

    /// <summary>导出的备份文件：几 KB 的 JSON 排序清单（只记录顺序/启用/合并器选项，不含 mod 文件）。</summary>
    List,
}

/// <summary>一条备份记录。</summary>
public sealed class BackupRecord
{
    [JsonPropertyName("kind")] public BackupKind Kind { get; set; }

    /// <summary>展示名（完整备份用 BCML 的目录名；导出清单用文件名）。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>文件绝对路径。</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>记录写入时间（本地时间）。</summary>
    [JsonPropertyName("recorded")] public DateTimeOffset Recorded { get; set; }

    /// <summary>建立时的模组数量（完整备份来自 BCML；清单来自导出时的列表）。</summary>
    [JsonPropertyName("mods")] public int ModCount { get; set; }

    /// <summary>由谁创建：bcml = BCML 扫描到的 .7z；app = 本应用导出时登记。</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "app";

    /// <summary>文件当前是否还在（列表展示时现算）。</summary>
    [JsonIgnore] public bool Exists => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);

    [JsonIgnore] public long SizeBytes => Exists ? new FileInfo(Path).Length : 0;

    [JsonIgnore] public string DisplayName => Name.Replace('_', ' ');
}

/// <summary>
/// 应用内部的备份记录台账。
///
/// 为什么要自己记一份：
///   · BCML 的完整备份只暴露「扫一遍目录拿到的 .7z」，删了/挪了就无从查起；
///   · 导出的 JSON 清单散落在用户挑的任意目录里，不记就再也找不回来，
///     自然也就做不到「集中列出、让用户选一个」。
///
/// 存一份 <c>%LOCALAPPDATA%\BCML-WinUI3\backup-index.json</c>，把两种备份都登记上。
/// 这是**台账**不是备份本身 —— 文件没了记录会自动标成缺失，用户可以删掉这条记录。
/// </summary>
public static class BackupIndex
{
    private static readonly object Gate = new();

    private static string FilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BCML-WinUI3", "backup-index.json");

    /// <summary>读取全部记录；文件坏了就返回空列表（不能因为台账坏了打不开备份页）。</summary>
    public static List<BackupRecord> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<BackupRecord>();
                var text = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<BackupRecord>>(text, Json.Options)
                       ?? new List<BackupRecord>();
            }
            catch
            {
                return new List<BackupRecord>();
            }
        }
    }

    private static void Save(List<BackupRecord> records)
    {
        lock (Gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp,
                    JsonSerializer.Serialize(records, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch
            {
                // 台账写不进去不该影响备份本身
            }
        }
    }

    /// <summary>登记一条记录（同路径覆盖）。</summary>
    public static void Upsert(BackupRecord record)
    {
        var list = Load();
        list.RemoveAll(r => string.Equals(r.Path, record.Path, StringComparison.OrdinalIgnoreCase));
        list.Add(record);
        list.Sort((a, b) => b.Recorded.CompareTo(a.Recorded));
        Save(list);
    }

    /// <summary>删除一条记录（不删文件本身 —— 那是 <c>DeleteBackupAsync</c> 的事）。</summary>
    public static void Remove(string path)
    {
        var list = Load();
        list.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        Save(list);
    }

    /// <summary>清掉文件已经不在了的记录，返回被清掉的数量。</summary>
    public static int PruneMissing()
    {
        var list = Load();
        var before = list.Count;
        list.RemoveAll(r => !r.Exists);
        if (list.Count != before) Save(list);
        return before - list.Count;
    }

    /// <summary>把 BCML 扫到的完整备份并进台账（用于补齐「不是本应用建的」那些）。</summary>
    public static void MergeFromBcml(IEnumerable<BackupInfo> backups)
    {
        var list = Load();
        foreach (var b in backups)
        {
            if (string.IsNullOrWhiteSpace(b.Path)) continue;
            if (list.Any(r => string.Equals(r.Path, b.Path, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new BackupRecord
            {
                Kind = BackupKind.Full,
                Name = b.Name,
                Path = b.Path!,
                Recorded = b.ModifiedAt is { } t
                    ? new DateTimeOffset(t)
                    : DateTimeOffset.Now,
                ModCount = b.Num,
                Source = "bcml",
            });
        }
        list.Sort((a, b) => b.Recorded.CompareTo(a.Recorded));
        Save(list);
    }

    /// <summary>按类型取记录。</summary>
    public static List<BackupRecord> OfKind(BackupKind kind) =>
        Load().Where(r => r.Kind == kind).ToList();
}
