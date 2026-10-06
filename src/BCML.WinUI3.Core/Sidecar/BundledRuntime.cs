using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BCML.WinUI3.Core.Models;
using BCML.WinUI3.Core.Services;

namespace BCML.WinUI3.Core.Sidecar;

/// <summary>内嵌运行时完整性校验的结论。</summary>
public enum RuntimeVerdict
{
    /// <summary>逐项对上，可以放心用。</summary>
    Ok,

    /// <summary>发布包里没有内嵌运行时（开发期 bin/ 就是这种情况），走系统 Python。</summary>
    NotPresent,

    /// <summary>运行时在，但没带完整性清单（老版本包）。能用，但要提示。</summary>
    ManifestMissing,

    /// <summary>清单在，但有文件对不上 —— 被改过、被截断，或者拷贝过程中损坏。</summary>
    Tampered,

    /// <summary>清单本身读不出来。</summary>
    Damaged,
}

/// <summary>一次完整性校验的结果。</summary>
public sealed class RuntimeVerification
{
    public RuntimeVerdict Verdict { get; init; }
    public int VerifiedCount { get; init; }
    public int TotalCount { get; init; }
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Modified { get; init; } = Array.Empty<string>();
    public string? Detail { get; init; }

    public bool IsFatal => Verdict is RuntimeVerdict.Tampered or RuntimeVerdict.Damaged;

    /// <summary>
    /// 给日志 / 界面用的一句话结论。
    ///
    /// 走 <see cref="Loc"/> 取词而不是在这里拼中文 —— Core 层没有 UI 依赖，但
    /// <c>Loc</c> 就在 Core 里且只管字符串表，用它才能保证英文界面下这句话也是英文。
    /// 之前这里硬拼中文，英文界面里这一行（例如「完整性校验通过（624/624 个文件）」）
    /// 会原样显示中文，用户看到的就是「本地化没做完」。
    /// </summary>
    public string Summary => Verdict switch
    {
        RuntimeVerdict.Ok => Loc.T("runtime.ok", VerifiedCount, TotalCount),
        RuntimeVerdict.NotPresent => Loc.T("runtime.notPresent"),
        RuntimeVerdict.ManifestMissing => Loc.T("runtime.manifestMissing"),
        RuntimeVerdict.Damaged => Loc.T("runtime.damaged", Detail ?? Loc.T("runtime.unparsable")),
        RuntimeVerdict.Tampered =>
            Loc.T("runtime.tampered", Missing.Count, Modified.Count)
            + (Missing.Count > 0 ? Loc.T("runtime.tamperedMissing", Missing[0]) : "")
            + (Modified.Count > 0 ? Loc.T("runtime.tamperedModified", Modified[0]) : ""),
        _ => Loc.T("runtime.unknown"),
    };
}

/// <summary>
/// 内嵌 Python 3.9 运行时的定位与完整性校验。
///
/// 为什么要做校验：发布包把整个 Python 解释器 + BCML 依赖（含原生扩展 `oead.pyd`）
/// 一起发出去，一旦其中任何文件被替换或被截断，表现都只是「后端起不来」这种含糊症状，
/// 用户根本分不清是安装损坏、杀软误删，还是自己环境有问题。
/// 有了 `runtime-manifest.json` 就能一句话说清是哪个文件不对。
/// </summary>
public static class BundledRuntime
{
    public const string RuntimeSubPath = "runtime/python39";
    public const string ManifestName = "runtime-manifest.json";

    /// <summary>
    /// 优先校验的关键文件：这几个一旦不对，Python 连启动都做不到，
    /// 先查它们可以尽早返回，不用白算 600 个文件的摘要。
    /// </summary>
    private static readonly string[] CriticalFiles =
    {
        "python.exe",
        "python39.dll",
        "python39.zip",
        "Lib/site-packages/oead.cp39-win_amd64.pyd",
    };

