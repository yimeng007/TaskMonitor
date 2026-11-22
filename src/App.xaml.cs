using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Common.IconKeys;
using iNKORE.UI.WPF.Modern.Controls;

namespace task_monitor
{
    /// <summary>
    /// Application entry point. There is intentionally no main window — this is a
    /// taskbar widget whose only persistent UI is the overlay (rendered on its own
    /// dedicated STA background thread). <see cref="ShutdownMode"/> is
    /// <c>OnExplicitShutdown</c>, so the process stays alive with no open WPF window;
    /// exit is via the overlay's right-click menu. Clicks on the overlay are marshaled
    /// here onto the UI thread to show the on-demand <see cref="DetailWindow"/>.
    /// </summary>
    public partial class App : Application
    {
        private readonly SamplingController _sampling = new SamplingController();
        private TaskbarOverlayManager _taskbars;
        // The transient (unpinned) flyout — at most one; torn down on every toggle.
        private DetailWindow _detail;
        // Pinned windows — one per column at most; survive focus loss and coexist with
        // the transient flyout. All open windows get every snapshot push (one per tick).
        private readonly List<DetailWindow> _pinned = new List<DetailWindow>();

        /// <summary>同时最多固定几个详情窗。每个固定窗常驻一整页 WPF 树（图表＋进程列表＋图标），
        /// 3 个是"够对比、又不至于把内存推高"的折中；加上 1 个未固定的临时窗，屏上最多 4 个。</summary>
        private const int MaxPinnedDetails = 3;

        private const string SingleInstanceMutexName = @"Global\TaskMonitor.exe__7f3a2c9e-4b1d-4e8a-9f2c-6a5b8c7d1e0a";
        private const string SecondInstanceEventName = SingleInstanceMutexName + ".AlreadyRunning";

        // Both handles live for the whole process. The event is created BEFORE the mutex so
        // any process that can observe the mutex can also signal this instance reliably.
        private Mutex _singleInstanceMutex;
        private EventWaitHandle _secondInstanceEvent;
        private RegisteredWaitHandle _secondInstanceWait;
        private AlreadyRunningDialog _alreadyRunningDialog;
        private bool _alreadyRunningPending;

        // Set in OnExit so the overlay-recreate loop in StartTaskbar stops re-entering
        // TaskbarWindow.Start() — a shutdown must not spawn a fresh overlay.
        private volatile bool _stopping;

        // The loaded settings.yaml (run directory). Read once by the elevation gate at
        // startup; kept on the instance — the settings pages share this same store.
        private AppSettings _config;

        // Global crash hooks that don't need Application.Current — installed in the
        // static ctor so they exist before Main() runs (even an App.xaml BAML failure
        // inside InitializeComponent is caught). The file log is initialized before the
        // hooks so even those earliest crashes have somewhere to be written; the Dispatcher
        // hook needs Application.Current, so it is added at the top of OnStartup. Every
        // unhandled managed exception funnels into CrashReporter → log file + crash dialog.
        static App()
        {
            // Releases ship only the exe, not its .config. Opt in before WPF creates any
            // window so each one scales to its own monitor under the PerMonitorV2 manifest.
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            // WPF multi-window BitmapCache defect: a UAC/lock-screen display reset can leave
            // later windows unrenderable while the Dispatcher stays alive. iNKORE enables this
            // cache in its Window/Popup templates by default; disable it before ANY window.
            iNKORE.UI.WPF.Modern.Common.ShadowAssist.UseBitmapCache = false;
            Logger.Init();
            CrashReporter.Install();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // UI-thread crash hook FIRST — before the elevation gate, so even the
            // ConsentDialog path is covered. One fault must never silently kill the
            // process: report it, and only exit when the user picks 退出 in the dialog.
            DispatcherUnhandledException += (s, e2) =>
            {
                e2.Handled = true;
                if (!CrashReporter.Report("UI 线程未处理异常", e2.Exception, fatal: false, block: true))
                    Shutdown();
            };

            // Elevation gate FIRST: the app is designed to always run elevated, and every
            // unelevated path inside it ends in Shutdown(). This MUST come before the
            // single-instance mutex — an unelevated "launcher" instance that is about to
            // exit must never hold the mutex, or the elevated relaunched child would take
            // itself for a second instance and exit before doing any work.
            if (!RunElevationGate()) return;

            // Both objects are GLOBAL and therefore reachable only after elevation. Creating
            // the auto-reset event first closes the startup race: once another process can see
            // the mutex, this event already exists and its Set() cannot be lost with the handle.
            _secondInstanceEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SecondInstanceEventName);
            _singleInstanceMutex = new Mutex(
                initiallyOwned: false,
                name: SingleInstanceMutexName,
                createdNew: out bool createdNew);
            if (!createdNew)
            {
                Logger.Info("全局互斥体已存在——通知正在运行的实例显示位置，本实例退出");
                try { _secondInstanceEvent.Set(); }
                catch (Exception ex) { Logger.Warn("通知正在运行的实例失败", ex); }
                Shutdown();
                return;
            }
            _secondInstanceWait = ThreadPool.RegisterWaitForSingleObject(
                _secondInstanceEvent, OnSecondInstanceSignaled, null, Timeout.Infinite, false);

            // With the mutex held, any shutdown.sentinel on disk targeted a PREVIOUS
            // instance (the build flow's touch is always consumed by the still-running
            // old instance before we can start) — a leftover must not self-exit us 2s in
            // (2026-09-29: a double-touched sentinel did exactly that, and the shutdown
            // then raced the queued prewarm into a crash dialog).
            TaskbarWindow.ClearStaleShutdownSentinel();

            // 最后一道网：任何忘了 await 的异步任务，其异常默认会在终结器线程重新抛出，
            // 表现为「后台任务异常 → 程序遇到未处理的异常」弹框（2026-11-16 实测：
            // TcpClient.EndConnect 的 NRE 就是这么冒出来的）。
            // 这类异常对常驻监控程序不该致命 —— 标记为已观察，记录留痕但不崩。
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                Logger.Warn("未观察的任务异常（已忽略，避免进程崩溃）", ex.Exception);
                ex.SetObserved();
            };

            base.OnStartup(e);

            Logger.Info($"启动 — 版本 {VersionInfo.Current}，OS {Environment.OSVersion}，Win11+={TaskbarWindow.IsWin11OrLater}");

