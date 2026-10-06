using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BCML.WinUI3.Core.Models;
using BCML.WinUI3.Core.Services;

namespace BCML.WinUI3.Core.Sidecar;

/// <summary>sidecar 返回的业务错误（后端异常 / 参数错误等）。</summary>
public class SidecarException : Exception
{
    public int Code { get; }
    public string? Detail { get; }

    public SidecarException(int code, string message, string? detail = null) : base(message)
    {
        Code = code;
        Detail = detail;
    }
}

/// <summary>
/// 后端要求界面交互（文件选择框等）。WinUI 侧应捕获它并用原生对话框实现，
/// 而不是当成错误弹给用户。
/// </summary>
public sealed class NeedsUiException : SidecarException
{
    public NeedsUiException(string message) : base(-32010, message) { }
}

/// <summary>sidecar 主动推来的通知（日志、就绪、错误）。</summary>
public sealed class SidecarNotification
{
    public required string Method { get; init; }
    public required JsonElement Params { get; init; }

    public string? Text =>
        Params.ValueKind == JsonValueKind.Object &&
        Params.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
}

public sealed class SidecarOptions
{
    /// <summary>rpc_server.py 的绝对路径。</summary>
    public required string ServerScript { get; init; }

    /// <summary>Python 解释器；留空则自动探测（先找随包的内嵌运行时，再找本机 BCML 那套 3.9）。</summary>
    public string? PythonPath { get; init; }

    /// <summary>应用根目录，用来定位随包发布的内嵌运行时；默认 <c>AppContext.BaseDirectory</c>。</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>
    /// 随包发布的内嵌 Python 3.9 运行时（绿色版，由 tools/make_runtime.py 组装）。
    /// 存在时优先使用 —— 这样用户机器上没装 BCML 也能直接跑。
    ///
    /// 注意这里只看「在不在」；完整性校验在
    /// <see cref="BundledRuntime.Verify"/>，由 AppServices 在启动前调用。
    /// </summary>
    public static string? BundledRuntimePython(string? baseDir = null) =>
        BundledRuntime.PythonExecutable(baseDir);

    /// <summary>探测失败时使用的候选列表，按顺序尝试。</summary>
    public static IEnumerable<string> DefaultPythonCandidates(string? baseDir = null)
    {
        // 1) 随包的内嵌运行时最优先：它自带 bcml/oead，行为可预期
        var bundled = BundledRuntimePython(baseDir);
        if (bundled is not null) yield return bundled;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "Python", "Python39", "python.exe");
        yield return Path.Combine(local, "Programs", "Python", "Python39-32", "python.exe");
        yield return @"C:\Python39\python.exe";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python39", "python.exe");
    }
}

/// <summary>
/// BCML sidecar 进程的客户端：定长帧 JSON-RPC 2.0（4 字节小端长度 + UTF-8 JSON）。
/// </summary>
public sealed class SidecarClient : IAsyncDisposable
{
    /// <summary>诊断钩子：把启动链路的每一步落到文件，便于排查"窗口起来了但后端没拉起"。</summary>
    public static event Action<string>? Diag;

    private static void D(string msg) => Diag?.Invoke(msg);

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private readonly Task _stderrLoop;
    private int _nextId;

    public event EventHandler<SidecarNotification>? Notification;
    public event EventHandler<string>? StderrLine;
    public event EventHandler<string>? Faulted;

    public string PythonPath { get; }
    public SidecarInfo? Info { get; private set; }
    public bool IsRunning => !_process.HasExited;

