using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace task_monitor
{
    /// <summary>
    /// 开机自启动 = 计划任务（不用 Run 键：那会每次开机弹 UAC）。
    /// 注册走生成的 XML（<c>schtasks /Create /XML</c>），因为 schtasks 的默认值对常驻 GUI 是错的：
    /// <c>ExecutionTimeLimit</c> 默认 72 小时（会被杀）、<c>StopIfGoingOnBatteries</c> 默认 true（笔记本不启）。
    /// 主体用当前用户 SID + <c>InteractiveToken</c>（GUI 不能 S4U），触发器同一个人。
    /// 状态不写进 settings.yaml —— **任务本身就是唯一事实**：查它、建它（覆盖）、删它。
    /// </summary>
    internal static class StartupTask
    {
        private const string TaskName = "TaskMonitor";

        /// <summary>True when the logon task exists (any state — disabled tasks still count
        /// as "on": we never create one disabled).</summary>
        public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

        /// <summary>「这份程序会不会开机自启」= 任务存在且指向当前 exe。
        /// 计划任务是整机唯一的，别处跑一份新副本时旧任务还在 —— 只看 IsEnabled() 会误显示成「开」。
        /// 读不到登记路径时按「开」处理，免得把用户已开的自启显示成关。</summary>
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
        /// <summary>任务存在、但它指向的 exe 已经不在了（用户移动/改名/删了程序）。
        /// 这是"开机静默失败"唯一可检测的信号 —— 看得到才谈得上提示。</summary>
        public static bool RegisteredTargetMissing()
        {
            try
            {
                if (!IsEnabled()) return false;
                var registered = RegisteredCommand();
                if (string.IsNullOrEmpty(registered)) return false;
                return !File.Exists(registered);
            }
            catch { return false; }
        }

        /// <summary>强制把任务重写成"指向当前 exe"（设置页「修复指向」用）。</summary>
        public static bool RepairToCurrentExe() => SetEnabled(true);

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
      <!-- 不加 Delay：登录即启动，任务栏就绪由程序自己重试（IsPlausibleTaskbarRect） -->
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

        /// <summary>当前 exe 是否跑在临时位置（压缩包临时解压目录 / %TEMP% / 回收站）。
        /// 绑自启等于把「下次开机失效」写死进去（2026-11-16 实测被指到 Tempðzip$Temp 下）。</summary>
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
        /// 启动时自愈：任务存在就**整份重写**（不新建、不弹窗，失败只记日志）。
        /// 必须每次重写而不是「路径不同才写」—— 任务 XML 里的设置（延迟、运行级）会随版本变，
        /// 只比路径的话旧设置永远留在系统里（2026-11-21：删掉的 20 秒延迟仍在任务里）。
        /// </summary>
        public static bool RefreshIfEnabled()
        {
            try
            {
                var registered = RegisteredCommand();
                if (registered == null) return false;          // 没任务 = 用户没开，什么都不做
                var current = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(current)) return false;
                // 路径相同也重写（让 XML 跟代码走），开销约 100ms。

                // 临时目录里的副本不当自启目标（清理后就「文件不存在」）。
                if (IsTransientPath(current))
                {
                    Logger.Warn($"开机自启动：当前在临时目录运行（{current}），跳过改写；"
                              + $"登记仍是 {registered}。请把程序解压到文件夹后再运行一次。");
                    return false;
                }

                bool ok = SetEnabledCore(true);
                bool moved = !string.Equals(registered, current, StringComparison.OrdinalIgnoreCase);
                if (ok)
                    Logger.Info(moved
                        ? $"开机自启动：任务指向已更新 —— {registered} → {current}"
                        : $"开机自启动：任务已同步当前程序（{current}）");
                else
                    Logger.Warn($"开机自启动：任务仍指向 {registered}，改写为 {current} 失败");
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
