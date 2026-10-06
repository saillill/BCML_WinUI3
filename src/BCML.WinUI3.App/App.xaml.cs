using System;
using System.Threading;
using System.Threading.Tasks;
using BCML.WinUI3.App.Services;
using Microsoft.UI.Xaml;

namespace BCML.WinUI3.App;

public partial class App : Application
{
    private MainWindow? _window;
    private EventWaitHandle? _showRequest;
    private System.Threading.Thread? _showWatcher;

    internal AppServices? Services { get; private set; }
    internal static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        StartupLog.Step("App ctor: InitializeComponent");
        InitializeComponent();
        StartupLog.Step("App ctor: ok");

        // 非打包的 WinUI 3 应用里，事件处理器抛异常会**静默退进程**（没有对话框、没有控制台输出），
        // 用户看到的就是「点一下就闪退」。这三处兜底把异常落盘 + 弹框，并尽量不让进程直接死。
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupLog.Error("AppDomain", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            StartupLog.Error("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    private static void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupLog.Error("XamlUnhandled", e.Exception);
        e.Handled = true;   // 尽量保住进程，让用户还能操作别的页面
        try
        {
            NativeMessageBox.Show(
                $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n详细堆栈见：\n{StartupLog.FilePath}",
                "BCML-WinUI3 · 出错了", true);
        }
        catch { /* 弹框都失败就只能靠日志 */ }
    }

    /// <summary>系统当前是否使用深色主题。</summary>
    internal static bool SystemUsesDark()
    {
        try
        {
            var bg = new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return bg.R < 128;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            StartupLog.Step("OnLaunched: begin");
            Services = new AppServices();
            _window = new MainWindow(Services, Program.StartupArgs);
            MainWindowInstance = _window;
            StartupLog.Step("OnLaunched: MainWindow constructed");
            _window.Activate();

            WireShowRequest();
            StartupLog.Step("OnLaunched: done");

            // 关窗时优雅停掉 sidecar。
            // 不做这一步也能跑（父进程退出后管道关闭，sidecar 读到 EOF 会自己退），
            // 但那是「硬杀」：正在跑的合并会被从中间掐断，可能留下写了一半的
            // mods_nx / 临时目录。这里先发 shutdown 让 Python 侧自己收尾。
            _window.Closed += async (_, _) =>
            {
                try
                {
                    StartupLog.Step("窗口已关闭，正在停止后端");
                    if (Services is not null) await Services.DisposeAsync();
                    StartupLog.Step("后端已停止");
                }
                catch (Exception ex)
                {
                    StartupLog.Error("关闭时停止后端失败", ex);
                }
            };

            // 界面先出来，后端在后台慢慢起 —— sidecar 首次 import bcml 要好几秒。
            _ = _window.InitializeBackendAsync();

            // 首次运行（或未完成过向导）时展示引导。
            _window.ShowWizardIfNeeded(Program.StartupArgs);
            StartupLog.Step("OnLaunched: wizard checked");
        }
        catch (Exception ex)
        {
            StartupLog.Fail("OnLaunched", ex);
            throw;
        }
    }

    /// <summary>
    /// 第二个实例启动时会把事件置位，这里把已有窗口唤到前台。
    ///
    /// ⚠ 必须用**在 UI 线程上预先抓好的** DispatcherQueue，不能在 watcher 线程里读
    /// `_window.DispatcherQueue` —— 那是 WinRT 属性，跨线程访问会抛
    /// COMException 0x8001010E(RPC_E_WRONG_THREAD)，而且消息是空的。
    /// 原来那版就是踩了这个坑：异常被下面的 catch 吞掉后直接 return，
    /// 于是 watcher 线程静默死掉，「双击图标唤到前台」这个功能从此彻底失效，
    /// 日志里还什么都看不到。
    /// </summary>
    private void WireShowRequest()
    {
        if (Program.ShowRequestEvent is null) return;
        _showRequest = Program.ShowRequestEvent;

        // 本方法在 OnLaunched 里调用，此刻就是 UI 线程，取到的是可信的引用。
        var ui = _window?.DispatcherQueue;

        _showWatcher = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showRequest.WaitOne()) continue;
                }
                catch (Exception ex)
                {
                    // 句柄失效之类的终止性错误：记一笔再退出线程，不要静默死
                    StartupLog.Error("ShowRequest watcher", ex);
                    return;
                }

                try
                {
                    if (ui is null) continue;
                    if (!ui.TryEnqueue(() => _window?.BringToFront()))
                    {
                        StartupLog.Warn("ShowRequest: DispatcherQueue 已停止，无法唤到前台");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // 单次唤前台失败不该拖垮整个 watcher，记下来继续等下一次
                    StartupLog.Error("ShowRequest BringToFront", ex);
                }
            }
        })
        { IsBackground = true };
        _showWatcher.Start();
    }
}
