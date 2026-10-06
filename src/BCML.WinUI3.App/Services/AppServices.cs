using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BCML.WinUI3.Core.Models;
using BCML.WinUI3.Core.Services;
using BCML.WinUI3.Core.Sidecar;

namespace BCML.WinUI3.App.Services;

/// <summary>
/// 应用级服务容器：管理 sidecar 生命周期 + 本地偏好 + 日志环形缓冲。
/// 刻意不引入 DI 容器 —— 这个规模下静态持有更省事、也更好排查。
/// </summary>
public sealed class AppServices
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly LinkedList<string> _log = new();
    private readonly object _logGate = new();
    private const int LogLimit = 2000;

    public SidecarClient? Client { get; private set; }
    public BcmlService? Bcml { get; private set; }
    public SidecarInfo? Info { get; private set; }
    public bool IsStarting { get; private set; }
    public bool IsReady => Bcml is not null && Info?.BcmlOk == true;
    public string? LastError { get; private set; }

    /// <summary>
    /// UI 调度队列。后端事件是在 RPC 读线程上触发的，任何触碰 XAML 的订阅者都必须回到 UI 线程，
    /// 否则会抛 COMException 0x8001010E(RPC_E_WRONG_THREAD)。所有事件统一经过 <see cref="Raise"/> 编组。
    /// </summary>
    public Microsoft.UI.Dispatching.DispatcherQueue? Ui { get; set; }

    private void Raise(Action action)
    {
        var q = Ui;
        if (q is null || q.HasThreadAccess) action();
        else q.TryEnqueue(() => action());
    }

    /// <summary>状态文本变化（用于状态栏）。</summary>
    public event EventHandler<string>? StatusChanged;
    /// <summary>新日志行。</summary>
    public event EventHandler<string>? LogAppended;
    /// <summary>后端就绪（或启动失败）。</summary>
    public event EventHandler? ReadyChanged;

    // ------------------------------------------------------------------ 本地偏好
    private static string PrefsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BCML-WinUI3", "app.json");

    public string? PythonPathOverride { get; private set; }

    /// <summary>界面主题：system / light / dark。</summary>
    public string Theme { get; private set; } = "system";

    /// <summary>界面语言（zh-Hans / en-US）。</summary>
    public string UiLanguage { get; private set; } = Loc.Zh;

    /// <summary>首次运行引导是否已完成（完成或跳过都算）。</summary>
    public bool WizardDone { get; private set; }

    /// <summary>后端诊断日志：排查"界面起来了但 sidecar 没拉起"时的第一手线索。</summary>
    public static string BackendLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BCML-WinUI3", "backend.log");

    private static readonly object DiagGate = new();

    public static void Diag(string msg)
    {
        lock (DiagGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BackendLogPath)!);
                File.AppendAllText(
                    BackendLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}" + Environment.NewLine);
            }
            catch { /* 忽略 */ }
        }
    }

    public AppServices()
    {
        LoadPrefs();
        Ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        SidecarClient.Diag += Diag;   // 把客户端内部每一步也接进来
    }

    private void LoadPrefs()
    {
        try
        {
            if (File.Exists(PrefsPath))
            {
                var node = JsonNode.Parse(File.ReadAllText(PrefsPath));
                PythonPathOverride = node?["pythonPath"]?.GetValue<string>();
                Theme = node?["theme"]?.GetValue<string>() ?? "system";
                UiLanguage = node?["uiLanguage"]?.GetValue<string>() ?? Loc.Zh;
                WizardDone = node?["wizardDone"]?.GetValue<bool>() ?? false;
            }
        }
        catch { /* 偏好损坏就退回默认值 */ }

        Loc.Language = UiLanguage;
    }

    private void SavePrefs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            var node = new JsonObject
            {
                ["pythonPath"] = PythonPathOverride,
                ["theme"] = Theme,
                ["uiLanguage"] = UiLanguage,
                ["wizardDone"] = WizardDone,
            };
            File.WriteAllText(PrefsPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 忽略 */ }
    }

    public void SavePythonPath(string? path)
    {
        PythonPathOverride = string.IsNullOrWhiteSpace(path) ? null : path;
        SavePrefs();
    }

    public void SetTheme(string theme)
    {
        Theme = theme;
        SavePrefs();
    }

    public void SetUiLanguage(string language)
    {
        UiLanguage = language;
        Loc.Language = language;
        SavePrefs();
    }

    /// <summary>标记首次运行引导已完成（完成或跳过），之后不再自动弹出。</summary>
    public void MarkWizardDone()
    {
        WizardDone = true;
        SavePrefs();
    }

    // ------------------------------------------------------------------ 日志
    public IReadOnlyList<string> Snapshot()
    {
        lock (_logGate) return new List<string>(_log);
    }

    private void Append(string line)
    {
        lock (_logGate)
        {
            _log.AddLast(line);
            while (_log.Count > LogLimit) _log.RemoveFirst();
        }
        Raise(() => LogAppended?.Invoke(this, line));
    }

    private void Status(string s)
    {
        Append("· " + s);
        Raise(() => StatusChanged?.Invoke(this, s));
    }

    // ------------------------------------------------------------------ 启动
    /// <summary>
    /// 校验随包内嵌运行时的完整性。
    ///
    /// 策略分档，避免「要么全放行、要么一刀切拒绝」：
    ///   · 没有内嵌运行时（开发期 bin/）→ 直接跳过，后面会回退到系统 Python；
    ///   · 有运行时但没带清单（老包）→ 只记警告 + 状态提示，仍然让它跑；
    ///   · 清单在但文件对不上 → **抛错**。被改过 / 被截断的原生扩展继续跑没有意义，
    ///     只会给出难以理解的失败，不如把"是哪个文件不对"直接说清楚。
    /// </summary>
    private void VerifyBundledRuntime()
    {
        var dir = BundledRuntime.RuntimeDirectory();
        if (dir is null) return;

        var verdict = BundledRuntime.Verify(dir);
        Diag($"运行时完整性：{verdict.Summary}");

        switch (verdict.Verdict)
        {
            case RuntimeVerdict.Ok:
                RuntimeIntegrityNote = verdict.Summary;
                break;

            case RuntimeVerdict.ManifestMissing:
                RuntimeIntegrityNote = verdict.Summary;
                Status(Loc.T("app.integrityManifestMissing"));
                break;

            case RuntimeVerdict.Tampered:
            case RuntimeVerdict.Damaged:
                RuntimeIntegrityNote = verdict.Summary;
                throw new InvalidOperationException(
                    Loc.T("app.integrityBlocked", verdict.Summary));
        }
    }

    /// <summary>最近一次运行时完整性校验的结论（供「设置」页展示）。</summary>
    public string? RuntimeIntegrityNote { get; private set; }

    /// <summary>定位随应用发布的 rpc_server.py。</summary>
    public static string ResolveServerScript()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "sidecar", "rpc_server.py"),
            Path.Combine(baseDir, "..", "..", "..", "..", "sidecar", "rpc_server.py"), // 开发期：从 bin 回到仓库根
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return Path.GetFullPath(candidates[0]);
    }

    /// <summary>
    /// 随包发布的内嵌 Python 3.9 运行时路径（绿色版）；没有则返回 null。
    /// 由 <c>tools/make_runtime.py</c> 组装进 <c>runtime\python39</c>。
    /// </summary>
    public static string? BundledPython => SidecarOptions.BundledRuntimePython();

    /// <summary>当前后端用的是不是随包的内嵌运行时（用于状态栏/设置页显示）。</summary>
    public bool UsingBundledPython =>
        Info?.Executable is { Length: > 0 } exe && BundledPython is { Length: > 0 } bundled &&
        string.Equals(Path.GetFullPath(exe), Path.GetFullPath(bundled), StringComparison.OrdinalIgnoreCase);

    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (IsReady) return true;

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady) return true;
            IsStarting = true;
            LastError = null;
            Status(Loc.T("app.backendStarting"));
            Diag("---- EnsureStartedAsync 开始 ----");
            Diag($"python 覆盖={PythonPathOverride ?? "<无>"} 脚本={ResolveServerScript()}");
            Diag($"内嵌运行时={BundledPython ?? "<未随包发布>"} 用户site=禁用");

            // 随包运行时先验完整性再启动。
            // 不校验的话，一个被替换/被截断的 oead.pyd 只会表现成"后端起不来"，
            // 用户无从判断是包坏了还是环境不对；这里直接把结论落到日志和错误信息里。
            VerifyBundledRuntime();

            var script = ResolveServerScript();
            if (!File.Exists(script))
            {
                throw new FileNotFoundException(
                    $"缺少 sidecar 脚本（{script}）。请确认发布目录里有 sidecar\\rpc_server.py。");
            }

            var client = await SidecarClient.StartAsync(new SidecarOptions
            {
                ServerScript = script,
                PythonPath = PythonPathOverride,
            }, ct).ConfigureAwait(false);

            client.Notification += (_, n) =>
            {
                if (n.Text is { Length: > 0 } t) Append(t);
                else if (n.Method == "notify.error") Append("!! " + n.Params);
            };
            client.StderrLine += (_, line) => Append(line);
            client.Faulted += (_, msg) => Status(Loc.T("app.backendError") + msg);

            Client = client;
            Bcml = new BcmlService(client);
            Info = await Bcml.InitializeAsync(ct).ConfigureAwait(false);

            if (Info.BcmlOk != true)
            {
                LastError = Info.BcmlError ?? Loc.T("app.bcmlImportFailed");
                Status(Loc.T("app.bcmlImportFailed") + LastError);
                Raise(() => ReadyChanged?.Invoke(this, EventArgs.Empty));
                return false;
            }

            Status($"后端就绪 · BCML {Info.BcmlVersion} · Python {Info.Python}");
            Raise(() => ReadyChanged?.Invoke(this, EventArgs.Empty));
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Diag("EnsureStartedAsync 失败: " + ex);
            Status(Loc.T("app.backendStartFailed") + ex.Message);
            Append(ex.ToString());
            Raise(() => ReadyChanged?.Invoke(this, EventArgs.Empty));
            return false;
        }
        finally
        {
            IsStarting = false;
            _startGate.Release();
        }
    }

    public async Task RestartAsync()
    {
        if (Client is not null)
        {
            Status(Loc.T("app.backendRestarting"));
            await Client.DisposeAsync().ConfigureAwait(false);
            Client = null;
            Bcml = null;
            Info = null;
        }
        await EnsureStartedAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Client is not null) await Client.DisposeAsync().ConfigureAwait(false);
    }
}