    private SidecarClient(Process process, string pythonPath)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
        PythonPath = pythonPath;
        _readLoop = Task.Run(ReadLoopAsync);
        _stderrLoop = Task.Run(StderrLoopAsync);
    }

    // ------------------------------------------------------------------ 启动
    public static async Task<SidecarClient> StartAsync(SidecarOptions options, CancellationToken ct = default)
    {
        if (!File.Exists(options.ServerScript))
        {
            throw new FileNotFoundException(Loc.T("app.scriptMissing", options.ServerScript));
        }

        D("StartAsync: 脚本=" + options.ServerScript);
        var python = options.PythonPath;
        if (string.IsNullOrWhiteSpace(python))
        {
            python = await ProbePythonAsync(options.BaseDirectory, ct).ConfigureAwait(false);
        }
        D("StartAsync: 解释器=" + (python ?? "<未找到>"));

        if (python is null)
        {
            throw new InvalidOperationException(
                Loc.T("app.noPython") + "\n" + Loc.T("app.noPythonHint"));
        }

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = Quote(options.ServerScript),
            WorkingDirectory = Path.GetDirectoryName(options.ServerScript)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        // 让 Python 不要缓冲 stdout，避免帧被卡住
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        // 关键：禁止加载「用户 site-packages」（%APPDATA%\Python\PythonXX\site-packages）。
        // 否则内嵌运行时可能悄悄捞到本机 Python 装的第三方包，
        // 「绿色版自带依赖」这个前提就不成立了，出问题也难以复现。
        psi.Environment["PYTHONNOUSERSITE"] = "1";
        // 同理，别让工作目录或 PYTHONPATH 里的东西混进搜索路径
        psi.Environment.Remove("PYTHONPATH");
        psi.Environment.Remove("PYTHONSTARTUP");

        var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException(Loc.T("app.cannotStartProcess"));
        D($"StartAsync: 进程已启动 pid={proc.Id}");
        var client = new SidecarClient(proc, python);

        try
        {
            client.Info = await client.CallAsync("sys.info", null, TimeSpan.FromSeconds(60), ct)
                                .ConfigureAwait(false) is JsonElement e
                                ? SidecarInfo.FromJson(e)
                                : null;
        }
        catch (Exception ex)
        {
            D("StartAsync: sys.info 失败 -> " + ex);
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        D("StartAsync: 完成，BCML " + (client.Info?.BcmlVersion ?? "?")); 
        return client;
    }

    /// <summary>逐个候选解释器试 <c>import bcml</c>，选出第一个可用的。</summary>
    private static async Task<string?> ProbePythonAsync(string? baseDir, CancellationToken ct)
    {
        foreach (var candidate in SidecarOptions.DefaultPythonCandidates(baseDir))
        {
            D("  候选解释器: " + candidate + (File.Exists(candidate) ? " (存在)" : " (不存在)"));
            if (!File.Exists(candidate)) continue;
            if (await CanImportBcmlAsync(candidate, ct).ConfigureAwait(false)) return candidate;
        }

        // 退一步：用 py 启动器问 3.9 / 3.10
        foreach (var ver in new[] { "-3.9", "-3.10" })
        {
            var path = await RunCaptureAsync("py.exe", $"{ver} -c \"import sys;print(sys.executable)\"", ct)
                .ConfigureAwait(false);
            var exe = path?.Trim();
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe) &&
                await CanImportBcmlAsync(exe!, ct).ConfigureAwait(false))
            {
                return exe;
            }
        }
        return null;
    }

    private static async Task<bool> CanImportBcmlAsync(string python, CancellationToken ct)
    {
        var outp = await RunCaptureAsync(python, "-c \"import bcml,oead;print('ok')\"", ct).ConfigureAwait(false);
        D("  import 测试 " + python + " -> " + (outp?.Trim() ?? "<失败>"));
        return outp?.Trim() == "ok";
    }

    private static async Task<string?> RunCaptureAsync(string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // 探测时也关掉用户 site-packages，让判断条件和真正启动 sidecar 时一致
            psi.Environment["PYTHONNOUSERSITE"] = "1";
            psi.Environment.Remove("PYTHONPATH");
            using var p = Process.Start(psi);
            if (p is null) return null;
            var text = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    // ------------------------------------------------------------------ 调用
    public async Task<JsonElement> CallAsync(
        string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
        });

        var effective = timeout ?? TimeSpan.FromMinutes(10);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(effective);

        // try 必须把「发帧」也包进来：发送可能失败（管道断开、超时取消）。
        // 原来发送在 try 之外，一旦失败就跳过 finally，_pending 里那条 TCS 再也没人清理 ——
        // 每次失败漏一条，直到进程退出才随读循环的兜底清空。
        try
        {
            await SendFrameAsync(payload, ct).ConfigureAwait(false);

            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));
            var result = await tcs.Task.ConfigureAwait(false);
            if (result.TryGetProperty("error", out var err))
            {
                // 用 TryGetInt32：后端万一把 code 写成字符串，GetInt32 会直接抛
                // InvalidOperationException，把真实错误掩成一个"解析失败"。
                var code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var cv)
                    ? cv
                    : -32000;
                var msg = err.TryGetProperty("message", out var m)
                    ? m.GetString() ?? Loc.T("app.unknownError")
                    : Loc.T("app.unknownError");
                var detail = err.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() : null;
                if (code == -32010) throw new NeedsUiException(msg);
                throw new SidecarException(code, msg, detail);
            }
            return result.TryGetProperty("result", out var r) ? r : default;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>调用 BCML 的 Api 方法，并把 <c>{success,data}</c> / <c>{error}</c> 信封拆开。</summary>
    public async Task<JsonElement> CallApiAsync(
        string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var raw = await CallAsync("api." + method, parameters, timeout, ct).ConfigureAwait(false);

        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("error", out var err))
        {
            var shortMsg = err.TryGetProperty("short", out var s) ? s.GetString() ?? "操作失败" : "操作失败";
            var longMsg = err.TryGetProperty("error_text", out var t) ? t.GetString() : null;
            throw new SidecarException(-32000, shortMsg, longMsg);
        }
        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("data", out var data))
        {
            return data;
        }
        return raw;
    }

    // ------------------------------------------------------------------ 帧
    private async Task SendFrameAsync(string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        header[0] = (byte)(body.Length & 0xFF);
        header[1] = (byte)((body.Length >> 8) & 0xFF);
        header[2] = (byte)((body.Length >> 16) & 0xFF);
        header[3] = (byte)((body.Length >> 24) & 0xFF);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync(header, ct).ConfigureAwait(false);
            await _stdin.WriteAsync(body, ct).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadExactAsync(header, 4).ConfigureAwait(false)) break;
                var len = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
                if (len <= 0 || len > 64 * 1024 * 1024) break;

                var body = new byte[len];
                if (!await ReadExactAsync(body, len).ConfigureAwait(false)) break;

                Dispatch(Encoding.UTF8.GetString(body));
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, Loc.T("app.readOutputFailed") + ex.Message);
        }
        finally
        {
            // 进程没了，把所有挂起的调用都失败掉，避免界面永远转圈
            var msg = _process.HasExited
                ? Loc.T("app.processExited", TryExitCode())
                : Loc.T("app.connectionClosed");
            foreach (var kv in _pending)
            {
                kv.Value.TrySetException(new SidecarException(-32001, msg));
            }
            _pending.Clear();
        }
    }

    private int TryExitCode()
    {
        try { return _process.ExitCode; } catch { return -1; }
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await _stdout.ReadAsync(buffer.AsMemory(offset, count - offset), _cts.Token)
                                   .ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var clone = root.Clone();

            if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String)
            {
                var note = new SidecarNotification
                {
                    Method = methodEl.GetString()!,
                    Params = root.TryGetProperty("params", out var p) ? p.Clone() : default,
                };
                Notification?.Invoke(this, note);
                return;
            }

            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) &&
                _pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(clone);
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, Loc.T("app.parseFrameFailed") + ex.Message);
        }
    }

    private async Task StderrLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                StderrLine?.Invoke(this, line);
            }
        }
        catch
        {
            // 进程退出时正常抛出，忽略
        }
    }

    // ------------------------------------------------------------------ 释放
    private int _disposed;

    /// <summary>
    /// 停掉 sidecar。
    ///
    /// 释放顺序是有讲究的，不能随便调：
    ///   1. 先尽力发一次 <c>shutdown</c>，让 Python 侧把临时目录之类收干净；
    ///   2. 取消 <c>_cts</c> 让读循环退出；
    ///   3. **等两个循环真正结束**，再去 Dispose 进程 / CTS / 流 ——
    ///      读循环的收尾逻辑里还会访问 <c>_process.HasExited</c>、还会读 <c>_cts.Token</c>，
    ///      提前 Dispose 会撞上 ObjectDisposedException / 竞态。
    /// 幂等：重复调用直接返回（启动失败路径与 AppServices 都会调它）。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // 1) 尽力优雅关闭；失败无所谓，下面照样 Kill
        try
        {
            if (!_process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await CallAsync("shutdown", null, TimeSpan.FromSeconds(2), cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 忽略：下面直接 Kill
        }

        // 2) 让读循环退出
        try { _cts.Cancel(); } catch { /* 已释放过 */ }

        // 3) 进程先收掉
        try
        {
            if (!_process.WaitForExit(3000)) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 忽略
        }

        // 4) 等循环真正结束 —— 这一步必须在 Dispose 资源之前
        try
        {
            await Task.WhenAll(_readLoop, _stderrLoop).ConfigureAwait(false);
        }
        catch
        {
            // 循环内部已把异常转成事件，这里不会再抛
        }

        // 5) 现在可以安全释放了
        try { _stdin.Dispose(); } catch { }
        try { _stdout.Dispose(); } catch { }
        try { _process.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
    }
}
