using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BCML.WinUI3.App;

/// <summary>
/// 显式入口（<c>DISABLE_XAML_GENERATED_MAIN</c>）。
///
/// 自己持有 Main 有两个理由：
///   1) 单实例判定必须发生在「任何 XAML 对象存在之前」；
///   2) 非打包的 WinUI 3 应用一旦在启动阶段抛异常，会**没有任何对话框、没有任何控制台输出**地直接死掉。
///      所以入口处全部落盘到 <c>%LOCALAPPDATA%\BCML-WinUI3\startup.log</c>。
/// </summary>
public static class Program
{
    private static Mutex? _mutex;

    /// <summary>Application.Start 里创建的 App 实例，显式持有引用。</summary>
    private static App? AppInstance { get; set; }

    internal static EventWaitHandle? ShowRequestEvent { get; private set; }

    /// <summary>命令行参数。目前只用到 --page &lt;模组|设置|备份|日志&gt;，方便直接打开某一页。</summary>
    internal static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    internal const string MutexId = @"Local\BCML.WinUI3.SingleInstance";
    internal const string ShowRequestEventName = @"Local\BCML.WinUI3.ShowRequest";

    [STAThread]
    private static void Main(string[] args)
    {
        StartupArgs = args ?? Array.Empty<string>();
        try
        {
            _mutex = new Mutex(true, MutexId, out var createdNew);
            StartupLog.Step($"mutex acquired, createdNew={createdNew}");

            if (!createdNew)
            {
                if (EventWaitHandle.TryOpenExisting(ShowRequestEventName, out var handle))
                {
                    using (handle) handle.Set();
                    StartupLog.Step("second instance: signalled the running instance");
                    return;
                }
                StartupLog.Fail("second instance", new InvalidOperationException("无法唤起因实例"));
                return;
            }

            ShowRequestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName);
            StartupLog.Reset();

            StartupLog.Step("InitializeComWrappers");
            WinRT.ComWrappersSupport.InitializeComWrappers();

            StartupLog.Step("Application.Start");
            Application.Start(callbackParams =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                StartupLog.Step($"dispatcher queue acquired: {queue is not null}");
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                StartupLog.Step("constructing App");
                // 存到静态字段里持有引用。原来写的是 `_ = new App()` + `GC.KeepAlive(callbackParams)`：
                // 想要保活的对象写错了（KeepAlive 的那个是参数，不是 App），
                // 而且丢弃返回值后这个实例在语义上就没主了 —— 实际能跑只是因为
                // WinUI 内部自己持有它。写清楚意图，别让下一个人以为是靠 KeepAlive 撑住的。
                AppInstance = new App();
            });

            StartupLog.Step("Application.Start returned");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("Program.Main", ex);
        }
    }
}

/// <summary>启动期诊断日志。非打包 WinUI 静默崩溃时这是唯一的线索。</summary>
internal static class StartupLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BCML-WinUI3", "startup.log");

    public static void Reset()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"[{DateTime.Now:O}] pid={Environment.ProcessId} {Environment.Version}\n");
        }
        catch { /* 日志本身不允许影响启动 */ }
    }

    public static void Step(string msg)
    {
        Write($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }

    /// <summary>记录非致命异常：落盘但不弹窗、不终止。</summary>
    public static void Error(string where, Exception? ex)
    {
        Write($"[{DateTime.Now:HH:mm:ss.fff}] !! {where}: {ex}");
    }

    public static void Warn(string msg)
    {
        Write($"[{DateTime.Now:HH:mm:ss.fff}] ! {msg}");
    }

    public static void Fail(string where, Exception ex)
    {
        Write($"[{DateTime.Now:HH:mm:ss.fff}] !! {where}: {ex}");
        try
        {
            NativeMessageBox.Show(
                $"{ex.GetType().Name}: {ex.Message}\n\n详细堆栈见：\n{FilePath}",
                "BCML-WinUI3 · 启动失败", true);
        }
        catch { /* 连消息框都失败就只能靠日志了 */ }
    }

    private static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + "\n");
            }
            catch { /* 忽略 */ }
        }
    }
}

/// <summary>最朴素的 Win32 消息框：XAML 还不可用时也要能报错。</summary>
internal static class NativeMessageBox
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Show(string text, string caption, bool error = false)
    {
        const uint MB_OK = 0x0;
        const uint MB_ICONERROR = 0x10;
        const uint MB_ICONINFORMATION = 0x40;
        const uint MB_SETFOREGROUND = 0x10000;
        MessageBoxW(IntPtr.Zero, text, caption,
            MB_OK | (error ? MB_ICONERROR : MB_ICONINFORMATION) | MB_SETFOREGROUND);
    }
}