    /// <summary>随包内嵌运行时的目录；不存在返回 null。</summary>
    public static string? RuntimeDirectory(string? baseDir = null)
    {
        var b = baseDir ?? AppContext.BaseDirectory;
        var dir = Path.Combine(b, "runtime", "python39");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>随包内嵌运行时的 python.exe；不存在返回 null。</summary>
    public static string? PythonExecutable(string? baseDir = null)
    {
        var dir = RuntimeDirectory(baseDir);
        if (dir is null) return null;
        var exe = Path.Combine(dir, "python.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>按清单逐项校验运行时。异常一律收敛成结论，不往外抛。</summary>
    public static RuntimeVerification Verify(string runtimeDir)
    {
        if (!Directory.Exists(runtimeDir))
        {
            return new RuntimeVerification { Verdict = RuntimeVerdict.NotPresent };
        }

        var manifestPath = Path.Combine(runtimeDir, ManifestName);
        if (!File.Exists(manifestPath))
        {
            return new RuntimeVerification { Verdict = RuntimeVerdict.ManifestMissing };
        }

        ManifestDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ManifestDocument>(
                File.ReadAllText(manifestPath), Json.Options);
        }
        catch (Exception ex)
        {
            return new RuntimeVerification
            {
                Verdict = RuntimeVerdict.Damaged,
                Detail = ex.Message,
            };
        }

        if (doc?.Files is null || doc.Files.Count == 0)
        {
            return new RuntimeVerification
            {
                Verdict = RuntimeVerdict.Damaged,
                Detail = "清单里没有文件条目",
            };
        }

        var missing = new List<string>();
        var modified = new List<string>();

        // 第一步：关键文件先过一遍，坏了立刻收工
        foreach (var rel in CriticalFiles)
        {
            var problem = CheckOne(runtimeDir, rel, doc.Files.TryGetValue(rel, out var e) ? e : null);
            if (problem is not null)
            {
                return new RuntimeVerification
                {
                    Verdict = RuntimeVerdict.Tampered,
                    TotalCount = doc.Files.Count,
                    Missing = new[] { rel },
                    Modified = problem == "size" || problem == "hash" ? new[] { rel } : Array.Empty<string>(),
                    Detail = $"{rel} {problem}",
                };
            }
        }

        // 第二步：全量
        var verified = 0;
        foreach (var (rel, entry) in doc.Files)
        {
            var problem = CheckOne(runtimeDir, rel, entry);
            if (problem is null)
            {
                verified++;
            }
            else if (problem == "missing")
            {
                missing.Add(rel);
            }
            else
            {
                modified.Add(rel);
            }
        }

        return new RuntimeVerification
        {
            Verdict = missing.Count == 0 && modified.Count == 0
                ? RuntimeVerdict.Ok
                : RuntimeVerdict.Tampered,
            VerifiedCount = verified,
            TotalCount = doc.Files.Count,
            Missing = missing,
            Modified = modified,
        };
    }

    /// <summary>返回 null 表示正常；否则返回问题类型。</summary>
    private static string? CheckOne(string runtimeDir, string relative, ManifestEntry? entry)
    {
        if (entry is null) return "missing";     // 关键文件没在清单里 = 清单不全
        var full = Path.Combine(runtimeDir, relative.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(full);
        if (!info.Exists) return "missing";
        if (entry.Size > 0 && info.Length != entry.Size) return "size";
        if (string.IsNullOrEmpty(entry.Sha256)) return null;

        try
        {
            using var stream = File.OpenRead(full);
            using var sha = SHA256.Create();
            var actual = Convert.ToHexString(sha.ComputeHash(stream));
            return string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                ? null
                : "hash";
        }
        catch
        {
            // 读不了（被占用 / 权限）也算不正常，但不要因此崩掉
            return "unreadable";
        }
    }

    private sealed class ManifestDocument
    {
        [JsonPropertyName("pythonVersion")] public string? PythonVersion { get; set; }
        [JsonPropertyName("fileCount")] public int FileCount { get; set; }
        [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
        [JsonPropertyName("files")] public Dictionary<string, ManifestEntry>? Files { get; set; }
    }

    private sealed class ManifestEntry
    {
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    }
}
