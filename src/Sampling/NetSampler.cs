using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace task_monitor
{
    /// <summary>
    /// Samples live network throughput for one adapter — auto-selected (the default) or
    /// pinned by the 设置 → 采样项目 → 网络 → 适配器 pick.
    /// Ported from TrafficMonitor's adapter selection + per-second octet-delta logic:
    ///   - <see cref="SelectAdapter"/> picks the Up adapter with the largest cumulative
    ///     BytesReceived+BytesSent (the one that's carried the most traffic since boot),
    ///     skipping loopback / tunnel / virtual adapters.
    ///   - <see cref="Sample"/> returns that adapter's per-second up/down rate by
    ///     differencing the cumulative byte counters between calls.
    ///   - If the selected adapter goes silent for 30s, it re-selects (auto mode only —
    ///     a pinned adapter stays put while silent).
    ///
    /// A pinned adapter (NetworkInterface.Id GUID from the settings) is used while it
    /// exists and is Up; when it disappears or goes down the sampler falls back to 自动
    /// (the pick survives in settings.yaml) and re-checks every 30 ticks so the pinned
    /// adapter resumes when it returns.
    ///
    /// Self-check / re-baseline (the "rate freezes at the old value" family of bugs):
    /// the cached <see cref="NetworkInterface"/> instance can go stale after a sleep /
    /// adapter re-enable / proxy TUN restart — the instance keeps referencing an interface
    /// index that no longer updates, so every subsequent delta reads 0 and the rate never
    /// recovers. Four guards, in order of how often they fire:
    ///   - <see cref="NetworkChange.NetworkAddressChanged"/> → re-baseline immediately
    ///     (cable swapped, WiFi↔Ethernet switch, VPN/TUN up or down);
    ///   - a <see cref="SuspendGapSeconds"/>-long gap between samples → the machine slept,
    ///     so the old byte counters are meaningless — take a fresh baseline;
    ///   - a negative or implausibly large delta → 32-bit wrap / counter reset / index
    ///     reuse — re-baseline instead of clamping to 0 forever;
    ///   - <see cref="NicRefreshEveryTicks"/> → swap in a freshly enumerated instance for
    ///     the same Id, which repairs the stale-index case without touching the baseline.
    ///
    /// Uses the managed <see cref="NetworkInterface"/> API (equivalent to TrafficMonitor's
    /// GetAdaptersInfo + GetIfTable, no P/Invoke). Runs on the taskbar STA thread inside
    /// SystemSampler — single-threaded, no concurrency.
    /// </summary>
    internal sealed class NetSampler
    {
        private const int ZeroSpeedReselectSeconds = 30;
        private const int PreferredCheckEveryTicks = 30;   // pinned-adapter return probe cadence
        // How often the cached NetworkInterface instance is re-enumerated for the same Id.
        // Enumeration is expensive (~275ms with many virtual adapters), so this is a slow
        // self-check, not a per-tick cost: the object is allowed to be a minute old.
        private const int NicRefreshEveryTicks = 60;
        // A gap this long between two samples means the UI thread was suspended (sleep /
        // hibernate / stalled). Byte deltas across such a gap are not a rate.
        private const double SuspendGapSeconds = 5.0;
        // One sample can never carry more than this much, anything above is a counter
        // wrap/reset or an interface-index reuse rather than real traffic.
        private const long ImplausibleDeltaBytes = 2L * 1024 * 1024 * 1024;

        // Substrings (case-insensitive) that mark an adapter as virtual/loopback/tunnel.
        // Matched against Description first, Name as a fallback — some virtual adapters
        // (e.g. vEthernet(WSL)) only carry the telltale token in Name.
        private static readonly string[] VirtualKeywords =
        {
            "vmware", "virtualbox", "vbox",
            "hyper-v", "vethernet", "wsl", "docker",
            "tunnel", "teredo", "isatap", "6to4",
            "loopback", "pseudo", "tap", "virtual", "vpn",
            "环回", "隧道", "虚拟",
        };

        private const int MaxHistory = 60;

        // 隧道类网卡（VPN / 代理 TUN / WireGuard…）的识别词，只用于「告知用户代理在线」，
        // 不参与选卡 —— 自动模式本来就会跳过它们、统计物理网卡（那才是运营商计费的字节数）。
        // 刻意比 VirtualKeywords 窄：Hyper-V / WSL / Docker 不算代理。
        private static readonly string[] TunnelKeywords =
        {
            "tap-", "tun", "wireguard", "openvpn", "vpn", "clash", "mihomo",
            "sing-box", "singbox", "tailscale", "zerotier", "代理", "隧道",
        };

        /// <summary>当前处于 Up 的隧道类网卡名（null = 没检测到）。
        /// 由 <see cref="SelectAdapterAuto"/> 在已有的一次枚举里顺带算出，不额外枚举。</summary>
        public static string TunnelAdapterName { get; private set; }

        private string _selectedId;        // NetworkInterface.Id (GUID) — stable locator
        private string _selectedName;      // Description, for display
        // 设置 → 采样项目 → 网络 → 适配器: the pinned NetworkInterface.Id, null = 自动.
        // Handed in per tick by SystemSampler (the volatile settings push); a change
        // forces a reselection.
        private string _preferredId;
        // While a pinned adapter is missing (we're on the 自动 fallback), ticks since the
        // last "did it come back?" probe — one enumeration every 30 ticks, not per tick.
        private int _ticksSincePreferredCheck = PreferredCheckEveryTicks;
        // The resolved adapter, cached so Sample() doesn't re-enumerate every tick.
        // NetworkInterface.GetAllNetworkInterfaces() is expensive on machines with many
        // virtual adapters (Hyper-V/WSL2/Docker) — re-resolving only on reselect/exception
        // cut the per-tick cost from ~275ms to <1ms (measured).
        private NetworkInterface _selectedNic;
        private long _prevBytesRecv;
        private long _prevBytesSent;
        private bool _hasPrev;             // first sample can only establish a baseline
        private long _lastTicks;           // Stopwatch timestamp of the previous Sample
        private double _silentSeconds;     // consecutive seconds with no traffic
        // Set by the NetworkChange handler (a threadpool thread) and consumed by Sample()
        // on the UI thread. A plain flag is enough: the handler only ever raises it, and a
        // stale "true" merely costs one extra baseline.
        private volatile bool _rebaselinePending;
        private int _ticksSinceNicRefresh;  // drives the slow instance self-check
        // Cumulative bytes per adapter as of the last full enumeration, used to spot the
        // adapter that is carrying traffic *right now* (切网卡 后旧网卡的累计值仍然最大，
        // 只按累计值重选会一直选回那张已停用的网卡).
        private Dictionary<string, long> _lastBytesById = new Dictionary<string, long>();

        // Per-second up/down rolling history (oldest→newest) for the detail popup's
        // bidirectional chart. Mirrors CpuSampler/RamSampler. Cleared on adapter reselect
        // so the chart never stitches two adapters' counters together.
        private readonly Queue<long> _upHistory = new Queue<long>(MaxHistory);
        private readonly Queue<long> _downHistory = new Queue<long>(MaxHistory);

        public NetSampler()
        {
            // 切网卡 / VPN(TUN) 起落 / 适配器禁用启用 都会触发：立刻重锁基线，避免拿旧
            // 计数器的差值算出一次假尖峰，也避免旧实例继续返回陈旧数字。
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            SelectAdapter();
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            _rebaselinePending = true;
        }

        /// <summary>
        /// The cached selected adapter, or null when none is selected. Read by
        /// <see cref="NetInfoSampler"/> (handed over by SystemSampler each tick) for the
        /// connection-info band — its background thread re-queries the NIC directly; the
        /// object itself is just a query façade.
        /// </summary>
        public NetworkInterface CurrentAdapter => _selectedNic;

        /// <summary>
        /// Pick the adapter to sample: the pinned one while it exists and is Up, else the
        /// auto rule — among Up, non-virtual, non-loopback adapters, the one with the
        /// largest cumulative BytesReceived+BytesSent. Resets the sampling baseline.
        /// </summary>
        private void SelectAdapter()
        {
            if (_preferredId != null && TrySelect(_preferredId)) return;
            SelectAdapterAuto();
        }

        // The auto rule (TrafficMonitor's pick): max cumulative traffic among Up,
        // non-virtual, non-loopback adapters.
        // <paramref name="preferRecentActivity"/> is the silent-reselect path: the adapter
        // that is currently carrying traffic wins over the one with the largest lifetime
        // total (切网卡 后旧网卡累计值仍最大，只按累计值会反复选回已经不走流量的那张).
        private void SelectAdapterAuto(bool preferRecentActivity = false)
        {
            string bestId = null;
            string bestName = null;
            NetworkInterface bestNic = null;
            long bestBytes = -1;

            string activeId = null;
            string activeName = null;
            NetworkInterface activeNic = null;
            long bestDelta = 0;

            var seen = new Dictionary<string, long>();
            string tunnelName = null;   // 顺带识别：Up 的隧道类网卡（只用于界面提示）

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (tunnelName == null && LooksLikeTunnel(nic)) tunnelName = nic.Description;
                if (IsVirtualAdapter(nic)) continue;

                long bytes;
                try
                {
                    var stats = nic.GetIPStatistics();
                    bytes = stats.BytesReceived + stats.BytesSent;
                }
                catch
                {
                    // Some tunnel/pseudo adapters throw on statistics queries — skip them.
                    continue;
                }

                seen[nic.Id] = bytes;

                if (preferRecentActivity && _lastBytesById.TryGetValue(nic.Id, out long prevBytes))
                {
                    long delta = bytes - prevBytes;
                    if (delta > bestDelta)
                    {
                        bestDelta = delta;
                        activeId = nic.Id;
                        activeName = nic.Description;
                        activeNic = nic;
                    }
                }

                if (bytes > bestBytes)
                {
                    bestBytes = bytes;
                    bestId = nic.Id;
                    bestName = nic.Description;
                    bestNic = nic;
                }
            }

            _lastBytesById = seen;
            TunnelAdapterName = tunnelName;

            if (preferRecentActivity && activeId != null)
                ApplySelection(activeId, activeName, activeNic);
            else
                ApplySelection(bestId, bestId != null ? bestName : null, bestNic);
        }

        // Description 优先、Name 兜底（与 IsVirtualAdapter 同一套路数）。
        private static bool LooksLikeTunnel(NetworkInterface nic)
        {
            string desc = nic.Description ?? "";
            string name = nic.Name ?? "";
            foreach (string kw in TunnelKeywords)
            {
                if (desc.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // The pinned rule: select the adapter with this exact Id, but only while it is
        // present and Up (down/disabled reads as "missing" and the caller falls back to
        // 自动). The virtual-adapter filter is deliberately NOT applied — an explicit pick
        // is exactly how the user watches a VPN/vEthernet adapter.
        private bool TrySelect(string id)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Id != id) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) return false;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
                ApplySelection(nic.Id, nic.Description, nic);
                _ticksSincePreferredCheck = 0;
                return true;
            }
            return false;
        }

        // A newly selected adapter's byte counters start fresh, so the rolling history is
        // dropped — carrying it over would mix two adapters' rates into one chart.
        // Re-selecting the SAME adapter (silent reselect landing on the same NIC, or the
        // periodic instance refresh) must NOT reset anything: the counters and the chart
        // are still that adapter's own, and clearing them would show a bogus gap.
        private void ApplySelection(string id, string name, NetworkInterface nic)
        {
            if (id != null && id == _selectedId && nic != null)
            {
                _selectedNic = nic;
                _selectedName = name;
                return;
            }

            _selectedId = id;
            _selectedName = name;
            _selectedNic = nic;
            _hasPrev = false;
            _silentSeconds = 0;
            _upHistory.Clear();
            _downHistory.Clear();
        }

        /// <summary>
        /// Re-arm the byte-counter baseline after an event that makes the previous counters
        /// meaningless (network address change, sleep/resume): keep the same adapter when
        /// possible and only take a fresh baseline, so the next tick measures correctly
        /// instead of closing a multi-hour gap into one absurd rate.
        /// </summary>
        private void RebaselineSelection(string reason)
        {
            _hasPrev = false;
            _lastTicks = Stopwatch.GetTimestamp();
            _silentSeconds = 0;
            _ticksSinceNicRefresh = 0;

            Logger.Info("[net] 重锁基线：" + reason);

            if (_preferredId == null)
            {
                // 自动: re-resolve against the current adapter set (this is the 切网卡 path).
                SelectAdapterAuto();
                return;
            }

            if (!TrySelect(_preferredId))
            {
                _ticksSincePreferredCheck = 0;
                SelectAdapterAuto();
            }
        }

        /// <summary>
        /// Slow self-check: swap the cached <see cref="NetworkInterface"/> for a freshly
        /// enumerated instance carrying the same Id. A cached instance can keep pointing at
        /// an interface index that stopped updating (sleep/resume, adapter disable+enable,
        /// proxy TUN restart) — it then returns frozen counters and the rate never recovers.
        /// The baseline is deliberately kept: the Id is the same adapter, only the object
        /// is replaced.
        /// </summary>
        private void RefreshCachedNic()
        {
            if (_selectedId == null) return;

            var seen = new Dictionary<string, long>();
            NetworkInterface match = null;
            bool matchUp = false;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                try
                {
                    var stats = nic.GetIPStatistics();
                    seen[nic.Id] = stats.BytesReceived + stats.BytesSent;
                }
                catch
                {
                    // tunnel/pseudo adapters throw on statistics queries — not candidates.
                }

                if (nic.Id != _selectedId) continue;
                match = nic;
                matchUp = nic.OperationalStatus == OperationalStatus.Up;
            }

            // Keep the per-adapter snapshot fresh so the silent-reselect path compares
            // "traffic in the last minute", not "traffic since some ancient enumeration".
            if (seen.Count > 0) _lastBytesById = seen;

            if (match == null)
            {
                // The Id is gone from the adapter list entirely — force a reselect on the
                // next tick rather than reading a dead object.
                _selectedId = null;
                _selectedNic = null;
                _hasPrev = false;
                return;
            }

            if (matchUp) _selectedNic = match;
        }

        /// <summary>
        /// Returns (upBytesPerSec, downBytesPerSec, adapterName, upHistory, downHistory).
        /// Up = sent, Down = received. In 自动 mode (preferredAdapterId null) it re-selects
        /// if the selected adapter has vanished or has been silent for 30s; a pinned
        /// adapter stays put while silent, falls back to 自动 while gone/down, and resumes
        /// when it returns. The histories are the 60-tick rolling up/down rate
        /// (oldest→newest) for the detail popup's bidirectional chart. Rates are normalized
        /// over the REAL elapsed time between calls, so the settings 采样间隔 (0.5s/1s/2s
        /// taskbar tick) never distorts them.
        /// </summary>
        /// <param name="preferredAdapterId">设置 → 网络 → 适配器: the pinned
        /// NetworkInterface.Id (GUID), null/empty = 自动.</param>
        public (long upBytesPerSec, long downBytesPerSec, string adapterName, long[] upHistory, long[] downHistory) Sample(string preferredAdapterId)
        {
            // A changed pick (including → 自动): drop the current selection so the code
            // below re-resolves against the new preference.
            if (string.IsNullOrEmpty(preferredAdapterId)) preferredAdapterId = null;
            if (preferredAdapterId != _preferredId)
            {
                _preferredId = preferredAdapterId;
                _selectedId = null;
                _selectedName = null;
                _selectedNic = null;
            }

            // Nothing selected yet (e.g. no Up adapter at startup, or the pick just
            // changed) — retry each tick.
            if (_selectedId == null)
            {
                SelectAdapter();
                if (_selectedId == null) return (0, 0, "未连接", _upHistory.ToArray(), _downHistory.ToArray());
            }
            // 网卡地址/可用性变化（切网卡、VPN TUN 起落、适配器禁用启用）：重锁基线。
            // 放在选择逻辑之后、读计数器之前，这样这一拍就能用上新的适配器。
            else if (_rebaselinePending)
            {
                _rebaselinePending = false;
                RebaselineSelection("网卡地址/可用性变化（切网卡、VPN 起落、适配器启停）");
            }
            else if (_preferredId != null)
            {
                if (_selectedId == _preferredId)
                {
                    // The pinned adapter is in use: a pulled cable / disabled NIC counts
                    // as "gone" — fall back to 自动 (TrySelect inside requires Up).
                    // OperationalStatus re-queries the one cached NIC — cheap, unlike
                    // GetAllNetworkInterfaces().
                    bool pinnedGone;
                    try { pinnedGone = _selectedNic.OperationalStatus != OperationalStatus.Up; }
                    catch { pinnedGone = true; }
                    if (pinnedGone) SelectAdapter();
                }
                else
                {
                    // On the 自动 fallback while pinned: probe every 30 ticks whether the
                    // pinned adapter came back (one enumeration at this cadence, not per
                    // tick — the per-tick path stays enumeration-free).
                    if (++_ticksSincePreferredCheck >= PreferredCheckEveryTicks)
                    {
                        _ticksSincePreferredCheck = 0;
                        TrySelect(_preferredId);
                    }
                }
            }

            // Read byte counters straight off the cached adapter — no per-tick enumeration.
            // GetAllNetworkInterfaces() is expensive (rebuilds managed objects for every
            // adapter, and dev machines carry many virtual ones); we re-resolve only when the
            // cached NIC throws (link pulled / adapter disabled / VPN dropped) or on the 30s
            // silent-traffic reselect below.
            NetworkInterface nic = _selectedNic;
            if (nic == null)
            {
                SelectAdapter();
                return (0, 0, _selectedName ?? "未连接", _upHistory.ToArray(), _downHistory.ToArray());
            }

            long curRecv, curSent;
            try
            {
                var stats = nic.GetIPStatistics();
                curRecv = stats.BytesReceived;
                curSent = stats.BytesSent;
            }
            catch
            {
                SelectAdapter();
                return (0, 0, _selectedName ?? "未连接", _upHistory.ToArray(), _downHistory.ToArray());
            }

            // First sample after (re)select only establishes the baseline — no delta yet.
            if (!_hasPrev)
            {
                _prevBytesRecv = curRecv;
                _prevBytesSent = curSent;
                _hasPrev = true;
                _lastTicks = Stopwatch.GetTimestamp();
                return (0, 0, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());
            }

            // Real elapsed time since the previous sample — the taskbar tick is configurable
            // (采样间隔), so a raw per-tick delta would read half/double the true rate.
            long nowTicks = Stopwatch.GetTimestamp();
            double dt = (nowTicks - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = nowTicks;
            if (dt <= 0) return (0, 0, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());

            // 休眠 / 待机 恢复：这一拍和上一拍之间隔了很久，计数器差值不再是「速率」。
            // 重锁基线，这一拍报 0，下一拍恢复正常 —— 比把几小时的流量摊到一次里更接近事实。
            if (dt > SuspendGapSeconds)
            {
                _prevBytesRecv = curRecv;
                _prevBytesSent = curSent;
                _silentSeconds = 0;
                Logger.Info(string.Format("[net] 采样间隔 {0:F1}s 超过阈值，按休眠唤醒处理：重锁基线", dt));
                return (0, 0, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());
            }

            // Per-second rate = byte delta / dt. The underlying MIB_IFROW counters are 32-bit
            // on net48, so a wrap can produce a negative delta; an interface index reused by
            // another adapter can produce an absurd jump. Both mean the baseline is void —
            // re-baseline instead of clamping to 0 (which used to freeze the rate for good).
            long dRecv = curRecv - _prevBytesRecv;
            long dSent = curSent - _prevBytesSent;
            if (dRecv < 0 || dSent < 0 ||
                dRecv > ImplausibleDeltaBytes || dSent > ImplausibleDeltaBytes)
            {
                _prevBytesRecv = curRecv;
                _prevBytesSent = curSent;
                _silentSeconds = 0;
                Logger.Info(string.Format(
                    "[net] 计数器异常（recv {0:+#;-#;0} B / sent {1:+#;-#;0} B），重锁基线 + 自检实例",
                    dRecv, dSent));
                RefreshCachedNic();
                return (0, 0, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());
            }

            _prevBytesRecv = curRecv;
            _prevBytesSent = curSent;
            long upRate = (long)(dSent / dt);
            long downRate = (long)(dRecv / dt);

            // Slow self-check: swap in a freshly enumerated instance for the same adapter so
            // a stale object (sleep/resume, adapter re-enable, proxy TUN restart) can never
            // keep feeding frozen counters. One enumeration a minute, baseline untouched.
            if (++_ticksSinceNicRefresh >= NicRefreshEveryTicks)
            {
                _ticksSinceNicRefresh = 0;
                RefreshCachedNic();
            }

            // Auto-reselect when the chosen adapter carries no traffic for a while — it may
            // have gone idle while another adapter is now the active path. AUTO mode only:
            // a pinned adapter is allowed to sit silent (its user asked for exactly it).
            // 重选时按「刚刚有流量」而不是「累计最大」挑，否则切网卡后会一直选回旧网卡。
            if (dRecv == 0 && dSent == 0)
            {
                _silentSeconds += dt;
                if (_preferredId == null && _silentSeconds >= ZeroSpeedReselectSeconds)
                {
                    _silentSeconds = 0;
                    SelectAdapterAuto(preferRecentActivity: true);
                    return (0, 0, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());
                }
            }
            else
            {
                _silentSeconds = 0;
            }

            _upHistory.Enqueue(upRate);
            _downHistory.Enqueue(downRate);
            while (_upHistory.Count > MaxHistory) _upHistory.Dequeue();
            while (_downHistory.Count > MaxHistory) _downHistory.Dequeue();

            return (upRate, downRate, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());
        }

        private static bool IsVirtualAdapter(NetworkInterface nic)
        {
            string desc = nic.Description ?? string.Empty;
            string name = nic.Name ?? string.Empty;
            foreach (var kw in VirtualKeywords)
            {
                if (desc.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
