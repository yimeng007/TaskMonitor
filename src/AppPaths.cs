using System;
using System.IO;

namespace task_monitor
{
    /// <summary>
    /// 程序的数据目录（<c>settings.yaml</c> / <c>net_history.json</c> / <c>logs\</c> 都放这里）。
    ///
    /// **规则**：默认跟 exe 同目录（绿色便携 —— 整个文件夹拷走即可带走配置），
    /// 但有两种情况改用 <c>%LOCALAPPDATA%\TaskMonitor\</c>：
    ///   1. exe 目录**不可写**（例如放在 Program Files）；
    ///   2. exe 单独躺在「随手放」的位置：桌面、下载、临时目录、盘符根目录。
    ///      这些地方放的是「临时拷出来试一下」的 exe，不该在那里生成 settings.yaml / logs ——
    ///      否则桌面会凭空多出配置和日志文件夹（2026-11-16 用户反馈）。
    ///
    /// 首次切到 LOCALAPPDATA 时会把旧的 settings.yaml / net_history.json **迁移过去**
    /// （只在目标不存在时复制，不删除源文件），避免用户感觉配置丢了。
    /// </summary>
    internal static class AppPaths
    {
        private const string FolderName = "TaskMonitor";

        private static string _dataDir;

        public static string DataDir => _dataDir ?? (_dataDir = Resolve());

        public static string SettingsFile => Path.Combine(DataDir, "settings.yaml");
        public static string NetHistoryFile => Path.Combine(DataDir, "net_history.json");
        public static string LogsDir => Path.Combine(DataDir, "logs");

        /// <summary>just for logging / troubleshooting.</summary>
        public static bool UsingUserDataDir { get; private set; }

        private static string Resolve()
        {
            string exeDir;
            try { exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
            catch { exeDir = null; }

            if (!string.IsNullOrEmpty(exeDir) && !IsSpotDirectory(exeDir) && IsWritable(exeDir))
                return exeDir;

            string local = null;
            try
            {
                local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            catch { }

            if (string.IsNullOrEmpty(local))
                return exeDir ?? ".";          // 极端兜底：连 LOCALAPPDATA 都读不到

            var dir = Path.Combine(local, FolderName);
            try { Directory.CreateDirectory(dir); } catch { /* 建不出来就交给后续写入报错 */ }

            UsingUserDataDir = true;
            Migrate(exeDir, dir);
            return dir;
        }

        /// <summary>
        /// 这个目录算不算「随手放」—— 桌面 / 下载 / 临时 / 盘符根。
        /// 用绝对路径比较（不区分大小写），并同时比对 OneDrive 重定向后的桌面。
        /// </summary>
        private static bool IsSpotDirectory(string dir)
        {
            try
            {
                var d = Path.GetFullPath(dir).TrimEnd('\\');

                if (d.Length <= 3) return true;                          // "C:\"
                if (d.EndsWith(":\\")) return true;

                var candidates = new[]
                {
                    SafeFolder(Environment.SpecialFolder.DesktopDirectory),
                    SafeFolder(Environment.SpecialFolder.Desktop),
                    SafeFolder(Environment.SpecialFolder.UserProfile) is string up && !string.IsNullOrEmpty(up)
                        ? Path.Combine(up, "Downloads") : null,
                    Path.GetTempPath(),
                    Environment.GetEnvironmentVariable("TEMP"),
                    Environment.GetEnvironmentVariable("TMP"),
                };
                foreach (var c in candidates)
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    if (string.Equals(d, Path.GetFullPath(c).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static string SafeFolder(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); } catch { return null; }
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, ".tm-write-probe");
                using (var fs = File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把「旧位置（exe 目录）」里的配置与流量历史搬到新位置 —— 仅在目标缺失时复制，
        /// 不删源文件（用户可能还想保留便携副本）。
        /// </summary>
        private static void Migrate(string fromDir, string toDir)
        {
            if (string.IsNullOrEmpty(fromDir)) return;
            try
            {
                if (string.Equals(Path.GetFullPath(fromDir).TrimEnd('\\'),
                                  Path.GetFullPath(toDir).TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase)) return;
                foreach (var name in new[] { "settings.yaml", "net_history.json" })
                {
                    var src = Path.Combine(fromDir, name);
                    var dst = Path.Combine(toDir, name);
                    if (File.Exists(src) && !File.Exists(dst))
                        File.Copy(src, dst, overwrite: false);
                }
            }
            catch { /* 迁移是尽力而为：失败不影响启动，旧配置仍留在原位 */ }
        }
    }
}
