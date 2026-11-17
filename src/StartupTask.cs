using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace task_monitor
{
    /// <summary>
    /// 开机自启动 via a Task Scheduler logon task (NOT the Run registry key). The whole
    /// point is avoiding a UAC prompt at every boot: the app always runs elevated, and an
    /// elevated Run-key/console launch would prompt — a scheduled task registered with
    /// <c>RunLevel=HighestAvailable</c> from an ALREADY-elevated process starts elevated
    /// at logon silently. The app is always elevated by design (App.RunElevationGate), so
    /// registration always has the rights it needs.
    ///
    /// The task is registered from a generated XML (<c>schtasks /Create /XML</c>) rather
    /// than command-line switches because the schtasks defaults are wrong for an always-on
    /// GUI app, and the switches can't change them:
    ///   - <c>ExecutionTimeLimit</c> defaults to 72h — Task Scheduler would KILL the
    ///     overlay after three days of uptime; the XML sets PT0S (never).
    ///   - <c>DisallowStartIfOnBatteries</c>/<c>StopIfGoingOnBatteries</c> default to
    ///     true — a laptop on battery would silently not auto-start; both false here.
    /// The principal is the current user by SID with <c>InteractiveToken</c> (a GUI
    /// overlay can't run S4U), and the logon trigger is pinned to that same user.
    ///
    /// State is NOT duplicated in settings.yaml — the task itself is the source of
    /// truth: <see cref="IsEnabled"/> queries it, <see cref="SetEnabled"/> creates
    /// (overwrite, so a moved exe is re-pointed on every re-enable) or deletes it.
    /// </summary>
    internal static class StartupTask
    {
        private const string TaskName = "TaskMonitor";

        /// <summary>True when the logon task exists (any state — disabled tasks still count
        /// as "on": we never create one disabled).</summary>
        public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

        /// <summary>
        /// 「这份程序会不会开机自启」—— 任务存在、且它指向的就是当前这个 exe。
        ///
        /// 为什么要区分：任务计划是**整机唯一**的，不区分副本。用户从另一个文件夹跑一份新的，
        /// 旧任务还在但指向老目录 —— 只看 IsEnabled() 会显示「开」，可它启动的并不是这一份，
        /// 界面上就成了「我没调过，怎么是开的」（2026-11-16 反馈）。
        /// 读不到登记路径时保守地按「开」处理，避免把用户已开启的自启显示成关。
        /// </summary>
        public static bool IsEnabledForCurrentExe()
        {
            // 从压缩包临时目录里运行：这种「自启」本身就不可信（临时目录会被清理），
            // 一律按「关」显示，避免设置页显示一个下一秒就会失效的「开」。
            if (IsRunningFromTransientLocation()) return false;
            if (!IsEnabled()) return false;
            var registered = RegisteredCommand();
            if (registered == null) return true;
            var current = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(current)) return true;
            return string.Equals(registered, current, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Create (overwrite) or delete the logon task. Returns false when
        /// schtasks failed, so the caller can snap the settings toggle back to reality.
        /// Never throws: the caller is a UI toggle handler, where an escaping exception
        /// means a crash dialog (App's DispatcherUnhandledException hook) for a
        /// best-effort feature.</summary>
        public static bool SetEnabled(bool on)
        {
            try
            {
                return SetEnabledCore(on);
            }
            catch (Exception ex)
            {
                Logger.Warn($"开机自启动：{(on ? "注册" : "删除")}计划任务失败（设置开关将回滚）", ex);
                return false;
            }
        }

        private static bool SetEnabledCore(bool on)
        {
            if (!on) return RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

            var exe = Process.GetCurrentProcess().MainModule.FileName;
            if (IsTransientPath(exe))
            {
                // 拒绝注册：从压缩包临时目录里绑自启，等于把「下次开机失效」写死进去。
                // 设置页的开关会因此弹回关闭（失败路径本来就是回滚到真实状态）。
                Logger.Warn($"开机自启动：拒绝从临时目录注册（{exe}）—— 请先把程序解压到文件夹再开启。");
                return false;
            }
            var userSid = WindowsIdentity.GetCurrent().User.Value;
            // XML-escape the exe path: an install path containing & or < would make the
            // document malformed, schtasks would fail, and the toggle would silently snap
            // back with no visible trace.
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{userSid}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{userSid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{SecurityElement.Escape(exe)}</Command>
    </Exec>
  </Actions>
</Task>
";
            // schtasks /XML insists on a UTF-16 file. Temp file, deleted right after.
            var tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0;
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// 读任务里登记的 exe 路径（null = 没有任务 / 读不到）。
        /// 直接读 <c>%SystemRoot%\System32\Tasks\{TaskName}</c> 这个 XML 文件，而不是解析
        /// <c>schtasks /Query /V</c> 的本地化文本 —— 那个输出随语言变，字段名不可靠。
        /// 任务文件是 UTF-16LE（带 BOM），且只有提权进程读得到；本程序常驻提权，所以可用。
        /// </summary>
        public static string RegisteredCommand()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "Tasks", TaskName);
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path, Encoding.Unicode);
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, "<Command>(.*?)</Command>", System.Text.RegularExpressions.RegexOptions.Singleline);
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
            catch (Exception ex)
            {
                Logger.Warn("开机自启动：读取任务登记路径失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 当前这份 exe 是不是跑在「临时位置」—— 压缩软件的临时解压目录、%TEMP%、回收站。
        ///
        /// 为什么不能把自启绑到这些地方：用户常直接在压缩包预览里双击运行，exe 实际落在
        /// <c>%LOCALAPPDATA%\Tempðzip$Temp\…</c> 之类的位置。绑上去之后，压缩软件清理
        /// 临时目录（或重启）、下次登录就又是「文件不存在」，等于把问题从一种搬成另一种。
        /// 2026-11-16 实测：自愈把任务指到了 <c>…\Tempðzip$Tempð$2\…	ask_monitor.exe</c>。
        /// </summary>
        public static bool IsRunningFromTransientLocation()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return false;
                return IsTransientPath(exe);
            }
            catch { return false; }
        }

        private static bool IsTransientPath(string path)
        {
            try
            {
                var p = Path.GetFullPath(path).ToLowerInvariant();
                string temp = null;
                try { temp = Path.GetTempPath().TrimEnd('\\').ToLowerInvariant(); } catch { }
                if (!string.IsNullOrEmpty(temp) && p.StartsWith(temp + "\\")) return true;

                foreach (var marker in new[] { "\\temp\\", "\\tmp\\", "$temp\\", "\\recycle.bin\\", "\\windows\\temp\\" })
                    if (p.Contains(marker)) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 启动时自愈：任务存在、但登记的 exe 不是当前这份 → 重新注册（覆盖）。
        ///
        /// 为什么要这个：自启状态 = 任务是否存在，而任务里<b>记着注册那一刻的 exe 路径</b>。
        /// 用户换了位置（重新解压、换目录、从单文件版换成文件夹版）之后，任务还在、
        /// 设置页照旧显示「开」，但每次登录都去启动一个已不存在的文件 —— 任务计划的
        /// LastResult 是 0x80070002（ERROR_FILE_NOT_FOUND），界面毫无提示。
        /// 2026-11-16 实测：任务指向 2026-09-30 的单文件版（文件早已删除），
        /// 从 10-31 起每次开机都在静默失败。
        ///
        /// 只有「用户已经开过自启」才动（不新建）；失败只记日志，绝不弹窗 —— 尽力而为。
        /// </summary>
        public static bool RefreshIfEnabled()
        {
            try
            {
                var registered = RegisteredCommand();
                if (registered == null) return false;          // 没任务 = 用户没开，什么都不做
                var current = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(current)) return false;
                if (string.Equals(registered, current, StringComparison.OrdinalIgnoreCase)) return false;

                // 临时目录里跑出来的副本**不能**当自启目标：压缩软件清理临时目录后，
                // 下次登录又会「文件不存在」。保持原登记不动，只记一条提示。
                if (IsTransientPath(current))
                {
                    Logger.Warn($"开机自启动：当前在临时目录运行（{current}），跳过改写；"
                              + $"登记仍是 {registered}。请把程序解压到文件夹后再运行一次。");
                    return false;
                }

                bool ok = SetEnabledCore(true);
                if (ok)
                    Logger.Info($"开机自启动：任务指向已更新 —— {registered} → {current}");
                else
                    Logger.Warn($"开机自启动：任务指向仍是 {registered}，改写为 {current} 失败");
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Warn("开机自启动：指向自愈检查失败", ex);
                return false;
            }
        }

        // schtasks prints localized output we never parse — only the exit code matters, so
        // the pipes stay UNredirected: with RedirectStandardOutput/Error = true and no
        // reader, a chatty failure fills the ~4KB pipe buffer, schtasks blocks, and the
        // bounded wait below burns its whole 10s on the UI thread and reports a false
        // failure (the toggle then snaps back although the task was created).
        // CreateNoWindow keeps the console hidden; the wait stays bounded so a wedged
        // service can never hang the settings page.
        private static int RunSchtasks(string arguments)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }))
                {
                    if (p == null) return -1;
                    p.WaitForExit(10000);
                    if (!p.HasExited) { try { p.Kill(); } catch { /* best effort */ } return -1; }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                // Logger, not Debug: a Release build has no Debug listener, and a failed
                // 开机自启动 toggle is user-visible (§4/§5).
                Logger.Warn($"开机自启动：schtasks {arguments} 调用失败", ex);
                return -1;
            }
        }
    }
}