            // 最低系统要求 = **Windows 10**。三层依赖叠加，Win7 / Win8 都过不了：
            //   ① 覆盖层是 D3D11 → DXGI flip-model → D2D1 → **DirectComposition**（Win8.1 起）
            //   ② 窗口带 WS_EX_NOREDIRECTIONBITMAP（Win8 起）
            //   ③ 每帧按显示器 DPI 布局，用 **GetDpiForWindow / SetThreadDpiAwarenessContext**
            //      （Win10 1607 起）—— 在 Win7 上这两条 P/Invoke 直接抛
            //      EntryPointNotFoundException，不是「画不出来」而是「崩」
            // 没有这道闸的预期：Win7 上要么崩、要么进程在跑但界面全空且毫无提示
            // （2026-11-16 准备做 Win7 兼容性测试时补上）。
            // 精确到 1607（build 14393）：GetDpiForWindow / SetThreadDpiAwarenessContext 都是那一版
            // 才加入 user32 的，Win10 1507/1511 上同样会 EntryPointNotFoundException。
            var osv = Environment.OSVersion.Version;
            bool osTooOld = osv.Major < 10 || (osv.Major == 10 && osv.Build < 14393);
            if (osTooOld)
            {
                Logger.Warn($"系统版本 {osv}（build {osv.Build}）低于最低要求 Windows 10 1607（build 14393），提示后退出");
                System.Windows.MessageBox.Show(
                    "TaskMonitor 需要 Windows 10 或更高版本。\n\n"
                    + "任务栏 / 悬浮窗的显示依赖三项系统能力：\n"
                    + "· DirectComposition 合成（Windows 8.1 起）\n"
                    + "· 无重定向位图窗口（Windows 8 起）\n"
                    + "· 按显示器取 DPI 的接口（Windows 10 1607 起）\n\n"
                    + "在 Windows 7 / 8 / 8.1，以及 Windows 10 早期版本（1607 之前）上会崩溃或界面全空，故不再支持。",
                    "TaskMonitor", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            // 开机自启动的任务里记着「注册那一刻的 exe 路径」：换过位置（重新解压 / 换目录 /
            // 从单文件版换文件夹版）之后任务还在、设置页仍显示「开」，但每次登录都在启动一个
            // 不存在的文件（任务计划 LastResult=0x80070002），界面毫无提示。
            // 这里在启动时校正一次：只有用户已经开过自启（任务存在）才改写，不新建、不弹窗。
            // 常驻提权进程才能读系统任务文件、也才改得动 HighestAvailable 任务，故放在提权门之后。
            StartupTask.RefreshIfEnabled();

            // Repaint already-open detail windows on ANY effective theme flip — the 设置
            // 主题 combo, or a system-theme change while 跟随系统 (the iNKORE ThemeManager
            // tracks it and fires this; everything DynamicResource-driven — the settings
            // window, the context menu — restyles itself and needs nothing here).
            ThemeManager.Current.ActualApplicationThemeChanged += (s, e2) =>
            {
                _detail?.ApplyTheme();
                foreach (var w in _pinned) w.ApplyTheme();
                // 悬浮模式的自绘卡片用的是 detail 弹窗那套色调常量，所以它的卡片颜色和文字颜色
                // 跟 APP 主题走（DetailWindow.ApplyTheme 同源），不是任务栏的系统主题。
                _taskbars?.SetFloatingDark(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark);
            };

            if (ShouldShowStartupGuide()) ShowStartupGuide();
            StartTaskbar();
            if (_alreadyRunningPending)
            {
                Dispatcher.BeginInvoke(new Action(ShowAlreadyRunning),
                    System.Windows.Threading.DispatcherPriority.Background);
            }

            // 卡死看门狗 — see CheckOverlayHealth. A freeze that heals itself leaves NOTHING
            // in the log, because the stalled thread is the very thread that would write it.
            StartOverlayWatchdog();

            // Pre-warm WPF: the overlay is a native Win32 window, so without this the
            // FIRST WPF window ever shown is the user's first click — which then eats
            // the whole cold-start tax (render thread/D3D, JIT, BAML, style realization,
            // acrylic, fonts). One throwaway DetailWindow, built + laid out offscreen
            // with every view once, pays it up front. Deferred past OnStartup so the
            // extra show/close can't disturb WPF's first-window bookkeeping; it still
            // completes long before the user can click.
            // NOTE: the right-click menu host stays LAZILY created (first right-click) —
            // showing it at startup regressed the menu: it stopped opening at all.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // This Background-priority slot can be reached MID-SHUTDOWN: OnExit's
                // Join of the taskbar thread keeps pumping the STA queue, dispatching
                // whatever is still queued (2026-09-29: a stale sentinel exited the app
                // 2s in, and the prewarm then ran against resources that were already
                // being torn down — NRE inside WPF's DeferredAppResourceReference, crash
                // dialog on a graceful exit). Skip when stopping; Prewarm itself also
                // never throws (it is a pure optimization).
                if (_stopping) return;
                new DetailWindow(_sampling).Prewarm();
                TrimMemorySoon("详情预热后");
            }), System.Windows.Threading.DispatcherPriority.Background);

            UpdateChecker.CheckOnce(_config, TrySaveConfig);
        }

        // 退出：先把菜单收起来，再让出一帧，最后才真正 Shutdown。
        //
        // 原先是 exit.Click → Shutdown() 直连，但 Shutdown() 会**同步**进入 OnExit：
        // 摘托盘、停采样、存配置、以及对每个覆盖层线程 Join 至多 1 秒 —— UI 线程被占住的这段，
        // 菜单的收起动画卡在半透明状态，而覆盖层在自己的线程上早没了，于是看起来
        // 「任务栏先消失、菜单慢半拍」。Dispatchers 在 Background 优先级低于 Render，
        // 所以 BeginInvoke 到 Background 能保证菜单先把这一帧画完。
        private void BeginShutdown()
        {
            try { if (_taskbarMenu != null) _taskbarMenu.IsOpen = false; } catch { }
            try { _tray?.Dispose(); _tray = null; } catch { }   // 托盘菜单与任务栏菜单是同一个
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Shutdown));
        }

        /// <summary>退出时把还开着的窗口一次性关掉（不触发回收，进程马上就没了）。</summary>
        private void CloseWindowsForExit()
        {
            try { _settings?.Close(); } catch { }
            try { _detail?.Close(); } catch { }
            foreach (var w in _pinned.ToArray()) { try { w.Close(); } catch { } }
            _settings = null;
            _detail = null;
            _pinned.Clear();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            var exitWatch = System.Diagnostics.Stopwatch.StartNew();
            Logger.Info("退出");
            _stopping = true;

            // 先把还开着的设置页 / 详情窗关掉：窗口晚于 taskbar 停机会在拆解阶段还挂着
            // 渲染资源与 COM 引用，退出会明显变慢（18.2 的"快速关闭"就是靠这个顺序）。
            try { CloseWindowsForExit(); } catch (Exception ex) { Logger.Debug("退出时关窗口失败：" + ex.Message); }
            // 先摘掉托盘图标：留着会在托盘里停一个点不动的死图标，直到用户划过它。
            _tray?.Dispose();
            _tray = null;
            _secondInstanceWait?.Unregister(null);
            _secondInstanceWait = null;
            _secondInstanceEvent?.Dispose();
            _secondInstanceEvent = null;
            // Flush a pending 透明度 debounce first: the timer dies with the dispatcher, so
            // quitting within its 500ms window silently reverted the last slider step even
            // though the comment above the timer promises the value still lands.
            if (_configSaveDebounce?.IsEnabled == true)
            {
                _configSaveDebounce.Stop();
                TrySaveConfig();
            }
            // 停掉两个还会自己跑的定时器：看门狗（每 2 秒读已停对象）、更新重试（到点会写 config）。
            _overlayWatch?.Stop();
            UpdateChecker.Stop();
            _taskbars?.StopAll();
            TaskbarWindow.ReleaseNativeCallChannel();
            _sampling.Stop();
            // 退出前把本月流量落盘（采样已停，数值定住了）。可能和上面的 debounce 保存重复一次，
            // 但 TrySaveConfig 是整体覆盖写，重复没有副作用。
            NetUsage.Store(_config);
            TrySaveConfig();
            // We never took ownership of the mutex (initiallyOwned=false), so there's
            // nothing to release — just drop the handle. Done explicitly so the lifetime
            // is unambiguous; process death would close it regardless.
            _singleInstanceMutex?.Dispose();
            Logger.Info($"退出完成：用时 {exitWatch.ElapsedMilliseconds}ms");
            base.OnExit(e);
        }

        private void OnSecondInstanceSignaled(object state, bool timedOut)
        {
            if (timedOut || _stopping) return;
            try { Dispatcher.BeginInvoke(new Action(ShowAlreadyRunning)); }
            catch (Exception ex)
            {
                if (!_stopping) Logger.Warn("二次启动通知未能投递到 UI 线程", ex);
            }
        }

        private void ShowAlreadyRunning()
        {
            if (_stopping) return;
            if (_alreadyRunningDialog != null)
            {
                _alreadyRunningDialog.Activate();
                return;
            }
            if (_taskbars == null)
            {
                _alreadyRunningPending = true;
                return;
            }
            _alreadyRunningPending = false;
            List<string> locations;
            try { locations = _taskbars.GetLocationDescriptions(); }
            catch (Exception ex)
            {
                Logger.Warn("读取已运行窗口位置失败", ex);
                locations = new List<string>();
            }
            string message = locations.Count == 0
                ? "软件已经在运行，无需重复启动。\n\n窗口目前正在初始化或重建，请稍后再次点击软件查看位置。"
                : "软件已经在运行，无需重复启动。\n\n当前窗口位置：\n" + string.Join("\n", locations);
            Logger.Info("收到二次启动通知，显示已运行提示");
            var dialog = new AlreadyRunningDialog(message);
            _alreadyRunningDialog = dialog;
            dialog.Closed += (s, e2) =>
            {
                if (ReferenceEquals(_alreadyRunningDialog, dialog)) _alreadyRunningDialog = null;
            };
            dialog.Show();
            dialog.Activate();
        }

        // ---------- Elevation gate (first-run consent + runas self-relaunch) ----------
        // The app is designed to ALWAYS run elevated (the SRUM per-process network API
        // returns nothing useful otherwise), but the manifest is asInvoker so the first
        // launch can ask for consent WITHOUT a UAC prompt. The consent ("是否允许") is
        // persisted to settings.yaml in the run directory; refusal is deliberately NOT
        // persisted — the next launch asks again. Every unelevated path ends in
        // Shutdown(): the process never runs degraded.
        private bool RunElevationGate()
        {
            _config = AppSettings.Load();
            // 本月流量：从配置恢复（跨月则归零，见 NetUsage.Load 的说明）。
            // 保留月数要先设好，Load 才会按它裁剪历史（默认 12 个月）。
            NetUsage.KeepMonths = _config.NetHistoryKeepMonths ?? NetHistory.DefaultKeepMonths;
            NetUsage.ShowHistory = _config.ShowNetHistory == true;   // 默认关
            NetUsage.Load(_config);
            NetUsage.ShowMonth = _config.ShowNetMonthUsage == true;   // 默认关：要让用户主动开
            NetUsage.ShowToday = _config.ShowNetTodayUsage == true;   // 默认关：要让用户主动开
            NetUsage.FixedGb = _config.NetUsageFixedGb == true;     // 默认关：正常自动升档
            _netUsageSavedAt = (long)SystemInfo.GetTickCount64();
            // Theme first, before ANY window exists (the ConsentDialog below included) —
            // re-applied to open windows live via the ActualApplicationThemeChanged hook.
            ApplyThemeSetting();

            if (IsElevated()) return true;

            if (_config.ElevationConsent == true)
            {
                // Consented before → every launch self-elevates. A UAC-cancel lands in
                // TryRelaunchElevated's catch; either way this launcher exits.
                Logger.Info("提权门：已持久化同意——runas 自我重启提升权限");
                TryRelaunchElevated();
                Shutdown();
                return false;
            }

            // First run (or previously refused — nothing was written then): ask.
            bool consented = false;
            if (new ConsentDialog().ShowDialog() == true)
            {
                consented = true;
                Logger.Info("提权门：用户首次同意, 持久化后 runas 自我重启提升权限");
                _config.ElevationConsent = true;
                if (_config.IsNewInstall) _config.StartupGuideCompleted = false;
                TrySaveConfig();        // best effort — a read-only install dir degrades to "ask again"
                TryRelaunchElevated();
            }
            // Refused (不允许 / ✕ / Esc), or the UAC prompt was cancelled (reported by
            // TryRelaunchElevated's own catch): exit rather than run degraded —
            // "始终保持在管理员权限的状态下运行". Logged only on the refusal path: an
            // unconditional line here made a consented relaunch look refused in the log.
            if (!consented) Logger.Info("提权门：未提权（用户拒绝或对话框取消）");
            Shutdown();
            return false;
        }

        private static bool IsElevated() =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

        // Relaunch this exe elevated via the shell's "runas" verb. Process.Start blocks
        // while the UAC prompt is up and throws Win32Exception (ERROR_CANCELLED 1223)
        // when the user declines — swallowed here because the caller exits regardless.
        private static void TryRelaunchElevated()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Verb = "runas",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Logger.Info($"UAC 提示被取消（Win32 err={ex.NativeErrorCode}）——启动器退出");
            }
        }

        private void TrySaveConfig()
        {
            try { _config.Save(); }
            catch (Exception ex) { Logger.Warn("settings.yaml 保存失败（下次启动将再询问）", ex); }
        }

        /// <summary>
        /// 本月流量的落盘节拍：每 30 秒一次（退出时另有一次，见 OnExit）。
        /// 在**采样线程**被调用（每秒一次），所以这里只做一次时间比较，真正的写盘丢给 UI 线程 —
        /// 采样线程碰文件会拖慢采样。节流放在这里而不是用定时器：少一个 DispatcherTimer 要管生命周期。
        /// </summary>
        private void MaybePersistNetUsage()
        {
            long now = (long)SystemInfo.GetTickCount64();
            if (now - _netUsageSavedAt < 30000) return;
            _netUsageSavedAt = now;
            try
            {
                Dispatcher?.BeginInvoke(new Action(() =>
                {
                    NetUsage.Store(_config);
                    TrySaveConfig();
                }));
            }
            catch (Exception ex)
            {
                // 退出瞬间 Dispatcher 已关，丢一次保存无妨（OnExit 还会再存一次）。
                Logger.Debug($"本月流量落盘跳过（{ex.GetType().Name}）");
            }
        }

        /// <summary>
        /// 把位置钉成「关 + 最左端」——第三方任务栏下的唯一安全位置。
        /// 三个字段必须一起写：只写 OverlayOnLeft 时，旧的 OverlaySnapToStart 会让
        /// AnchorIndexOf 回退到「贴近开始按钮」。
        /// </summary>
        private void ForceLeftAnchor()
        {
            _config.OverlayOnLeft = null;
            _config.OverlayAnchor = 0;
            _config.OverlaySnapToStart = null;
        }

        private bool ShouldShowStartupGuide()
        {
            // 有第三方任务栏工具时不弹引导（静默用默认值），只弹一次「检测到 X」；卸载后再补一次。
            string thirdParty = DetectThirdPartyTaskbar();
            if (thirdParty != null)
            {
                _config.ThirdPartyTaskbarSeen = true;

                bool firstTime = _config.StartupGuideCompleted != true;
                if (firstTime || _config.StartupGuideCompleted == null)
                {
                    // 静默走"完成"：位置用默认（最左端），不改开机自启，也不弹任何窗口。
                    _config.StartupGuideCompleted = true;
                    _config.StartupGuideSkippedForThirdParty = true;
                    ForceLeftAnchor();
                    TrySaveConfig();
                    Logger.Info("检测到 " + thirdParty + "——已静默跳过首次引导（位置取默认最左端）");
                }
                return false;
            }

            // 已卸载且上次是被它顶掉的 → 补一次引导；老配置用「看到过 + 弹过提示」等价判断。
            bool skippedByThirdParty =
                _config.StartupGuideSkippedForThirdParty == true
                || (_config.ThirdPartyTaskbarSeen == true && _config.ThirdPartyTaskbarHintShown == true);

            if (skippedByThirdParty && _config.ThirdPartyTaskbarSeen == true)
            {
                _config.StartupGuideSkippedForThirdParty = null;
                _config.ThirdPartyTaskbarSeen = null;
                _config.StartupGuideCompleted = null;      // 回到"未完成"，下次进入桌面即弹
                TrySaveConfig();
                Logger.Info("第三方任务栏工具已卸载——恢复首次引导，本次启动会重新走一遍");
                return true;
            }

            return _config.StartupGuideCompleted == false ||
                   (_config.StartupGuideCompleted == null && _config.IsNewInstall);
        }

        private void ShowStartupGuide()
        {
            var displays = TaskbarDisplayCatalog.GetDisplays();
            // 左对齐的任务栏左端没有空位（被开始按钮和图标占满），所以向导不再给
            // 「任务栏最左端」/「贴近开始按钮」两张卡，只提供托盘一侧。竖排任务栏更进一步：
            // 三个横向锚点全部不适用，覆盖层自己避开开始按钮
            // （开始按钮在中段就贴顶端，在顶端就贴托盘上方）。
            bool taskbarCentered = TaskbarWindow.IsTaskbarCenterAligned();
            bool taskbarVertical = TaskbarWindow.IsTaskbarVertical();
            Logger.Info($"首次启动引导：任务栏{(taskbarVertical ? "竖直" : "水平")}，对齐={(taskbarCentered ? "居中" : "靠左")}");
            var guide = new StartupGuideWindow(SupportsSnapToStart(displays, _config.TaskbarDisplays), taskbarCentered, taskbarVertical);
            if (guide.ShowDialog() == true)
            {
                _config.FloatingMode = guide.UseFloatingMode ? true : (bool?)null;
                if (!guide.UseFloatingMode)
                {
                    // 三张位置卡都是「按位置摆放」的选项 —— 开关保持开启，答案由 anchor 携带。
                    // 以前托盘那张卡写 onLeft:false 是可以的，但那个值现在表示「固定最左端」
                    // （2026-11-10 改动所致）。
                    int anchor = guide.OverlayAnchor;
                    _config.OverlayOnLeft = null;
                    _config.OverlayAnchor = anchor == 0 ? (int?)null : anchor;
                    _config.OverlaySnapToStart = anchor == 1 ? true : (bool?)null;
                }
                string placement = guide.UseFloatingMode ? "悬浮窗"
                    : guide.OverlayAnchor == 1 ? "任务栏·开始按钮旁"
                    : guide.OverlayAnchor == 2 ? "任务栏·贴近系统托盘" : "任务栏·最左端";
                Logger.Info($"首次启动引导完成：{placement}");
            }
            else
            {
                // **跳过 = 只用默认值，不替用户做选择**（2026-11-16 修两处）：
                //
                // ① 位置：以前跳过什么都不写，配置里就缺 OverlayAnchor，读的时候回退成
                //    「0 = 最左端」—— 而那正是向导刻意不给用户选的位置（左对齐任务栏的左端
                //    被开始按钮和图标占满，实测覆盖层会挤在开始按钮右边）。
                //    现在按向导自己的规则补上默认值：靠左 / 竖排任务栏 → 2（贴近系统托盘）；
                //    居中任务栏的左端是真空位，保持最左端（不写 = 0）。
                // 靠左 / 竖排：默认放到托盘一侧（左端被开始按钮和图标占满，站不住）。
                // 居中任务栏：左端是真空位，保持"最左端"= 不写 OverlayAnchor。
                // 两种情形都要清掉旧的布尔偏好 —— 否则旧配置里的 overlaySnapToStart=true
                // 会让 AnchorIndexOf 回退成 1（贴近开始按钮），与「居中=最左端」的意图不符。
                _config.OverlayAnchor = (taskbarVertical || !taskbarCentered) ? 2 : (int?)null;
                _config.OverlayOnLeft = null;
                _config.OverlaySnapToStart = null;
                Logger.Info("首次启动引导已跳过——位置用默认值"
                          + (taskbarVertical || !taskbarCentered ? "（任务栏·贴近系统托盘）" : "（任务栏·最左端）"));
            }
            // 向导里的「开机自启动」勾选：**只在用户点了完成时应用**。
            // 跳过（DialogResult=false）表示「别替我决定」，此时保持原状 ——
            // 以前这段在 if/else 之外，跳过了也会按默认勾选把自启打开，与「跳过=保持原样」的
            // 约定不符（2026-11-16 用户反馈）。
            if (guide.DialogResult == true
                && StartupTask.IsEnabledForCurrentExe() != guide.AutoStartChosen)
            {
                bool ok = StartupTask.SetEnabled(guide.AutoStartChosen);
                Logger.Info($"首次启动引导：开机自启动 = {guide.AutoStartChosen}（{(ok ? "已应用" : "应用失败，保持原状")}）");
            }

            _config.StartupGuideCompleted = true;
            _config.StartupGuideSkippedForThirdParty = null;   // 用户自己走完，不再补第二次
            TrySaveConfig();

            TrimMemorySoon("引导关闭后");   // 引导是 WPF 里最重的一屏，不回收会多留 50~100MB
        }

        private void StartTaskbar()
        {
            _taskbars = new TaskbarOverlayManager(_sampling);
            _taskbars.ToggleCallback = (source, column) =>
                Dispatcher.BeginInvoke(new Action(() => ToggleDetail(source, column)));
            _taskbars.RightClickRequested = () =>
                Dispatcher.BeginInvoke(new Action(ShowTaskbarMenu));
            _taskbars.AnchorRemoved = OnAnchorRemoved;
            _taskbars.CatalogChanged = OnTaskbarCatalogChanged;
            _sampling.SnapshotChanged = snapshot =>
            {
                _taskbars.PublishSnapshot(snapshot);
                // 本月流量累计：只在采样线程里做加法（很轻），落盘由 UI 线程每 30 秒一次。
                NetUsage.Tick(snapshot.NetUpBytesPerSec, snapshot.NetDownBytesPerSec, snapshot.SampleIntervalMs);
                Dispatcher.BeginInvoke(new Action(RefreshDetails));
                MaybePersistNetUsage();
            };

            // 第三方任务栏下界面会锁死位置开关；配置必须跟着钉死，否则界面显示"关"、
            // 运行时仍按旧配置摆到别处。
            if (DetectThirdPartyTaskbar() != null) ForceLeftAnchor();

            _taskbars.SetPlacement(_config.OverlayOnLeft != false, AnchorIndexOf(_config));
            _taskbars.SetCompactLayout(_config.CompactLayout == true);
            _taskbars.SetShowSeparators(_config.ShowSeparators != false);
            _taskbars.SetOverlayUsageGraph(_config.OverlayUsageGraph == true);
            ApplyGraphColor(ResolveGraphRgb());
            StartGraphColorWatch();
            _taskbars.SetItemOrder(_config.ItemOrder);
            _taskbars.SetSampleInterval(_config.SampleIntervalMs ?? 1000);
            _taskbars.SetMetricSamplingMask(SamplingMaskOf(_config));
            _sampling.SetMergeByPath(_config.MergeSamePathProcesses != false);
            _sampling.SetDiskDisplay(DiskDisplayModeIndexOf(_config), _config.DiskDisplayIndex ?? 0);
            _sampling.SetGpuDisplay(GpuDisplayModeIndexOf(_config), _config.GpuDisplayIndex ?? 0);
            _taskbars.SetGpuTempDisplay(_config.GpuTaskbarContent ?? GpuTaskbarContent.Usage,
                _config.GpuTempIndex ?? -1, _config.GpuTempUnit == GpuTempUnit.Fahrenheit);
            _sampling.SetNetAdapter(_config.NetAdapterId);
            _sampling.SetClashApi(_config.ClashEnabled != false, _config.ClashApiAddress, _config.ClashApiSecret);
            _sampling.SetPublicIpLookup(_config.PublicIpEnabled != false);

            _taskbars.SetFloatingMode(_config.FloatingMode == true, _config.FloatingX, _config.FloatingY);
            _taskbars.SetFloatingTopmost(_config.FloatingTopmost != false);
            _taskbars.SetFloatingAllowOutOfBounds(_config.FloatingAllowOutOfBounds == true);
            _taskbars.SetFloatingOpacity(_config.FloatingOpacity ?? 1.0);
            _taskbars.SetFloatingEdgeHide(_config.FloatingEdgeHide == true);
            _taskbars.SetFloatingFullscreenHide(_config.FloatingFullscreenHide != false);
            _taskbars.SetFloatingDark(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark);
            _taskbars.FloatingPositionChanged = (x, y) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _config.FloatingX = x;
                _config.FloatingY = y;
                TrySaveConfig();
            }));
            _taskbars.FloatingDragged = (dx, dy) =>
                Dispatcher.BeginInvoke(new Action<int, int>(FollowFloatingDrag), dx, dy);

            _sampling.Start();
            _taskbars.Start(_config.FloatingMode == true, _config.TaskbarDisplays);
            StartTrayIcon();

            // 第三方"重写任务栏"的工具（StartAllBack / ExplorerPatcher）会让任务栏嵌入不稳，
            // 提示一次改用悬浮模式。**必须等托盘图标起来之后**再问，且走 Dispatcher 异步：
            // 之前在启动中途同步弹 MessageBox，进程会卡在一个看不见的模态框里
            // （2026-11-20 实测：日志停在"启动 — 版本"，覆盖层不出现，内存却占住）。
            Dispatcher.BeginInvoke(new Action(OfferFloatingModeIfThirdPartyTaskbar),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// 检测 StartAllBack / ExplorerPatcher 这类"重写任务栏"的工具，提示一次改用悬浮模式。
        /// 依据：它们接管了任务栏的坐标与窗口结构，本程序嵌入任务栏会与它们抢布局
        /// （上游 issue #2；实测见 资料/03-技术资料/上游issue清查/#2-与StartAllBack不兼容.md）。
        /// </summary>
        private void OfferFloatingModeIfThirdPartyTaskbar()
        {
            if (_config.ThirdPartyTaskbarHintShown == true) return;
            if (_config.FloatingMode == true) return;      // 已经在用悬浮模式，不必提示

            string detected = DetectThirdPartyTaskbar();
            if (detected == null) return;

            _config.ThirdPartyTaskbarHintShown = true;     // 无论选什么都只提示一次
            TrySaveConfig();                               // 立刻落盘：后面就算崩了也不会反复问
            Logger.Info("检测到 " + detected + "——询问是否改用悬浮模式");

            // 本程序没有主窗口，裸 MessageBox 会被盖住；用屏幕外的置顶 owner 把它顶到前台。
            Window owner = null;
            MessageBoxResult r = MessageBoxResult.No;
            try
            {
                owner = new Window
                {
                    Width = 1, Height = 1, Left = -20000, Top = -20000,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = true,
                    Topmost = true,
                };
                owner.Show();
                owner.Activate();
                r = System.Windows.MessageBox.Show(owner,
                    "检测到 " + detected + "。\n\n"
                    + "它会接管任务栏的布局（包括左边的开始按钮和中间的图标区），"
                    + "本程序嵌入任务栏时两边会互相抢位置。实测后果：任务栏图标被挤、"
                    + "窗口一路右移出屏幕、任务栏位置开关一开就乱。\n\n"
                    + "请选择：\n"
                    + "【是】保持现状 —— 窗口固定在任务栏最左端，「贴近系统托盘」被禁用，"
                    + "等于是安全模式（推荐，不影响你继续用 " + detected + "）。\n"
                    + "【否】我要卸载 " + detected + " —— 卸载后任务栏回到系统原始状态，"
                    + "位置选项全部恢复可用（会打开卸载程序，卸载与否你确认）。\n\n"
                    + "之后可随时在 设置 → 显示与排列 里调整。",
                    "TaskMonitor · 检测到第三方任务栏", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }
            catch (Exception ex) { Logger.Warn("悬浮模式提示框显示失败：" + ex.Message); }
            finally { try { owner?.Close(); } catch { } }

            if (r == MessageBoxResult.Yes)
            {
                _config.FloatingMode = false;
                ForceLeftAnchor();
                TrySaveConfig();
                Logger.Info("检测到 " + detected + "——用户选择保持现状：窗口固定最左端，「贴近系统托盘」已禁用");
            }
            else
            {
                Logger.Info("检测到 " + detected + "——用户选择卸载，正在打开卸载程序");
                TryOpenUninstaller(detected);
            }
        }

        /// <summary>
        /// 打开第三方任务栏工具的卸载程序（用户点了「我要卸载」时才调）。
        /// 只负责把卸载界面拉起来，真正的卸载动作仍由用户确认——不做静默卸载。
        /// </summary>
        internal static bool TryOpenUninstaller(string detected)
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                string[] candidates = detected == "StartAllBack"
                    ? new[]
                      {
                          Path.Combine(pf, "StartAllBack", "StartAllBackCfg.exe"),
                          Path.Combine(local, "StartAllBack", "StartAllBackCfg.exe"),
                      }
                    : new[]
                      {
                          Path.Combine(local, "ExplorerPatcher", "ep_setup.exe"),
                      };

                foreach (string exe in candidates)
                {
                    if (!File.Exists(exe)) continue;
                    Process.Start(new ProcessStartInfo(exe, "/uninstall") { UseShellExecute = true });
                    Logger.Info("已打开卸载程序：" + exe);
                    return true;
                }

                Logger.Warn("没找到 " + detected + " 的卸载程序，请到 控制面板 → 程序和功能 手动卸载");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn("打开卸载程序失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>返回检测到的第三方任务栏工具名；没有则 null。只用可执行文件/注册表存在性判断，不注入、不枚举模块。</summary>
        /// <summary>
        /// 检测 StartAllBack / ExplorerPatcher（只看文件与注册表存在性，不注入、不枚举模块）。
        /// 结果缓存 5 秒：设置页每秒断言一次、恢复/切换路径也会调，避免反复做 IO。
        /// 卸载后最多 5 秒才反映为"已卸载"，对用户无感。
        /// </summary>
        internal static string DetectThirdPartyTaskbar()
        {
            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;   // .NET Framework 4.8 没有 TickCount64
            if (now - _thirdPartyProbeTick < 5000) return _thirdPartyProbe;

            string found = null;
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                if (File.Exists(Path.Combine(local, "StartAllBack", "StartAllBackCfg.exe"))
                    || File.Exists(Path.Combine(pf, "StartAllBack", "StartAllBackCfg.exe")))
                    found = "StartAllBack";
                else if (Directory.Exists(Path.Combine(local, "ExplorerPatcher")))
                    found = "ExplorerPatcher";
                else
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\StartIsBack"))
                        if (k != null) found = "StartAllBack";
                }
            }
            catch { /* 探测失败就当没装 */ }

            _thirdPartyProbe = found;
            _thirdPartyProbeTick = now;
            return found;
        }

        private static string _thirdPartyProbe;
        private static long _thirdPartyProbeTick = -100000;

        // ---------- 托盘图标 ----------
        // 存在的理由：取消置顶后点「显示桌面」，悬浮窗会被桌面盖住，而那之后没有入口能把它
        // 叫回来（用户 2026-11-11 实报只能「随便点一个网页」）。左键 = 召回，右键 = 同一份菜单。
        private TrayIcon _tray;

        private void StartTrayIcon()
        {
            if (_tray != null) return;
            _tray = new TrayIcon();
            _tray.LeftClick += OnTrayLeftClick;
            _tray.RightClick += () => ShowTaskbarMenu();
            // 悬停只显示程序名（用户 2026-11-11 要求去掉后面那串用法说明）。
            _tray.Show("TaskMonitor");
        }

        private void OnTrayLeftClick()
        {
            // 「把它叫回来」：不管是被「显示桌面」的桌面盖住、被别的窗口盖住，还是任务栏那边
            // 整个不见了（explorer 重启把覆盖层连带销毁），左键都试一遍 —— 悬浮窗没了就召回，
            // 任务栏覆盖层没了就整块重建。健康状态下只是轻轻重新贴一次任务栏，不会闪。
            _taskbars.RecallOrRebuild();
        }

        private void RefreshDetails()
        {
            _detail?.Refresh();
            foreach (var w in _pinned) w.Refresh();
            _settings?.RefreshAbout();   // the About page's live waveform (no-op unless showing)
        }

        /// <summary>UI-thread handler for a taskbar column toggle.</summary>
        private void ToggleDetail(TaskbarWindow source, int column)
        {
            if (source == null || !_taskbars.Contains(source)) return;
            CloseDetail();
            if (column < 0) return;

            var existing = _pinned.Find(w => w.Column == column);
            if (existing != null)
            {
                source.RequestDeselect(column);
                existing.Activate();
                var existingHwnd = new WindowInteropHelper(existing).Handle;
                if (existingHwnd != IntPtr.Zero) WindowInterop.SetForegroundWindow(existingHwnd);
                return;
            }

            _detail = new DetailWindow(source, _sampling);
            _detail.PinStateChanged += OnDetailPinStateChanged;
            _detail.SizeChanged += (s, e) => ScheduleDetailKeepOut();
            _detail.LocationChanged += (s, e) => ScheduleDetailKeepOut();
            _detail.Closed += (s, e) =>
            {
                var w = (DetailWindow)s;
                if (ReferenceEquals(_detail, w)) _detail = null;
                _pinned.Remove(w);
                _taskbars.SetColumnClickEnabled(w.Column, true);
                ScheduleDetailKeepOut();
                ScheduleIdleTrim();
            };
            _detail.ShowColumn(column);
            ScheduleDetailKeepOut();
        }

        private void OnAnchorRemoved(TaskbarWindow source)
        {
            if (_detail?.Anchor == source) CloseDetail();
            foreach (var window in _pinned) window.DetachAnchor(source);
        }

        // A window that pins itself leaves the transient slot (so a new popup may open
        // alongside it); unpinning makes it the transient popup again. Unpinning can only
        // be clicked while that window has focus, which guarantees the transient slot is
        // empty by then (any focus shift closes a transient flyout). While a window is
        // pinned its overlay column is press-disabled (hover retained) — the pinned
        // window owns the column until it is unpinned or closed.
        private void OnDetailPinStateChanged(DetailWindow w, bool pinned)
        {
            if (pinned)
            {
                if (ReferenceEquals(_detail, w)) _detail = null;

                if (!_pinned.Contains(w)) _pinned.Add(w);

                // 固定窗上限 3 个（每个都是整页 WPF 树：图表 + 进程列表 + 图标缓存），
                // 超出时按"最早固定"的顺序关掉最旧的那个。加上 1 个未固定的临时窗，
                // 屏幕上最多同时 4 个详情窗。
                while (_pinned.Count > MaxPinnedDetails)
                {
                    var oldest = _pinned[0];
                    if (ReferenceEquals(oldest, w)) break;      // 新固定的自己不动
                    _pinned.RemoveAt(0);
                    Logger.Info($"固定窗超过 {MaxPinnedDetails} 个——自动关闭最早的那个");
                    try { oldest.Close(); } catch { }
                }

                _taskbars.SetColumnClickEnabled(w.Column, false);
            }
            else
            {
                _pinned.Remove(w);
                _detail = w;
                _taskbars.SetColumnClickEnabled(w.Column, true);
            }
            // Pin/unpin moves the window (the band grows out of the top; unpin restores the
            // pre-pin spot) — a different keep-out for the floating widget either way.
            ScheduleDetailKeepOut();
        }

        // ---------- 悬浮模式: the detail windows travel with the widget ----------
        // The widget is being dragged, one step at a time (TaskbarWindow.FloatingDragged). Every
        // open detail window is anchored to it — the flyout beside its column, a pinned window
        // wherever the user put it — so they all move by the same delta and the group stays
        // together. (A pinned window survives the drag by definition; the transient flyout
        // survives it too whenever 置顶显示 is on, because the widget is WS_EX_NOACTIVATE and
        // therefore never takes focus away from it. With 置顶显示 off, the click that starts the
        // drag activates the widget and the flyout dismisses itself, as any focus loss does.)
        private void FollowFloatingDrag(int dx, int dy)
        {
            _detail?.FollowFloatingDrag(dx, dy);
            foreach (var w in _pinned) w.FollowFloatingDrag(dx, dy);
        }

        // ---------- 悬浮模式: keep the widget out from under a detail window ----------
        // The widget's home is where the user dragged it, but a detail window can end up
        // covering that spot: its placement is anchored to the widget's column and CLAMPED into
        // the monitor work area, so on a screen too short to hold the popup beside the widget
        // the popup lands on top of it (and pinning grows the window further up). Rather than
        // re-place the popup, the widget steps aside — TaskbarWindow.ComputeFloatTarget — and
        // goes back home once no open window covers it. App owns the window LISTS, so it
        // publishes them; the taskbar thread reads the rects itself (and re-derives every tick,
        // which is what makes a change that raised no WPF event still land).
        //
        // Deferred AND coalesced: toggling a column closes the old popup and opens the new one
        // inside one dispatcher pass, so a synchronous push on the close would send the widget
        // home for a few ms and dodge it right back out (a visible blink). Background priority
        // also puts the push after the layout/render pass, i.e. after the new window's
        // placement has actually reached its HWND.
        private bool _keepOutQueued;

        private void ScheduleDetailKeepOut()
        {
            if (_keepOutQueued) return;
            _keepOutQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _keepOutQueued = false;
                var hwnds = new List<IntPtr>(_pinned.Count + 1);
                AddKeepOut(_detail, hwnds);
                foreach (var w in _pinned) AddKeepOut(w, hwnds);
                _taskbars.SetFloatingKeepOut(hwnds.ToArray());
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static void AddKeepOut(DetailWindow w, List<IntPtr> into)
        {
            if (w == null) return;
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero) into.Add(hwnd);
        }

        private void CloseDetail()
        {
            var d = _detail;
            _detail = null;
            d?.Close();
        }

        /// <summary>把一次内存回收排到 UI 空闲时执行（异步；30 秒节流在 SystemInfo 里）。</summary>
        private void TrimMemorySoon(string reason)
        {
            if (_stopping) return;        // 退出中排回收没有意义，且可能在 dispatcher 关闭后投递
            Dispatcher.BeginInvoke(new Action(() => SystemInfo.TrimMemory(reason)),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 最后一个 WPF 窗口关闭后回收一次 —— 否则那棵 UI 树（控件、图表、亚克力资源）
        /// 会一直挂在内存里等下一次 GC，而进程闲着时可能永远等不到。
        /// 延到 Background：Closed 是在 ToggleDetail 中途同步触发的，当场检查会误判"全关"。
        /// </summary>
        private void ScheduleIdleTrim()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_detail != null || _pinned.Count != 0 || _settings != null) return;
                SystemInfo.TrimMemory("窗口全关");
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ---------- 卡死看门狗 (the reported 「进程卡死了…又恢复了」) ----------
        // SamplingController and every overlay have independent heartbeats. The UI-thread
        // watchdog checks each one because a stalled thread cannot report itself; waiting
        // for a selected taskbar or deliberately stopping an instance is not a zombie.
        private sealed class OverlayHealthState
        {
            public long StallLastTick;
            public long GoneSinceTick;
            public bool GoneLogged;
        }

        private System.Windows.Threading.DispatcherTimer _overlayWatch;
        private const int WatchIntervalMs = 2000;
        private readonly Dictionary<TaskbarWindow, OverlayHealthState> _overlayHealth = new Dictionary<TaskbarWindow, OverlayHealthState>();
        private readonly OverlayHealthState _samplingHealth = new OverlayHealthState();
        private bool _restartedForZombie;

        private void StartOverlayWatchdog()
        {
            _overlayWatch = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(WatchIntervalMs) };
            _overlayWatch.Tick += (s, e) => CheckOverlayHealth();
            _overlayWatch.Start();
        }

        private void CheckOverlayHealth()
        {
            if (_stopping) return;
            long now = (long)SystemInfo.GetTickCount64();
            CheckHeartbeat("采样", _sampling.LastTickTickCount, _sampling.NativeThreadId, _samplingHealth, now);

            var windows = _taskbars?.GetWatchdogWindows() ?? new List<TaskbarWindow>();
            var current = new HashSet<TaskbarWindow>(windows);
            foreach (var window in windows)
            {
                if (!_overlayHealth.TryGetValue(window, out var state))
                {
                    state = new OverlayHealthState();
                    _overlayHealth[window] = state;
                }
                CheckHeartbeat("覆盖层", window.LastTickTickCount, window.NativeThreadId, state, now);
                CheckZombie(window, state, now);
            }
            var stale = new List<TaskbarWindow>();
            foreach (var window in _overlayHealth.Keys)
            {
                if (!current.Contains(window)) stale.Add(window);
            }
            foreach (var window in stale) _overlayHealth.Remove(window);
        }

        private void CheckHeartbeat(string kind, long last, int nativeThreadId, OverlayHealthState state, long now)
        {
            if (last == 0) return;
            int interval = _config.SampleIntervalMs ?? 1000;
            long threshold = Math.Max(4000, interval * 3L);
            if (state.StallLastTick == 0)
            {
                if (now - last > threshold)
                {
                    state.StallLastTick = last;
                    Logger.Warn($"{kind}心跳停滞：已 {now - last}ms 没有 tick（阈值 {threshold}ms，线程 {nativeThreadId}）；当前步骤={CrashTrace.CurrentStepFor(nativeThreadId)}，最近消息=[{CrashTrace.RecentMessagesFor(nativeThreadId)}]，线程栈={CrashTrace.CaptureStackOf(nativeThreadId)}");
                }
            }
            else if (now - last <= interval * 2L)
            {
                Logger.Warn($"{kind}心跳恢复：本次停滞约 {now - state.StallLastTick}ms");
                state.StallLastTick = 0;
            }
        }

        private void CheckZombie(TaskbarWindow window, OverlayHealthState state, long now)
        {
            IntPtr hwnd = window.OverlayHwnd;
            bool there = hwnd != IntPtr.Zero && WindowInterop.IsWindow(hwnd);
            if (!there)
            {
                if (state.GoneSinceTick == 0) state.GoneSinceTick = now;
                else if (!state.GoneLogged && now - state.GoneSinceTick > 5000)
                {
                    state.GoneLogged = true;
                    Logger.Error($"覆盖层窗口已消失 {(now - state.GoneSinceTick) / 1000}s（线程 {window.NativeThreadId}）——先唤醒消息循环，45s 后仍无恢复将自动重启进程");
                    if (window.NativeThreadId > 0)
                        WindowInterop.PostThreadMessageW((uint)window.NativeThreadId, WindowInterop.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
                if (!_restartedForZombie && now - state.GoneSinceTick > 45000)
                    RestartForZombie(now - state.GoneSinceTick);
            }
            else
            {
                if (state.GoneLogged)
                    Logger.Info($"覆盖层窗口已恢复（消失约 {now - state.GoneSinceTick}ms）");
                state.GoneSinceTick = 0;
                state.GoneLogged = false;
            }
        }

        private void RestartForZombie(long goneMs)
        {
            _restartedForZombie = true;
            Logger.Error($"覆盖层窗口消失 {goneMs / 1000}s——判定为不可恢复卡死，自动重启进程");
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
                Logger.ReleaseHandle();
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                });
            }
            catch (Exception ex) { Logger.Error("自动重启：拉起新进程失败", ex); }
            Environment.Exit(0);
        }

        // ---------- Right-click context menu (悬浮模式 / 置顶显示 / 设置 / Exit) ----------
        private ContextMenu _taskbarMenu;
        private MenuItem _menuFloating;
        private MenuItem _menuFloatingTopmost;
        private MenuItem _menuFloatingEdgeHide;
        private MenuItem _menuFloatingFullscreenHide;
        private MenuItem _menuCompact;
        private Window _menuHost;
        // The settings window — at most one; a second menu click re-activates it.
        private SettingsWindow _settings;

        private void ShowTaskbarMenu()
        {
            EnsureTaskbarMenu();
            // WPF Popup chooses its initial DPI from PlacementTarget's location, even with
            // Placement=MousePoint. Move the invisible host to the cursor in physical pixels
            // and flush the resulting WM_DPICHANGED before the menu creates its popup HWND.
            if (WindowInterop.GetCursorPos(out var cursor))
            {
                var hwnd = new WindowInteropHelper(_menuHost).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    WindowInterop.SetWindowPos(hwnd, IntPtr.Zero, cursor.x, cursor.y, 0, 0,
                        WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOZORDER | WindowInterop.SWP_NOACTIVATE);
                    _menuHost.UpdateLayout();
                }
            }
            // 打开前先把菜单量到最终尺寸：WPF 的 Popup 如果没有预先 Arrange，会先以极小尺寸
            // 创建窗口再撑开，VM 下就可能留下那一帧的边框残影。
            try
            {
                _taskbarMenu.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                var want = _taskbarMenu.DesiredSize;
                if (want.Width > 0 && want.Height > 0)
                    _taskbarMenu.Arrange(new System.Windows.Rect(0, 0, want.Width, want.Height));
            }
            catch { /* 量不出来就按原样弹，不影响功能 */ }

            _menuHost.Activate();            // make it the active window so deactivation closes the menu
            _taskbarMenu.IsOpen = true;      // Placement=MousePoint → opens at the cursor

            // 菜单弹出后把 1×1 宿主挪回屏幕外：它刚才为了「按光标 DPI 定位」被放到了鼠标点上，
            // 而它又是**当前活动窗口** —— Windows 会给活动窗口描一圈强调色边框，于是在点击处
            // 留下一个 1px 的小蓝方块（2026-11-16 Win10 虚拟机实测：跟着点击位置走、
            // 颜色是系统蓝、菜单一关就消失）。
            // SWP_NOACTIVATE 保证移动不会改变激活状态，"点别处关闭菜单"的逻辑不受影响。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var hwnd2 = new WindowInteropHelper(_menuHost).Handle;
                    if (hwnd2 != IntPtr.Zero)
                        WindowInterop.SetWindowPos(hwnd2, IntPtr.Zero, -10000, -10000, 0, 0,
                            WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOZORDER
                            | WindowInterop.SWP_NOACTIVATE);

                    // 还有一层：菜单弹窗自己在「光标处」创建过初始窗口，VM 里 DWM 来不及重绘，
                    // 那块会留下一个 1px 强调色边框残影（形如小蓝方块，跟着点击位置走）。
                    // 请点击处的窗口（任务栏 / 我们自己的覆盖层）把那一小块重画一次即可清除。
                    if (WindowInterop.GetCursorPos(out var pt))
                    {
                        var target = WindowInterop.WindowFromPoint(pt);
                        if (target != IntPtr.Zero)
                        {
                            var r = new WindowInterop.RECT
                            {
                                left = pt.x - 16, top = pt.y - 16,
                                right = pt.x + 16, bottom = pt.y + 16,
                            };
                            WindowInterop.InvalidateRect(target, ref r, true);
                        }
                    }
                }
                catch { /* 退出中：宿主/任务栏已销毁，忽略 */ }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static bool SupportsSnapToStart(IReadOnlyList<TaskbarDisplayInfo> displays,
            IReadOnlyList<TaskbarDisplaySelection> selection)
        {
            bool matched = false;
            foreach (var display in displays)
            {
                bool use = selection == null ? display.IsPrimary : IsDisplaySelected(selection, display.Id);
                if (!use) continue;
                matched = true;
                if (display.HasTaskbar && TaskbarWindow.IsWindows11Taskbar(display.TaskbarHwnd)) return true;
            }
            if (matched) return false;
            var primary = TaskbarDisplayCatalog.GetPrimaryDisplay();
            return primary != null && primary.HasTaskbar && TaskbarWindow.IsWindows11Taskbar(primary.TaskbarHwnd);
        }

        private static bool IsDisplaySelected(IReadOnlyList<TaskbarDisplaySelection> selection, string id)
        {
            foreach (var display in selection)
            {
                if (display != null && string.Equals(display.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void ShowSettings()
        {
            if (_settings != null)
            {
                _settings.Activate();
                return;
            }
            var displays = TaskbarDisplayCatalog.GetDisplays();
            _settings = new SettingsWindow(
                _config.OverlayOnLeft != false, AnchorIndexOf(_config), OnOverlayPlacementChanged,
                SupportsSnapToStart(displays, _config.TaskbarDisplays), displays, _config.TaskbarDisplays, OnTaskbarDisplaysChanged,
                StartupTask.IsEnabledForCurrentExe(), OnAutoStartChanged,
                ThemeIndexOf(_config.Theme), OnThemeChanged,
                _config.CompactLayout == true, OnCompactLayoutChanged,
                _config.ShowSeparators != false, OnShowSeparatorsChanged,
                _config.ShowNetMonthUsage == true, OnShowNetMonthUsageChanged,
                _config.ShowNetTodayUsage == true, OnShowNetTodayUsageChanged,
                _config.NetUsageFixedGb == true, OnNetUsageFixedGbChanged,
                _config.NetHistoryKeepMonths ?? NetHistory.DefaultKeepMonths, OnNetHistoryKeepMonthsChanged,
                _config.ShowNetHistory == true, OnShowNetHistoryChanged,
                _config.OverlayUsageGraph == true, OnOverlayUsageGraphChanged,
                _config.OverlayGraphColor, OnOverlayGraphColorChanged,
                _config.ItemOrder, OnItemOrderChanged,
                _config.FloatingMode == true, OnFloatingModeChanged,
                _config.FloatingTopmost != false, OnFloatingTopmostChanged,
                _config.FloatingAllowOutOfBounds == true, OnFloatingAllowOutOfBoundsChanged,
                _config.FloatingEdgeHide == true, OnFloatingEdgeHideChanged,
                _config.FloatingFullscreenHide != false, OnFloatingFullscreenHideChanged,
                _config.FloatingOpacity ?? 1.0, OnFloatingOpacityChanged,
                _config.SampleIntervalMs ?? 1000, OnSampleIntervalChanged,
                SamplingMaskOf(_config), OnMetricSamplingChanged,
                _config.MergeSamePathProcesses != false, OnMergeSamePathChanged,
                // The 特定磁盘 picker's items come from the latest snapshot's disk list
                // (empty while disk sampling is off — the picker then keeps only the
                // stored pick's （未连接） placeholder).
                DiskDisplayModeIndexOf(_config), _config.DiskDisplayIndex ?? 0,
                _sampling.LatestSnapshot?.Disks, OnDiskDisplayChanged,
                // Same for the 特定 GPU picker (empty while GPU sampling is off).
                GpuDisplayModeIndexOf(_config), _config.GpuDisplayIndex ?? 0,
                _sampling.LatestSnapshot?.Gpus, OnGpuDisplayChanged,
                // 任务栏显示 / 温度来源 / 温度单位 — the 温度来源 picker shares the same
                // adapter list.
                GpuTaskbarContentIndexOf(_config), _config.GpuTempIndex ?? -1,
                GpuTempUnitIndexOf(_config), OnGpuTaskbarContentChanged,
                _config.NetAdapterId, _config.NetAdapterName,
                EnumerateNetAdapters(), OnNetAdapterChanged,
                _config.PublicIpEnabled != false, OnPublicIpLookupChanged,
                _config.ClashEnabled != false, _config.ClashApiAddress, _config.ClashApiSecret, OnClashApiChanged,
                _config.UpdateCheckEnabled == true, OnUpdateCheckChanged,
                UpdateSourceIndexOf(_config), OnUpdateSourceChanged,
                // The About page's live waveform reads the same latest snapshot.
                () => _sampling.LatestSnapshot);
            _settings.Closed += (s, e) =>
            {
                _settings = null;
                ScheduleIdleTrim();
            };
            _settings.Show();
            _settings.Activate();
        }

        private void OnTaskbarCatalogChanged(IReadOnlyList<TaskbarDisplayInfo> displays)
        {
            Logger.Info($"显示器目录变化通知设置窗口：在线={displays.Count}，设置打开={_settings != null}");
            _settings?.RefreshDisplays(displays, _config.TaskbarDisplays);
        }

        private void OnTaskbarDisplaysChanged(IReadOnlyList<TaskbarDisplaySelection> selection)
        {
            var saved = new List<TaskbarDisplaySelection>();
            foreach (var display in selection)
                saved.Add(new TaskbarDisplaySelection { Id = display.Id, Name = display.Name });
            _config.TaskbarDisplays = saved;
            TrySaveConfig();
            CloseDetail();
            _taskbars.SetDisplaySelection(saved);
        }

        // 设置 外观 section's placement cards changed: persist, re-anchor the overlay, and
        // drop the transient flyout (its anchor just moved; pinned windows are user-placed
        // and stay where they are).
        private void OnOverlayPlacementChanged(bool onLeft, int anchor)
        {
            // 第三方任务栏下「贴近系统托盘」不可用：即使配置/旧版本把 anchor=2 带进来，也强制归 0。
            if (anchor == 2 && DetectThirdPartyTaskbar() != null)
            {
                anchor = 0;
                Logger.WarnOnce("anchor-right-blocked",
                    "第三方任务栏环境下「贴近系统托盘」不可用（会与它抢布局）——已强制改为最左端；"
                    + "需要自由摆放请改用悬浮模式");
            }
            _config.OverlayOnLeft = onLeft;
            _config.OverlayAnchor = anchor == 0 ? (int?)null : anchor;
            // 给首次启动向导做镜像：那边仍然用布尔值表达（是不是开始按钮旁）。
            _config.OverlaySnapToStart = anchor == 1 ? true : (bool?)null;
            TrySaveConfig();
            _taskbars.SetPlacement(onLeft, anchor);
            CloseDetail();
        }

        // 任务栏位置按索引存：0 = 最左端，1 = 贴近开始按钮，2 = 贴近系统托盘。
        // 没有这个键的旧配置只带了布尔值 overlaySnapToStart，那就拿它做回退。
        private static int AnchorIndexOf(AppSettings config)
        {
            int? stored = config.OverlayAnchor;
            if (stored.HasValue && stored.Value >= 0 && stored.Value <= 2) return stored.Value;
            return config.OverlaySnapToStart == true ? 1 : 0;
        }

        // 设置 外观 → 紧凑模式 (or the right-click menu's checkable item): persist (null =
        // off — only the enabled state is written) and hand the flip to the taskbar thread,
        // whose WM_APP_SET_METRICS resize re-lays the overlay out at once (cells, row pitch
        // and insets all change — in the floating form the card narrows with them). The
        // transient flyout is dropped like a placement change: its anchor column's centre
        // just moved. Pinned windows are user-placed and stay.
        private void OnCompactLayoutChanged(bool on)
        {
            _config.CompactLayout = on ? true : (bool?)null;
            TrySaveConfig();
            CloseDetail();
            _taskbars.SetCompactLayout(on);
            _settings?.SyncCompactLayout(on);
        }

        private void OnShowNetMonthUsageChanged(bool on)
        {
            _config.ShowNetMonthUsage = on ? true : (bool?)null;
            NetUsage.ShowMonth = on;      // NetDetailView 每个 tick 读它决定这一行显不显示
            TrySaveConfig();
        }

        private void OnShowNetTodayUsageChanged(bool on)
        {
            _config.ShowNetTodayUsage = on ? true : (bool?)null;
            NetUsage.ShowToday = on;
            TrySaveConfig();
        }

        private void OnNetUsageFixedGbChanged(bool on)
        {
            _config.NetUsageFixedGb = on ? true : (bool?)null;
            NetUsage.FixedGb = on;       // NetDetailView 每个 tick 读它决定用哪种单位
            TrySaveConfig();
        }

        /// <summary>
        /// 多月历史保留月数（设置 → 外观 → 流量历史保留）。改完立刻生效：下一次落盘
        /// （≤30 秒）就会按新值裁剪 net_history.json，不用重启。
        /// </summary>
        /// <summary>「显示往月流量」开关（设置 → 外观）。默认关；开与关只影响弹窗那一行。</summary>
        private void OnShowNetHistoryChanged(bool on)
        {
            _config.ShowNetHistory = on ? true : (bool?)null;
            NetUsage.ShowHistory = on;
            TrySaveConfig();
        }

        private void OnNetHistoryKeepMonthsChanged(int months)
        {
            int keep = NetHistory.NormalizeKeep(months);
            _config.NetHistoryKeepMonths = keep == NetHistory.DefaultKeepMonths ? (int?)null : keep;
            NetUsage.KeepMonths = keep;
            TrySaveConfig();
        }

        private void OnShowSeparatorsChanged(bool on)
        {
            _config.ShowSeparators = on ? (bool?)null : false;
            TrySaveConfig();
            _taskbars.SetShowSeparators(on);
            Logger.Info($"显示分割线{(on ? "开启" : "关闭")}——重绘所有覆盖层");
        }

        private void OnOverlayUsageGraphChanged(bool on)
        {
            _config.OverlayUsageGraph = on ? true : (bool?)null;
            TrySaveConfig();
            _taskbars.SetOverlayUsageGraph(on);
            Logger.Info($"已用走势{(on ? "开启" : "关闭")}——重绘所有覆盖层");
        }

        // 曲线颜色：当前已经下发给各表面的值，5 秒轮询靠它判断是否需要重发（相同就跳过）。
        // 每次设置变动都重新从 settings.yaml 解析；设置为空（跟随系统）时取系统强调色。
        private uint _graphRgb = 0x0078D4;
        private DispatcherTimer _graphColorWatch;

        private void OnOverlayGraphColorChanged(string hex)
        {
            _config.OverlayGraphColor = string.IsNullOrWhiteSpace(hex) ? null : hex;
            TrySaveConfig();
            ApplyGraphColor(ResolveGraphRgb());
            Logger.Info($"曲线颜色：{_config.OverlayGraphColor ?? "跟随系统"}（#{_graphRgb:X6}）");
        }

        // 把解析好的颜色同时推给两个表面，只在这里做：覆盖层的走势曲线，以及详情弹窗里手绘的
        // 图表（ChartPalette.Accent）。已打开的窗口通过各自的 ApplyTheme 重新读取 —— 和明暗主题
        // 切换走的是同一条路径，所以钉住的窗口也能实时换色，不用关掉重开。
        private void ApplyGraphColor(uint rgb)
        {
            _graphRgb = rgb;
            ChartPalette.Override = Color.FromRgb(
                (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
            _taskbars.SetOverlayGraphColor(rgb);
            _detail?.ApplyTheme();
            foreach (var w in _pinned) w.ApplyTheme();
        }

        // 跟随系统：用 5 秒定时器而不是 SystemEvents.UserPreferenceChanged —— 后者要一个隐藏窗口，
        // 而且并不是每次强调色变化都会触发；5 秒重读一次只有一个注册表打开的开销，
        // 换色延迟远在可接受范围内。
        private void StartGraphColorWatch()
        {
            _graphColorWatch = new DispatcherTimer(
                TimeSpan.FromSeconds(5), DispatcherPriority.Background,
                (_, __) => RefreshGraphColor(), Dispatcher);
            _graphColorWatch.Start();
        }

        private void RefreshGraphColor()
        {
            uint rgb = ResolveGraphRgb();
            if (rgb == _graphRgb) return;
            ApplyGraphColor(rgb);
            Logger.Info($"曲线颜色跟随系统强调色：#{rgb:X6}");
        }

        private uint ResolveGraphRgb()
        {
            uint rgb = ParseHexRgb(_config.OverlayGraphColor);
            return rgb != 0 ? rgb : SystemAccentRgb();
        }

        private static uint ParseHexRgb(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return 0;
            string s = hex.Trim().TrimStart('#');
            if (s.Length != 6) return 0;
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : 0;
        }

        // DWM 的 ColorizationColor 是 0xAARRGGBB，低 24 位本身就是 0xRRGGBB，直接取即可。
        private static uint SystemAccentRgb()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (key?.GetValue("ColorizationColor") is int v)
                    {
                        uint rgb = (uint)(v & 0x00FFFFFF);
                        if (rgb != 0) return rgb;
                    }
                }
            }
            catch { }
            return 0x0078D4;
        }

        private void OnItemOrderChanged(IReadOnlyList<int> order)
        {
            var normalized = TaskbarWindow.NormalizeItemOrder(order);
            _config.ItemOrder = TaskbarWindow.IsDefaultItemOrder(normalized) ? null : new List<int>(normalized);
            TrySaveConfig();
            CloseDetail();
            _taskbars.SetItemOrder(normalized);
        }

        // 设置 外观 → 悬浮模式 (or the right-click menu's checkable item): persist (null =
        // off — only the enabled state is written) and hand the flip to the taskbar thread,
        // which rebuilds the overlay window in its other form (top-level card widget instead
        // of a taskbar child, or back). That also moves the flyout's anchor, so drop the
        // transient popup; pinned windows are user-placed and stay. The saved home
        // (FloatingX/Y) rides along, so turning the mode back on restores where the user
        // last dragged the widget. An open settings window is synced back — a no-op when
        // the change came from there.
        private void OnFloatingModeChanged(bool on)
        {
            _config.FloatingMode = on ? true : (bool?)null;
            TrySaveConfig();
            CloseDetail();
            _taskbars.SetFloatingMode(on, _config.FloatingX, _config.FloatingY);
            _settings?.SyncFloatingMode(on);
        }

        // 设置 外观 → 悬浮模式 → 置顶显示 (or the right-click menu's checkable item): persist
        // (null = on, the default — only the disabled state is written). No rebuild: the
        // widget just changes z-order band, and the taskbar thread verifies + re-asserts the
        // promotion (the foreground lock can eat it).
        private void OnFloatingTopmostChanged(bool on)
        {
            _config.FloatingTopmost = on ? (bool?)null : false;
            TrySaveConfig();
            _taskbars.SetFloatingTopmost(on);
            _settings?.SyncFloatingTopmost(on);
        }

        private void OnFloatingAllowOutOfBoundsChanged(bool on)
        {
            _config.FloatingAllowOutOfBounds = on ? true : (bool?)null;
            TrySaveConfig();
            _taskbars.SetFloatingAllowOutOfBounds(on);
        }

        // 设置 外观 → 悬浮模式 → 贴边隐藏 (or the right-click menu's checkable item): persist
        // (null = off, the default — only the enabled state is written) and hand it to the
        // taskbar thread, which re-derives the dock from the home position — ON slides a
        // home-at-edge widget out at once, OFF slides a hidden/peeked one back home. The
        // dock itself is derived state (a home within the snap distance of a 左/右/上 work
        // edge), so nothing but the switch is stored.
        private void OnFloatingEdgeHideChanged(bool on)
        {
            _config.FloatingEdgeHide = on ? true : (bool?)null;
            TrySaveConfig();
            _taskbars.SetFloatingEdgeHide(on);
            _settings?.SyncFloatingEdgeHide(on);
        }

        // 设置 外观 → 悬浮模式 → 全屏时隐藏 (or the right-click menu's checkable item):
        // persist (null = on, the default — only the disabled state is written) and hand it
        // to the taskbar thread, whose probe runs at once — ON with a fullscreen foreground
        // hides the widget immediately, OFF restores it no matter what is on screen. The
        // fullscreen state itself is derived live by the tick; only the switch is stored.
        private void OnFloatingFullscreenHideChanged(bool on)
        {
            _config.FloatingFullscreenHide = on ? (bool?)null : false;
            TrySaveConfig();
            _taskbars.SetFloatingFullscreenHide(on);
            _settings?.SyncFloatingFullscreenHide(on);
        }

        // 设置 外观 → 悬浮模式 → 透明度: applied LIVE (a plain re-tint on the taskbar thread —
        // the widget must follow the thumb, so no debounce on the visual), but persisted
        // debounced: the slider reports per step and every step would rewrite settings.yaml
        // (the Clash text boxes debounce for the same reason — only the write is delayed
        // here, not the effect). null = 不透明 (only a non-default value is written).
        // Settings-page only: a percent slider has no right-click-menu shape.
        private void OnFloatingOpacityChanged(double opacity)
        {
            _taskbars.SetFloatingOpacity(opacity);
            _config.FloatingOpacity = opacity < 0.999 ? opacity : (double?)null;
            if (_configSaveDebounce == null)
            {
                _configSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _configSaveDebounce.Tick += (s, e) =>
                {
                    _configSaveDebounce.Stop();
                    TrySaveConfig();
                };
            }
            _configSaveDebounce.Stop();
            _configSaveDebounce.Start();
        }

        // The 透明度 slider's yaml-write debounce (see OnFloatingOpacityChanged). A fired
        // timer outlives the settings window on purpose: the last dragged value still lands.
        private DispatcherTimer _configSaveDebounce;

        /// <summary>上次把本月流量写进 settings.yaml 的时刻（Environment.TickCount64）。</summary>
        private long _netUsageSavedAt;

        // ---------- 设置: 开机自启动 / 主题 / 采样间隔 ----------

        // The scheduled task itself is the auto-start state (nothing in settings.yaml):
        // create/delete it on toggle; on a schtasks failure snap the toggle back to the
        // real state.
        private void OnAutoStartChanged(bool on)
        {
            if (StartupTask.SetEnabled(on)) return;
            _settings?.SyncAutoStart(StartupTask.IsEnabledForCurrentExe());
        }

        // 主题 combo: 0=跟随系统 (yaml null — the ThemeManager tracks the system theme
        // live) 1=浅色 2=深色. Persisted + pushed to the ThemeManager; the
        // ActualApplicationThemeChanged hook in OnStartup repaints open detail windows.
        private void OnThemeChanged(int index)
        {
            _config.Theme = index == 1 ? AppThemeSetting.Light
                          : index == 2 ? AppThemeSetting.Dark
                          : (AppThemeSetting?)null;
            TrySaveConfig();
            ApplyThemeSetting();
        }

        private void ApplyThemeSetting()
        {
            ThemeManager.Current.ApplicationTheme =
                _config.Theme == AppThemeSetting.Light ? ApplicationTheme.Light
                : _config.Theme == AppThemeSetting.Dark ? ApplicationTheme.Dark
                : (ApplicationTheme?)null;
        }

        private static int ThemeIndexOf(AppThemeSetting? theme)
            => theme == AppThemeSetting.Light ? 1 : theme == AppThemeSetting.Dark ? 2 : 0;

        // 采样间隔 combo: persist (1000ms stays implicit in the yaml) and re-arm the
        // taskbar overlay's timer.
        private void OnSampleIntervalChanged(int ms)
        {
            _config.SampleIntervalMs = ms == 1000 ? (int?)null : ms;
            TrySaveConfig();
            _taskbars.SetSampleInterval(ms);
        }

        // 合并相同程序 toggle: persist (null = on, the default — only the disabled state
        // is written) and push to the sampler. Open detail windows pick it up on the next
        // snapshot tick — the per-process lists rebuild every Refresh, so no window needs
        // closing.
        private void OnMergeSamePathChanged(bool on)
        {
            _config.MergeSamePathProcesses = on ? (bool?)null : false;
            TrySaveConfig();
            _taskbars.SetMergeSamePathProcesses(on);
        }

        // ---------- 设置 → 采样 → 磁盘: 显示方式 ----------

        // yaml DiskDisplay ↔ the settings combo index (0=所有磁盘平均 = yaml null, the
        // default — only non-default modes are written; 1=最高利用率 2=特定磁盘).
        private static int DiskDisplayModeIndexOf(AppSettings c)
            => c.DiskDisplay == MetricDisplayMode.Max ? 1 : c.DiskDisplay == MetricDisplayMode.Specific ? 2 : 0;

        // 显示方式 / 特定磁盘 combo changed: persist (null = 平均, the default; the picked
        // PhysicalDrive index is kept even when the mode leaves 特定磁盘, so switching back
        // restores it) and push to the taskbar thread. Open disk windows pick it up on the
        // next snapshot tick — the headline and chart rebuild every Refresh.
        private void OnDiskDisplayChanged(int modeIndex, int diskIndex)
        {
            _config.DiskDisplay = modeIndex == 1 ? MetricDisplayMode.Max
                                : modeIndex == 2 ? MetricDisplayMode.Specific
                                : (MetricDisplayMode?)null;
            _config.DiskDisplayIndex = diskIndex;
            TrySaveConfig();
            _taskbars.SetDiskDisplay(modeIndex, diskIndex);
        }

        // ---------- 设置 → 采样 → GPU: 显示方式 ----------

        // yaml GpuDisplay ↔ the settings combo index (1=最高利用率 = yaml null, the GPU
        // default — Task Manager's sidebar rule; 0=所有 GPU 平均 2=特定 GPU).
        private static int GpuDisplayModeIndexOf(AppSettings c)
            => c.GpuDisplay == MetricDisplayMode.Average ? 0 : c.GpuDisplay == MetricDisplayMode.Specific ? 2 : 1;

        // GPU 显示方式 / 特定 GPU combo changed — same contract as the disk one above
        // (the picked "GPU N" number is kept when the mode leaves 特定 GPU).
        private void OnGpuDisplayChanged(int modeIndex, int gpuIndex)
        {
            _config.GpuDisplay = modeIndex == 0 ? MetricDisplayMode.Average
                               : modeIndex == 2 ? MetricDisplayMode.Specific
                               : (MetricDisplayMode?)null;
            _config.GpuDisplayIndex = gpuIndex;
            TrySaveConfig();
            _taskbars.SetGpuDisplay(modeIndex, gpuIndex);
        }

        // ---------- 设置 → 采样 → GPU: 任务栏显示 / 温度来源 / 温度单位 ----------

        // yaml GpuTaskbarContent ↔ the 任务栏显示 combo index (0=使用率 = yaml null, the
        // default; 1=温度 2=使用率+温度).
        private static int GpuTaskbarContentIndexOf(AppSettings c)
            => c.GpuTaskbarContent == GpuTaskbarContent.Temperature ? 1
               : c.GpuTaskbarContent == GpuTaskbarContent.Both ? 2 : 0;

        // yaml GpuTempUnit ↔ the 温度单位 combo index (0=摄氏度 = yaml null, the default).
        private static int GpuTempUnitIndexOf(AppSettings c)
            => c.GpuTempUnit == GpuTempUnit.Fahrenheit ? 1 : 0;

        // 任务栏显示 / 温度来源 / 温度单位 combo changed: persist (the pick ≥0 and the
        // unit are KEPT when the content returns to 使用率, so switching back restores
        // them — same keep rule as GpuDisplayIndex) and push to the taskbar thread.
        // SetGpuTempDisplay posts WM_APP_SET_METRICS — unlike 显示方式 — because the row
        // count (and so the overlay's layout width) changes with the content.
        private void OnGpuTaskbarContentChanged(int contentIndex, int tempIndex, bool fahrenheit)
        {
            var content = contentIndex == 1 ? GpuTaskbarContent.Temperature
                        : contentIndex == 2 ? GpuTaskbarContent.Both
                        : GpuTaskbarContent.Usage;
            _config.GpuTaskbarContent = content == GpuTaskbarContent.Usage ? (GpuTaskbarContent?)null : content;
            _config.GpuTempIndex = tempIndex >= 0 ? tempIndex : (int?)null;   // -1/absent = 最高温度
            _config.GpuTempUnit = fahrenheit ? GpuTempUnit.Fahrenheit : (GpuTempUnit?)null;
            TrySaveConfig();
            _taskbars.SetGpuTempDisplay(content, tempIndex, fahrenheit);
        }

        // ---------- 设置 → 采样 → 网络: 适配器 ----------

        // The 适配器 combo's items: every non-loopback adapter (virtual ones included —
        // an explicit pick is exactly how the user watches a VPN/vEthernet adapter),
        // sorted by display name. Enumerated HERE on settings open: GetAllNetworkInterfaces()
        // is expensive (~275ms on machines with many virtual adapters), so it must never
        // run on the sampler's per-tick path — a one-time cost per settings open is fine.
        private static List<(string Id, string Label)> EnumerateNetAdapters()
        {
            var adapters = new List<(string Id, string Label)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    adapters.Add((nic.Id, $"{nic.Description} ({nic.Name})"));
                }
                adapters.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
            }
            catch (Exception ex) { Logger.Warn("网络适配器枚举失败", ex); }
            return adapters;
        }

        // 适配器 combo changed: persist (null/"" = 自动 — only a pinned pick is written,
        // with its display name for the picker's （未连接） placeholder) and push to the
        // taskbar thread. A pinned adapter that's gone falls back to 自动 inside
        // NetSampler; open network windows follow on the next snapshot tick.
        private void OnNetAdapterChanged(string id, string name)
        {
            _config.NetAdapterId = string.IsNullOrEmpty(id) ? null : id;
            _config.NetAdapterName = string.IsNullOrEmpty(id) ? null : name;
            TrySaveConfig();
            _taskbars.SetNetAdapter(_config.NetAdapterId);
        }

        // Clash/Mihomo integration changed (the switch reports immediately, text edits are
        // debounced by the settings page): persist (enabled null = on — only the disabled
        // state is written; null/"" address = the 127.0.0.1:9090 default, kept across the
        // switch so turning back on restores it) and push to the taskbar thread;
        // ClashSampler's poll thread retargets/idles on the change, and the Network
        // list's "Clash"-tagged rows appear/decay on their own.
        private void OnClashApiChanged(bool enabled, string address, string secret)
        {
            _config.ClashEnabled = enabled ? (bool?)null : false;
            _config.ClashApiAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
            _config.ClashApiSecret = _config.ClashApiAddress == null || string.IsNullOrEmpty(secret)
                ? null : secret;
            TrySaveConfig();
            _taskbars.SetClashApi(enabled, _config.ClashApiAddress, _config.ClashApiSecret);
        }

        // ---------- 设置 → 采样 → 网络: 公网 IP ----------

        // 公网 IP toggle: persist (null = on, the default — only the disabled state is
        // written) and push to the taskbar thread; NetInfoSampler's poll thread drops its
        // cached address and stops BOTH the HTTP lookups and the 公网延迟 ICMP probe, so
        // the Network panel's 公网 IPv4 / 公网延迟 cells go "—" (the v6 row collapses) on
        // the next snapshot tick — and resume immediately when turned back on.
        private void OnPublicIpLookupChanged(bool on)
        {
            _config.PublicIpEnabled = on ? (bool?)null : false;
            TrySaveConfig();
            _taskbars.SetPublicIpLookup(on);
        }

        // ---------- 设置 → 通用: 检查更新 / 更新源 ----------

        // yaml UpdateSource ↔ the settings combo index (0=CNB = yaml null, the default —
        // only "github" is ever written; 1=GitHub).
        private static int UpdateSourceIndexOf(AppSettings c)
            => string.Equals(c.UpdateSource, "github", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        // 检查更新 toggle: persist (null = on, the default — only the disabled state is
        // written). Takes effect on the next startup (the check runs once per launch).
        private void OnUpdateCheckChanged(bool on)
        {
            _config.UpdateCheckEnabled = on ? (bool?)true : false;
            TrySaveConfig();
        }

        // 更新源 combo: persist (null = CNB, the default — only "github" is written).
        private void OnUpdateSourceChanged(int index)
        {
            _config.UpdateSource = index == 1 ? "github" : null;
            TrySaveConfig();
        }

        // ---------- 设置 → 采样: per-metric sampling switches ----------

        // 设置 → 采样 toggles ↔ the SystemSampler mask (one bit per overlay hit slot;
        // a null yaml value means enabled, so only "off" is ever written). The settings
        // page keeps at least one metric on; CPU is the fallback for a hand-edited all-off
        // config, so neither the sampler nor the overlay can receive an empty mask.
        private static int SamplingMaskOf(AppSettings c)
        {
            int mask = 0;
            if (c.CpuSamplingEnabled != false) mask |= SystemSampler.MaskCpu;
            if (c.RamSamplingEnabled != false) mask |= SystemSampler.MaskRam;
            if (c.DiskSamplingEnabled != false) mask |= SystemSampler.MaskDisk;
            if (c.GpuSamplingEnabled != false) mask |= SystemSampler.MaskGpu;
            if (c.NetSamplingEnabled != false) mask |= SystemSampler.MaskNet;
            return mask == 0 ? SystemSampler.MaskCpu : mask;
        }

        // A metric expander's toggle flipped: persist (null = enabled), push the new mask
        // to the taskbar thread (the slot hides and the overlay reflows, its press is
        // suppressed) and close that column's open windows — they have no live data until
        // re-enabled. The Closed handlers do their usual cleanup (unpin mask, idle GC)
        // on the way out.
        private void OnMetricSamplingChanged(int slot, bool on)
        {
            switch (slot)
            {
                case 0: _config.CpuSamplingEnabled = on ? (bool?)null : false; break;
                case 1: _config.RamSamplingEnabled = on ? (bool?)null : false; break;
                case 2: _config.DiskSamplingEnabled = on ? (bool?)null : false; break;
                case 3: _config.GpuSamplingEnabled = on ? (bool?)null : false; break;
                case 4: _config.NetSamplingEnabled = on ? (bool?)null : false; break;
                default: return;
            }
            TrySaveConfig();
            _taskbars.SetMetricSamplingMask(SamplingMaskOf(_config));
            if (on) return;
            if (_detail?.Column == slot) CloseDetail();
            foreach (var w in _pinned.FindAll(w => w.Column == slot)) w.Close();
        }

        private void EnsureTaskbarMenu()
        {
            if (_taskbarMenu != null) return;

            // The context menu uses iNKORE.UI.WPF.Modern's Fluent 2 styles for the
            // standard WPF ContextMenu/MenuItem (merged via ui:XamlControlsResources in
            // App.xaml — the same look as iNKORE's MenuFlyout). They're applied by key
            // here because the FluentWpfCore resource dictionary is merged *after*
            // XamlControlsResources and would otherwise win for the keyless defaults.
            // 悬浮模式/置顶显示/贴边隐藏/全屏时隐藏 mirror the 设置 → 外观 switches
            // (checkable items funnelling into the same change handlers; a checkable
            // MenuItem flips IsChecked before Click fires, so the handler reads the new
            // state). Their checkmarks are refreshed on every open below — the settings
            // window can change them too.
            _menuFloating = new MenuItem
            {
                Header = "悬浮模式",
                IsCheckable = true,
                Icon = new FontIcon { Icon = FluentSystemIcons.WindowMultiple_16_Regular, FontSize = 16 },
            };
            _menuFloating.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            _menuFloating.Click += (s, e) => OnFloatingModeChanged(_menuFloating.IsChecked);

            _menuFloatingTopmost = new MenuItem
            {
                Header = "置顶显示",
                IsCheckable = true,
                Icon = new FontIcon { Icon = FluentSystemIcons.Pin_16_Regular, FontSize = 16 },
            };
            _menuFloatingTopmost.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            _menuFloatingTopmost.Click += (s, e) => OnFloatingTopmostChanged(_menuFloatingTopmost.IsChecked);

            _menuFloatingEdgeHide = new MenuItem
            {
                Header = "贴边隐藏",
                IsCheckable = true,
                Icon = new FontIcon { Icon = FluentSystemIcons.PanelLeftContract_16_Regular, FontSize = 16 },
            };
            _menuFloatingEdgeHide.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            _menuFloatingEdgeHide.Click += (s, e) => OnFloatingEdgeHideChanged(_menuFloatingEdgeHide.IsChecked);

            _menuFloatingFullscreenHide = new MenuItem
            {
                Header = "全屏时隐藏",
                IsCheckable = true,
                Icon = new FontIcon { Icon = FluentSystemIcons.FullScreenMinimize_16_Regular, FontSize = 16 },
            };
            _menuFloatingFullscreenHide.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            _menuFloatingFullscreenHide.Click += (s, e) => OnFloatingFullscreenHideChanged(_menuFloatingFullscreenHide.IsChecked);

            // 紧凑模式 mirrors the 设置 → 外观 switch — unlike the four above it applies to
            // BOTH forms (taskbar overlay and floating widget), so it never greys out. It
            // sits in its own separator-bounded section: the four 悬浮模式-related items are
            // one group, 设置/退出 below are the app actions, and this one stands alone
            // between them (it belongs to neither group).
            _menuCompact = new MenuItem
            {
                Header = "紧凑模式",
                IsCheckable = true,
                // Same glyph as the settings card's HeaderIcon; the icon set ships no _16_
                // variant of TextColumnOneNarrow, so the 20-px glyph renders at the menu's
                // 16 px like its siblings (font glyphs are vector — scaling is lossless).
                Icon = new FontIcon { Icon = FluentSystemIcons.TextColumnOneNarrow_20_Regular, FontSize = 16 },
            };
            _menuCompact.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            _menuCompact.Click += (s, e) => OnCompactLayoutChanged(_menuCompact.IsChecked);

            Separator NewSeparator()
            {
                var s = new Separator();
                s.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemSeparatorStyle");
                return s;
            }

            var settings = new MenuItem
            {
                Header = "设置",
                Icon = new FontIcon { Icon = FluentSystemIcons.Settings_16_Regular, FontSize = 16 },
            };
            settings.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            settings.Click += (s, e) => ShowSettings();

            var exit = new MenuItem
            {
                Header = "退出",
                Icon = new FontIcon { Icon = FluentSystemIcons.DoorArrowLeft_16_Regular, FontSize = 16 },
            };
            exit.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            exit.Click += (s, e) => BeginShutdown();

            _taskbarMenu = new ContextMenu
            {
                // 这三项是给「Win10 虚拟机里右键时点击处闪一下强调色小方块」用的（2026-11-16）：
                // - HasDropShadow=false：WPF 默认会在弹窗四周留一圈透明边做阴影，VM 的 DWM
                //   来不及重绘，那一圈会先被画成一条边框，看起来就是个小方块；
                // - 打开前先 Measure（见 ShowTaskbarMenu），避免「先 1×1 再撑开」。
                // ContextMenu 没有 PopupAnimation 属性（那是 WinUI 的），WPF 侧只关阴影即可。
                HasDropShadow = false,
            };

            // **右键点菜单项不该等于「选中」**：WPF 的 MenuItem 左右键都会触发 Click，
            // 用户在弹开的菜单上再右键一下，就会执行到那一项 —— 实测会直接「退出」
            // （2026-11-16 反馈：右键弹菜单、再右键点，程序退出了，像是左右键不分）。
            // Win32 原生菜单里右键不动作，这里对齐同样的行为：菜单层把右键吃掉，
            // 不让它落到 MenuItem 上。左键点击、键盘操作、点外面关闭都不受影响。
            _taskbarMenu.PreviewMouseRightButtonDown += (s, e) => e.Handled = true;
            _taskbarMenu.PreviewMouseRightButtonUp += (s, e) => e.Handled = true;
            _taskbarMenu.SetResourceReference(FrameworkElement.StyleProperty, "DefaultContextMenuStyle");
            _taskbarMenu.Items.Add(_menuFloating);
            _taskbarMenu.Items.Add(_menuFloatingTopmost);
            _taskbarMenu.Items.Add(_menuFloatingEdgeHide);
            _taskbarMenu.Items.Add(_menuFloatingFullscreenHide);
            _taskbarMenu.Items.Add(NewSeparator());
            _taskbarMenu.Items.Add(_menuCompact);
            _taskbarMenu.Items.Add(NewSeparator());
            _taskbarMenu.Items.Add(settings);
            _taskbarMenu.Items.Add(exit);

            // Reflect the live state on every open: 置顶显示/贴边隐藏 only mean anything in the
            // floating form, so they grey out with the mode — the same rule as the settings
            // page's FloatingTopmostCard/FloatingEdgeHideCard. 紧凑模式 applies to both forms
            // and stays enabled. (Programmatic IsChecked sets don't fire Click.)
            _taskbarMenu.Opened += (s, e) =>
            {
                bool isFloating = _config.FloatingMode == true;
                _menuFloating.IsChecked = isFloating;
                _menuFloatingTopmost.IsChecked = _config.FloatingTopmost != false;
                _menuFloatingTopmost.IsEnabled = isFloating;
                _menuFloatingEdgeHide.IsChecked = _config.FloatingEdgeHide == true;
                _menuFloatingEdgeHide.IsEnabled = isFloating;
                _menuFloatingFullscreenHide.IsChecked = _config.FloatingFullscreenHide != false;
                _menuFloatingFullscreenHide.IsEnabled = isFloating;
                _menuCompact.IsChecked = _config.CompactLayout == true;
            };

            // There is no main window, so the menu needs an invisible host window to attach to.
            _menuHost = new Window
            {
                Width = 1, Height = 1,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000,
            };
            _menuHost.SourceInitialized += (s, e) =>
            {
                // WS_EX_TOOLWINDOW keeps the invisible host out of Alt+Tab
                // (ShowInTaskbar=false alone only hides the taskbar button).
                var hwnd = new WindowInteropHelper(_menuHost).Handle;
                var ex = (uint)WindowInterop.GetWindowLongPtr(hwnd, WindowInterop.GWL_EXSTYLE);
                WindowInterop.SetWindowLongPtr(hwnd, WindowInterop.GWL_EXSTYLE,
                    new IntPtr(ex | WindowInterop.WS_EX_TOOLWINDOW));
            };
            _menuHost.Show();
            _taskbarMenu.PlacementTarget = _menuHost;
            _taskbarMenu.Placement = PlacementMode.MousePoint;

            // The host must be activated for "click elsewhere → close" to work.
            // When it deactivates (user clicked another window), close the menu.
            _menuHost.Deactivated += (s, e) => _taskbarMenu.IsOpen = false;
        }
    }
}
