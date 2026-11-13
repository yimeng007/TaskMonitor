using System;
using System.Globalization;
using System.Windows.Data;

namespace task_monitor
{
    /// <summary>
    /// 把「字节/秒」格式化成带自适应单位的一行：B/s → KB/s → MB/s → **GB/s**，阈值二进制（1024），
    /// 与 TrafficMonitor 一致。任务栏浮层（D2D 文字）和详情弹窗（WPF）共用。
    /// <para>2026-11-12 加了 GB/s 档并把 MB 及以上统一成两位小数（用户拍板）：原来封顶在 MB/s，
    /// 万兆网跑满会印成 <c>1250.0 MB/s</c> 这种四位数，既难读也占宽。加档后同样的速率是
    /// <c>1.22 GB/s</c>，反而比旧写法短，不会撑破列宽。KB/s 保持一位（<c>50.0 KB/s</c>），
    /// 因为日常速率集中在那一档，多一位只增加噪声。</para>
    /// </summary>
    internal static class NetRateFormatter
    {
        private const double KB = 1024.0;
        private const double MB = 1024.0 * 1024.0;
        private const double GB = 1024.0 * 1024.0 * 1024.0;
        private const double TB = 1024.0 * 1024.0 * 1024.0 * 1024.0;

        /// <summary>
        /// 累计字节数，**不带 /s**。阈值同样是二进制（1024），共五档 B/KB/MB/GB/TB：
        /// 1 GB 上去是 TB —— 累计量长期只增不减，只给到 GB 迟早会印出 "4096.00 GB" 这种难读的数字。
        /// 两位小数在 GB 及以上保持（153.76 MB 这种"看起来精确"的读法才是用户要的），
        /// KB 仍是一位小数免得 "1.5 KB" 变成 "1.50 KB"。
        ///
        /// **TB 是终点档**：1024 TB 依然显示成 "1024.00 TB"，整数部分随便长。
        /// 早先还有一档 PB，2026-11-12 用户拍板取消 —— 按「每月清零」的口径，一个人一辈子也不可能
        /// 在一个月里跑到 1 PB（那要 3 Tbps 连跑一个月），留着只会让 99.99% 的人看到一个永远用不到的
        /// 分支，还显得这程序在夸张自己的量级。所以这里**故意**不写 PB 判断。
        /// </summary>
        /// <param name="fixedGb">true = 把**上限**锁在 GB：超过 1024 GB 也不升 TB（`1025 GB`）。
        /// 注意是「封顶」不是「只用」——不到 1 GB 时仍按 MB / KB / B 显示（早先写成无条件 `GB:F2`，
        /// 结果几百 KB 被印成 `0.00 GB`，2026-11-11 修）。用途是对账：运营商按 GB 给用量。
        /// 设置页「固定按 GB 显示」开关控制，默认关。</param>
        public static string FormatBytes(long bytes, bool fixedGb)
        {
            if (bytes < 0) bytes = 0;
            double b = bytes;
            if (b >= GB)
            {
                double g = b / GB;
                if (fixedGb)
                {
                    // 四位数以上不带小数：`1536.00 GB` 比 `1536 GB` 宽 3 个字符，弹窗那行只有
                    // ~300 px，带小数会折行（实测 1.5 TB 就折了）。对账只关心整数 GB。
                    return g >= 1000 ? $"{g:F0} GB" : $"{g:F2} GB";
                }
                if (b >= TB) return $"{b / TB:F2} TB";
                return $"{g:F2} GB";
            }
            if (b >= MB) return $"{b / MB:F2} MB";
            if (b >= KB) return $"{b / KB:F1} KB";
            return $"{b:F0} B";
        }

        /// <summary>e.g. 0 → "0 B/s", 51200 → "50.0 KB/s", 12_000_000 → "11.44 MB/s",
        /// 1_600_000_000 → "1.49 GB/s". 档位 B/s → KB/s → MB/s → GB/s，1024 进位；
        /// KB/s 一位小数，MB/s 与 GB/s 两位（见类注释）。</summary>
        public static string Format(long bytesPerSec)
        {
            if (bytesPerSec < 0) bytesPerSec = 0;
            double bps = bytesPerSec;
            if (bps >= GB) return $"{bps / GB:F2} GB/s";
            if (bps >= MB) return $"{bps / MB:F2} MB/s";
            if (bps >= KB) return $"{bps / KB:F1} KB/s";
            return $"{bps:F0} B/s";
        }

        /// <summary>
        /// The narrow-strip form of <see cref="Format"/> for a SIDE-docked taskbar: no space
        /// and no "/s" — such a strip is only ~46 DIP wide (a 48-DIP side bar minus its
        /// margin), where "11.4 MB/s" (~56 DIP) cannot fit, while "11.4M" (37 DIP) does.
        /// The unit letter carries the scale (K = KB/s, M = MB/s, G = GB/s, B = B/s);
        /// the detail popup keeps the full form. One decimal below 100M, none above (4 glyphs max).
        /// </summary>
        public static string FormatCompact(long bytesPerSec)
        {
            if (bytesPerSec < 0) bytesPerSec = 0;
            double bps = bytesPerSec;
            if (bps >= GB) return $"{bps / GB:F1}G";
            if (bps >= 100 * MB) return $"{bps / MB:F0}M";
            if (bps >= MB) return $"{bps / MB:F1}M";
            if (bps >= KB) return $"{bps / KB:F0}K";
            return $"{bps:F0}B";
        }
    }

    /// <summary>
    /// XAML bridge for <see cref="NetRateFormatter"/>: binds a process's per-second
    /// up/down byte rate to its display string in the Network detail list template.
    /// </summary>
    public sealed class NetRateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => NetRateFormatter.Format(value is long l ? l : System.Convert.ToInt64(value, culture));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
