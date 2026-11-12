using System;
using System.Collections.Generic;
using System.Threading;

namespace task_monitor
{
    /// <summary>
    /// 已使用流量，同时维护**今日**与**本月**两套累计（上传 / 下载分开）。
    ///
    /// 数据来源：采样线程每个 tick 给出 <c>NetUpBytesPerSec</c> / <c>NetDownBytesPerSec</c> 和
    /// 实际采样间隔，这里按「速率 × 间隔」累加。不用网卡的累计字节做差分，因为网卡切换
    /// （无线↔有线）、断连、休眠唤醒都会让那个值跳变，一次就能把几天的量算进当天。
    ///
    /// **重置规则**（用户 2026-11-11 定）：
    ///  - 今日：每天 0 点归零，判据是 "yyyy-MM-dd"
    ///  - 本月：每月 1 号归零，判据是 "yyyy-MM"
    /// 两套各自独立判断，程序一直开着跨过 0 点也会当场切换。
    ///
    /// **已知限制**：程序没运行时不计入。这是速率累加方案的固有限制（TrafficMonitor 同）。
    /// 想统计整天必须读 SRUM/SRBD 历史库，那有 60 分钟延迟，做不到实时。
    ///
    /// 线程模型：<see cref="Tick"/> 在采样线程调用，<see cref="Load"/> / <see cref="Store"/>
    /// 在 UI 线程调用，所以计数用 Interlocked，日期串用 volatile。
    /// </summary>
    internal static class NetUsage
    {
        /// <summary>
        /// 单次 tick 允许累加的上限。采样间隔最长 10 s，千兆满载也只有 1.25 GB —— 100 MB 的
        /// 上限只在时钟跳变 / 系统休眠后一次补算很多个 tick 时才会触发，那种增量本来就是错的。
        /// </summary>
        private const long TickCapBytes = 100L * 1024 * 1024;

        private static long _todayUp, _todayDown, _monthUp, _monthDown;
        private static volatile string _todayDate = "";
        private static volatile string _monthPeriod = "";

        // ---- 多月历史（net_history.json）----
        // _history 只在 Tick（跨月时推入上月）和 Store（写盘前覆盖当月）里动，前者在采样线程、
        // 后者在 UI 线程，所以统一用 _historySync 保护。列表本身很小（默认 ≤12 条）。
        private static readonly object _historySync = new object();
        private static List<NetMonthRecord> _history = new List<NetMonthRecord>();
        private static int _keepMonths = NetHistory.DefaultKeepMonths;

        /// <summary>历史保留月数（1~120，默认 12）。App 启动时按 settings.yaml 写入。</summary>
        public static int KeepMonths
        {
            get { return _keepMonths; }
            set { _keepMonths = NetHistory.NormalizeKeep(value); }
        }

        /// <summary>历史文件路径（与 exe 同目录）。测试可覆盖它指向临时目录。</summary>
        public static string HistoryPath = NetHistory.DefaultPath;

        /// <summary>
        /// 历史快照（副本、按月份升序）。调用方是网络详情页的「往月」那一行 —— 必须给副本：
        /// 采样线程跨月时会就地改这个列表，读的时候直接暴露引用会读到半更新状态。
        /// </summary>
        public static NetMonthRecord[] HistorySnapshot()
        {
            lock (_historySync)
            {
                var copy = new NetMonthRecord[_history.Count];
                for (int i = 0; i < _history.Count; i++)
                    copy[i] = new NetMonthRecord { Period = _history[i].Period, Up = _history[i].Up, Down = _history[i].Down };
                return copy;
            }
        }

        public static long TodayUpBytes => Interlocked.Read(ref _todayUp);
        public static long TodayDownBytes => Interlocked.Read(ref _todayDown);
        public static long MonthUpBytes => Interlocked.Read(ref _monthUp);
        public static long MonthDownBytes => Interlocked.Read(ref _monthDown);

        public static long TodayTotalBytes => TodayUpBytes + TodayDownBytes;
        public static long MonthTotalBytes => MonthUpBytes + MonthDownBytes;

        /// <summary>今日累计所属日期，"yyyy-MM-dd"。</summary>
        public static string TodayDate => _todayDate;

        /// <summary>本月累计所属月份，"yyyy-MM"。</summary>
        public static string MonthPeriod => _monthPeriod;

        /// <summary>
        /// 详情弹窗里是否显示「本月已使用」/「今日已使用」两行。
        /// 由设置页写入、NetDetailView 每个 tick 读取 —— 用静态字段而不是往快照里塞两个 bool，
        /// 是因为它只影响一个视图的可见性，跟采样数据无关。
        /// </summary>
        public static volatile bool ShowMonth = false;

        /// <inheritdoc cref="ShowMonth"/>
        public static volatile bool ShowToday = false;

        /// <summary>
        /// 累计流量的单位是否锁定在 GB。true = 超过 1024 GB 也不换算成 TB/PB，
        /// 一直用 GB 显示（对账时和运营商账单同口径）；false = 正常自动升档。
        /// 跟上面两个开关一样是静态字段，由设置页写入、NetDetailView 每个 tick 读取。
        /// </summary>
        public static volatile bool FixedGb = false;

        /// <summary>「往月」那一行是否显示（设置 → 外观 → 显示往月流量）。默认关。</summary>
        public static volatile bool ShowHistory = false;

        /// <summary>启动时从配置恢复；跨日 / 跨月的那一套从零开始。首次运行两套都从零开始。</summary>
        public static void Load(AppSettings settings)
        {
            if (settings == null) return;
            string today = CurrentDate();
            string month = CurrentMonth();

            if (string.Equals(settings.NetTodayDate, today, StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _todayUp, Math.Max(0, settings.NetTodayUpBytes));
                Interlocked.Exchange(ref _todayDown, Math.Max(0, settings.NetTodayDownBytes));
            }
            else
            {
                Interlocked.Exchange(ref _todayUp, 0);
                Interlocked.Exchange(ref _todayDown, 0);
            }
            _todayDate = today;

            if (string.Equals(settings.NetMonthPeriod, month, StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _monthUp, Math.Max(0, settings.NetMonthUpBytes));
                Interlocked.Exchange(ref _monthDown, Math.Max(0, settings.NetMonthDownBytes));
            }
            else
            {
                Interlocked.Exchange(ref _monthUp, 0);
                Interlocked.Exchange(ref _monthDown, 0);
            }
            _monthPeriod = month;

            // 多月历史：读盘（损坏/缺失都会退化成空列表），再把「当月」这条补上，
            // 这样即使上个月的数字此刻只剩配置里的那一份，也不会在历史里缺月。
            lock (_historySync)
            {
                _history = NetHistory.Load(HistoryPath, _keepMonths);
                NetHistory.Upsert(_history, month, MonthUpBytes, MonthDownBytes);
            }
        }

        /// <summary>落盘前调用：把当前累计写回配置对象（由调用方负责 Save）。</summary>
        public static void Store(AppSettings settings)
        {
            if (settings == null) return;
            settings.NetTodayDate = _todayDate;
            settings.NetTodayUpBytes = TodayUpBytes;
            settings.NetTodayDownBytes = TodayDownBytes;
            settings.NetMonthPeriod = _monthPeriod;
            settings.NetMonthUpBytes = MonthUpBytes;
            settings.NetMonthDownBytes = MonthDownBytes;

            // 历史随之落盘：先把「当月」覆盖成最新值（本月数字一直在涨），再裁剪 + 原子替换。
            // 写失败只记一行日志，不影响配置本身的保存。
            lock (_historySync)
            {
                NetHistory.Upsert(_history, _monthPeriod, MonthUpBytes, MonthDownBytes);
                NetHistory.Save(HistoryPath, _history, _keepMonths);
            }
        }

        /// <summary>每个采样 tick 调一次。intervalMs 传配置的采样间隔。</summary>
        public static void Tick(long upBytesPerSec, long downBytesPerSec, int intervalMs)
        {
            if (intervalMs <= 0) return;

            string today = CurrentDate();
            string month = CurrentMonth();
            if (!string.Equals(today, _todayDate, StringComparison.Ordinal))
            {
                // 跨日：今日清零。本月不动（同一天之内不可能同时跨月，1 号的跨日会顺带跨月）。
                Interlocked.Exchange(ref _todayUp, 0);
                Interlocked.Exchange(ref _todayDown, 0);
                _todayDate = today;
            }
            if (!string.Equals(month, _monthPeriod, StringComparison.Ordinal))
            {
                // 跨月：先把**旧月份**的最终数字推进历史，再清零 —— 顺序反了就会丢数。
                lock (_historySync)
                {
                    NetHistory.Upsert(_history, _monthPeriod, MonthUpBytes, MonthDownBytes);
                    _history = NetHistory.Trim(_history, _keepMonths);
                }
                Interlocked.Exchange(ref _monthUp, 0);
                Interlocked.Exchange(ref _monthDown, 0);
                _monthPeriod = month;
            }

            long up = (long)(upBytesPerSec * (intervalMs / 1000.0));
            long down = (long)(downBytesPerSec * (intervalMs / 1000.0));
            if (up < 0 || up > TickCapBytes) up = 0;
            if (down < 0 || down > TickCapBytes) down = 0;
            if (up == 0 && down == 0) return;

            Interlocked.Add(ref _todayUp, up);
            Interlocked.Add(ref _todayDown, down);
            Interlocked.Add(ref _monthUp, up);
            Interlocked.Add(ref _monthDown, down);
        }

        private static string CurrentDate() => DateTime.Now.ToString("yyyy-MM-dd");
        private static string CurrentMonth() => DateTime.Now.ToString("yyyy-MM");
    }
}
