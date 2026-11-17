using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace task_monitor
{
    /// <summary>
    /// One point-in-time set of connection details for the Network detail panel's info band
    /// (between the chart and the process list). Published by <see cref="NetInfoSampler"/>
    /// as a whole-instance swap — instances are never mutated after publication, so a reader
    /// on any thread always sees a consistent set. Strings are display-ready; null / -1
    /// means "unknown" and the panel shows "—".
    /// </summary>
    internal sealed class NetInfo
    {
        public static readonly NetInfo Empty = new NetInfo();

        public string ConnectionType;   // "WLAN" / "有线" / an enum name (Ppp…) / null = 未连接
        public string Ssid;             // Wi-Fi only, else null
        public string WifiStandard;     // e.g. "Wi-Fi 6 (802.11ax)" — Wi-Fi only
        public string WifiBand;         // "2.4 GHz" / "5 GHz" / "6 GHz" — Wi-Fi only
        public string WifiChannelWidth; // e.g. "80 MHz" — Wi-Fi only (6 GHz width: not parsed)
        public long LinkRxBps;          // negotiated receive link rate, bits/s (0 = unknown)
        public long LinkTxBps;          // negotiated transmit link rate, bits/s
        public string LocalIp;          // IPv4 preferred, global IPv6 when there's no IPv4
        public string PublicIp;         // external what-is-my-ip lookup; null until fetched
        public string PublicIpPrev;     // 上一次的出口 IP（变了才有值，用于「已切换」提示）
        public string GeoLabel;         // 「中国 山东 · 电信」这类地区+运营商（可为空）
        public DateTime PublicIpChangedAt;  // 上次发现变化的时刻（UTC）；默认值 = 从未变化
        public string PublicIpV6;       // same, over IPv6; null when no v6 connectivity
        public long GatewayRttMs = -1;  // ICMP RTT to the default gateway ("本地延迟")
        public long PublicRttMs = -1;   // ICMP RTT to the public probe ("公网延迟")
        // 本次探测失败、显示的是**上一次有效值**时为 true（UI 加 "~" 前缀提示"可能不是最新"）。
        // 以前一次超时就直接显示 "—"，延迟线看上去忽有忽无（2026-11-16 用户反馈）。
        public bool GatewayRttStale;
        public bool PublicRttStale;
        // 延迟是**TCP 握手**量出来的（ICMP 被拦、从未成功过时才启用）→ UI 显示 "≈44 ms"，
        // 说明口径与 ICMP 不同（含握手开销，通常比 ping 略高几 ms）。
        public bool GatewayRttViaTcp;
        public bool PublicRttViaTcp;
        public string ProxyNote;        // 代理活动来源（隧道网卡名 / 「系统代理 host:port」）；null = 无。
                                        // 仅用于界面提示「经代理」——统计口径仍是物理网卡。
        /// <summary>
        /// 是否显示「换节点后可点刷新」的提示。**条件就是"经代理"**，不看档位 ——
        /// 按档位判断会出现「显示 → 节奏回到 15 秒档后消失 → 8 分钟后又出现」的闪烁
        /// （2026-11-17 实测反馈），也才会出现「硬等十几分钟才看到」的错觉。
        /// </summary>
        public bool ShowRefreshHint;

        // Field-wise copy — PollOnce publishes an early partial snapshot, so the instance
        // it keeps filling must not be the published one (publication = never mutated).
        public NetInfo ShallowCopy() => (NetInfo)MemberwiseClone();
    }

    /// <summary>
    /// Samples the Network panel's connection-info band: connection type, negotiated link
    /// rate, Wi-Fi SSID/standard/band/channel-width (wlanapi.dll via <see cref="WlanInterop"/>),
    /// local IP, default-gateway and public ICMP latency, and the public IP (an HTTP lookup,
    /// refreshed every <see cref="PublicIpRefreshMinutes"/> min, retried after
    /// <see cref="PublicIpRetrySeconds"/> s on failure, and re-fetched immediately when the
    /// selected adapter changes; the lookup AND the public ICMP latency probe can be
    /// switched off entirely in 设置 → 采样项目 → 网络 → 公网 IP — <see cref="PublicIpLookupEnabled"/>).
    ///
    /// None of this may run on the taskbar STA thread: each ICMP ping can block up to
    /// <see cref="PingTimeoutMs"/>, the HTTP lookup up to <see cref="HttpTimeoutMs"/> per
    /// endpoint, and even the WLAN query is an RPC to wlansvc. So everything happens on a
    /// dedicated background thread at ~1 Hz and the result is published as a volatile
    /// <see cref="NetInfo"/> swap (the same pattern as ProcessNetSampler's SRUM callback
    /// thread). <see cref="Sample"/> — called from SystemSampler's per-second tick — is a
    /// single volatile read. The adapter to inspect is handed over by SystemSampler each
    /// tick (NetSampler's cached selection). The poll thread never throws and never dies —
    /// a failed cycle just leaves the previous values in place for one more second.
    /// </summary>
    internal sealed class NetInfoSampler
    {
        // Public latency probe target — an ICMP ping to www.baidu.com: fast and
        // ICMP-responsive inside CN. A hostname, not a literal: DNS picks the CDN's
        // nearest node and the resolution happens inside Ping.Send, on this background
        // thread (never the taskbar STA). This is the one-line calibration point for
        // the 公网延迟 number.
        private const string PublicProbeHost = "www.baidu.com";

        // Public-IP lookup endpoints, tried in order; each answers with the caller's public
        // address as a short plain-text body (IPv4-oriented — a v6 literal doesn't match the
        // extraction regex and the next endpoint is tried).
        // 顺序按 2026-11-13 实测（带系统代理 / 直连各 3 次）排：
        // icanhazip 代理下 0.61s、直连 0.74s，两条路径都通，最稳；
        // cip.cc 直连 0.20s、代理 1.39s，且响应体自带「地址 / 运营商」，排第二兼顾速度与地区；
        // ipify 直连不通、代理 2.35s，只作最后兜底。
        // **两套端点，按是否经代理切换**（2026-11-17 全量实测，见「本地待记录」）：
        //   经代理 → 国外端点。国内站点会被「智能分流」直接放行，返回的仍是直连 IP（误导）。
        //   未代理 → 国内端点。国外端点在国内多数不通。
        // 括号里是实测延迟（同一台机器、同一时刻，走本机代理 / 直连）。
        private static readonly string[] PublicIpEndpointsProxied =
        {
            "https://ipv4.icanhazip.com",      // 0.30s 纯文本
            "https://api.ip.sb/ip",            // 0.33s 纯文本
            "https://checkip.amazonaws.com",   // 0.41s 纯文本
            "https://ifconfig.me/ip",          // 0.50s 纯文本
            "https://ipinfo.io/ip",            // 0.52s 纯文本
            "https://ipecho.net/plain",        // 0.53s 纯文本
            "https://www.trackip.net/ip",      // 0.65s 纯文本
            "https://ip.gs",                   // 0.68s 纯文本
            "https://ipapi.co/ip/",            // 0.73s 纯文本
            "https://api.ipify.org",           // 0.77s 纯文本
        };

        // 国内「回显你自己 IP」的纯文本/JSON 接口**极其稀少**（2026-11-17 实测 20 多个候选）：
        //   大部分是 JS 渲染（HTML 里根本没有你的 IP）、要 key（ip138/ipip）、
        //   已停服/限流（3322 / qq / dnspod / vore.top / taobao 旧接口），或只回 IPv6（ipip.net / bilibili）。
        // 真正可用的就这三个，加海外最后兜底：
        private static readonly string[] PublicIpEndpointsDirect =
        {
            "https://ddns.oray.com/checkip",                                       // 0.25s 「Current IP Address: x」纯文本
            "https://cip.cc/",                                                     // 0.17s HTML 内含你的 IP
            "https://ip.taobao.com/outGetIpInfo?ip=myip&accessKey=alibaba-inc",    // 0.37s JSON queryIp（有 QPS 限制）
            "https://ipv4.icanhazip.com",                                          // 1.7s 纯文本（国内时通时不通，末位）
        };

        // IPv6 counterparts — v6-only hosts, so a machine without IPv6 connectivity simply
        // fails them all (they're only called when the adapter HAS a global v6 address).
        // v6 顺序同样按实测：icanhazip 1.39s、api6.ipify 1.60s；代理下两者都不通
        // （代理不转发 v6，失败即跳过，不影响任何显示）。
        // 公网 IPv6 的回显端点（2026-11-17 实测扩充，原为 2 个）。
        // 排序原则：**纯 v6 主机优先** —— 它们只监听 v6，不会用 v4 应答（否则 v6 解析器
        // 因「整体必须是 v6 字面量」而丢弃，白等一个超时）；双栈主机放后面兜底。
        // 括号内为本机实测延迟（强制 -6，全部返回同一个 v6 地址）。
        private static readonly string[] PublicIpV6Endpoints =
        {
            "https://ipv6.icanhazip.com",   // 0.88s 纯 v6
            "https://ipv6.seeip.org",       // 2.95s 纯 v6
            "https://v6.ident.me",          // 2.53s 纯 v6
            "https://api6.ipify.org",       // 4.41s 纯 v6（较慢，靠后）
            "https://ipecho.net/plain",     // 0.93s 双栈（应答 v6 时可用）
            "https://ip.gs",                // 1.78s 双栈
            "https://ifconfig.me/ip",       // 2.64s 双栈
        };

        private const int PingTimeoutMs = 1000;
        /// <summary>TCP 兜底握手的超时（比 ping 略短：握手要么很快要么压根不通）。</summary>
        private const int TcpProbeTimeoutMs = 800;
        private const int HttpTimeoutMs = 3000;
        // 出口 IP 变化检测：10 分钟才查一次，所以「切换」最多滞后一个刷新周期 —— 这是刻意的，
        // 加频会让「轻」的定位失守。只记住上一次的 IP 与变化时刻，不引入任何新的外部服务。
        private string _publicIpPrev;
        private DateTime _publicIpChangedAt;

        // 地区缓存：按 IP 缓存 10 分钟（cip.cc 是单点小站，别刷它）。
        private string _geoIp;
        private string _geoLabel;
        private long _geoFetchedAtMs;

        private const int PublicIpRefreshMinutes = 10;  // re-lookup cadence on success
        // **经代理时不能等 10 分钟**（2026-11-16 反馈）：加速器换节点后出口 IP 立刻就变了，
        // 但程序仍按 10 分钟节奏查，点了刷新也只影响「下一次允许查询」的时间戳 ——
        // 用户看到的是「换了节点 IP 不跟」。代理态下改成 15 秒一轮，换节点十几秒内自动跟上。
        //
        // 但「每 15 秒一次」不能长时间跑：那是 240 次/小时、5760 次/天，而且几乎全打在
        // 列表里第一个成功的端点上 —— 这些多是社区/个人维护的免费服务，受不了（2026-11-17 讨论）。
        // 所以改成**阶梯**：代理刚生效/刚换 IP 时用 15s 跟紧（换节点的高发窗口），
        // 稳定下来后放宽到 30s、再到 60s 封顶；一旦 IP 变了（说明换了节点）立刻回到 15s。
        private const int ProxyRefreshMs = 15_000;      // 阶梯第一档（紧跟随）
        private const int ProxyRefreshMidMs = 30_000;   // 第二档
        private const int ProxyRefreshSlowMs = 60_000;  // 封顶（约 60 次/小时）
        private const int ProxyFastStreak = 8;          // 前 8 次（约 2 分钟）保持 15s
        private const int ProxyMidStreak = 20;          // 之后到第 20 次用 30s
        // 失败后的重试节奏：**前几次快速重试，之后退回 60 秒**（2026-11-16 修「首次运行有概率
        // 看不到公网 IP」）。原来失败一次就固定等 60 秒：VM 刚开机时网络栈还没就绪，
        // 3 个端点 9 秒内全超时 → 界面接下来整整一分钟都是「—」，看起来像功能坏了。
        // 现在 3s → 6s → 12s → 24s → 48s → 60s 封顶，网络一就绪就能补上。
        private const int PublicIpRetrySeconds = 60;    // 退避上限
        private const int PublicIpFirstRetryMs = 3000;  // 首次失败后的等待

        // Written by SystemSampler (taskbar STA thread) every tick; read by the poll thread.
        private volatile NetworkInterface _adapter;
        private readonly AutoResetEvent _pollWake = new AutoResetEvent(false);
        private readonly Thread _pollThread;
        private volatile bool _stopping;
        // 设置 → 采样项目 → 网络 → 公网 IP switch (default on) — written by SystemSampler's
        // per-tick hand-off, read by the poll thread. Off = NOTHING goes to the public
        // internet: neither the what-is-my-ip HTTP lookups nor the 公网延迟 ICMP probe
        // (the gateway ping is LAN-only and unaffected).
        private volatile bool _publicIpLookupEnabled = true;
        // The latest published set — whole-instance swap, never mutated in place.

        // 系统代理（「设置 → 网络和 Internet → 代理」/ Clash Verge 的「系统代理」开关）：
        // 开着的表现是 HKCU\...\Internet Settings 的 ProxyEnable=1 + ProxyServer=host:port。
        // 这种模式下没有隧道网卡，流量照走物理网卡 —— 但用户对不上账单时要知道这层存在。
        private static string SystemProxyNote()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key == null) return null;
                    object enable = key.GetValue("ProxyEnable");
                    if (!(enable is int) || (int)enable == 0) return null;
                    string server = key.GetValue("ProxyServer") as string;
                    return string.IsNullOrEmpty(server) ? "系统代理" : "系统代理 " + server;
                }
            }
            catch { return null; }   // 读不到就当没代理：提示是锦上添花，不值得为它报错
        }

        private volatile NetInfo _latest = NetInfo.Empty;

        // ---- poll-thread state (single-threaded below) ----
        // The WLAN client handle is opened ONLY inside TryQueryWifiLive (open → query → close
        // in one go) and Zero at all other times. WlanOpenHandle / WlanQueryInterface /
        // WlanGetNetworkBssList are location-sensitive on Win10+ — any call lights the taskbar
        // location indicator. We touch them only when the user opens the Network panel
        // (RequestWifiDetails → _wifiDetailsRequested), never on the idle poll path.
        private IntPtr _wlan;               // WLAN client handle; Zero except inside TryQueryWifiLive
        private bool _wlanUnavailable;      // wlanapi.dll missing (Server Core) → never retry
        private string _adapterId;          // last-seen adapter Id (a change re-fetches the public IP)
        private string _publicIp;           // last successful lookup (kept across later failures)
        private string _publicIpV6;         // same, IPv6
        private long _publicIpNextTryMs;    // Stopwatch-ms timestamp of the next allowed lookup

        // ---- on-demand Wi-Fi details (location-sensitive; queried only when a panel opens) ----
        // Poll-thread-private cache; no synchronization — the UI thread sees results only via the
        // _latest NetInfo copy. Keyed by adapter Id, NOT SSID: SSID itself needs wlanapi to read,
        // so keying on it would force a query on every open. A same-adapter SSID roam is invisible
        // without a query; WifiCacheTimeoutMs bounds that staleness.
        private sealed class WifiCache
        {
            public string AdapterId;                     // NetworkInterface.Id this cache is for
            public WlanInterop.WlanConnectionInfo Info;  // the queried SSID/PHY/rates/band/width
            public long RefreshedAtMs;                   // Stopwatch-ms of the query
        }
        private WifiCache _wifiCache;
        private const long WifiCacheTimeoutMs = 5 * 60_000L;  // same-adapter roam staleness backstop
        private volatile bool _wifiDetailsRequested;     // UI thread sets; poll thread reads-and-clears

        /// <summary>唯一实例：面板上的「立即刷新」按钮要用它（见 <see cref="RequestImmediateRefresh"/>）。</summary>
        public static NetInfoSampler Current { get; private set; }

        public NetInfoSampler()
        {
            Current = this;
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "NetInfoSampler" };
            _pollThread.Start();
        }

        /// <summary>把下一次公网 IP 查询与地区查询都拉到现在：不改变任何节奏常量，
        /// 只是让轮询线程下一拍立刻重查（原先要等最多 10 分钟）。UI 线程可安全调用。</summary>
        public void RequestImmediateRefresh()
        {
            _publicIpNextTryMs = 0;     // 让 nowMs >= _publicIpNextTryMs 成立 → 下一拍就查
            _geoFetchedAtMs = 0;        // 地区缓存一起作废，IP 没变也会重取
            _proxyStreak = 0;           // 用户正在盯着看：回到最紧的 15s 档（不然可能还是 60s）
            Logger.Debug("用户点击刷新——公网 IP 与地区缓存已作废，下一拍重查");
        }

        /// <summary>The adapter to inspect (NetSampler's current selection), or null.</summary>
        public NetworkInterface Adapter
        {
            set => _adapter = value;
        }

        /// <summary>Whether the public-internet probes run (设置 → 采样项目 → 网络 → 公网 IP;
        /// default on): the what-is-my-ip HTTP lookups AND the 公网延迟 ICMP ping. One
        /// volatile write — cheap enough for the per-second hand-off.</summary>
        public bool PublicIpLookupEnabled
        {
            set => _publicIpLookupEnabled = value;
        }

        /// <summary>The latest published set (never null). One volatile read — cheap enough
        /// for the per-second SystemSampler tick.</summary>
        public NetInfo Sample() => _latest;

        public bool Shutdown()
        {
            _stopping = true;
            _pollWake.Set();
            AbortInFlight();                 // 先打断网络等待，线程才能立刻退出

            // 只等 250ms —— 这个线程末尾**没有任何清理**（唯一的语句是一条 Debug 日志），
            // 而 SRUM 注销是 SamplingController 在调用本方法之前自己做的。
            // 以前等 1000ms：一旦此刻正卡在 DNS 解析或 ICMP 里（Abort/Dispose 都断不掉），
            // 退出就要多等 1 秒，还会连带让 SamplingController 报「采样线程停止等待超时」
            // （2026-11-16 VM 日志：6 次退出里 1 次 1016ms）。
            // 线程是后台线程，进程退出时自然结束，不需要为它阻塞界面。
            bool stopped = _pollThread.Join(250);
            if (!stopped) Logger.Debug("NetInfoSampler 未在 250ms 内退出——后台线程会在进程结束时结束");
            else Logger.Debug("NetInfoSampler 已停止");
            return true;                     // 对调用方而言「停止已完成」，不再等待
        }

        /// <summary>打断正在进行的公网 IP 查询（退出时用）。可从任意线程安全调用。</summary>
        private void AbortInFlight()
        {
            try { _reqV4InFlight?.Abort(); } catch { }
            try { _reqV6InFlight?.Abort(); } catch { }
            try { _reqGeoInFlight?.Abort(); } catch { }
            try { _pingInFlight?.Dispose(); } catch { }
        }

        /// <summary>Called from the UI thread when the Network detail panel opens. The next
        /// poll tick refreshes the Wi-Fi cache IF it is stale (wrong adapter / timed out);
        /// a fresh cache is reused without any wlanapi call. Thread-safe (single volatile
        /// write). This is the sole trigger for the location-sensitive wlanapi path.</summary>
        public void RequestWifiDetails() => _wifiDetailsRequested = true;

        private void PollLoop()
        {
            try
            {
                while (!_stopping)
                {
                    long started = Stopwatch.GetTimestamp();
                    try { PollOnce(); }
                    catch { /* never die — stale values for one cycle beat an empty band */ }

                    long elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;
                    int delay = (int)(1000 - elapsedMs);
                    _pollWake.WaitOne(delay < 250 ? 250 : delay);
                }
            }
            finally { Logger.Debug("NetInfoSampler poll 线程已退出"); }
        }

        private void PollOnce()
        {
            NetworkInterface nic = _adapter;
            if (nic == null)
            {
                _latest = NetInfo.Empty;
                return;
            }

            var info = new NetInfo
            {
                // 代理活动来源：优先隧道网卡（TUN 模式），否则看系统代理（PAC/HTTP 代理模式）。
                // 两条路径统计口径都没变 —— 数字仍是物理网卡字节数，这里只是告知用户「经代理」。
                // 第一遍：隧道网卡（TUN）优先，否则读系统代理。
                // 拿不到就**立刻用注册表再确认一次** —— 抵掉网卡枚举偶发返回 null 的抖动，
                // 同时保证「关代理」当拍就能反映（不挂粘滞，用户要的是立刻消失）。
                ProxyNote = NetSampler.TunnelAdapterName ?? SystemProxyNote() ?? SystemProxyNote(),
            };

            // 提示状态在**建 info 时就算好**：这一拍会被发布两次（早快照 + 探测后快照），
            // 若只在第二次里设，UI 每秒会收到「false → true」—— 那就是闪烁的根源。
            _proxyHintOn = info.ProxyNote != null;
            info.ShowRefreshHint = _proxyHintOn;
            // On-demand Wi-Fi: only when the Network panel was just opened (UI thread sets the
            // flag via RequestWifiDetails). This is the ONE place on the poll path that may touch
            // the location-sensitive wlanapi — and only if the cache is stale (a hit reuses it).
            if (_wifiDetailsRequested)
            {
                _wifiDetailsRequested = false;   // clear first: a failed cycle must not retry forever
                TryRefreshWifiCache(nic);
            }
            FillConnection(nic, info);
            IPAddress gateway = FillAddresses(nic, info, out bool hasGlobalV6);

            // 公网 IP lookup switched off (设置 → 采样项目 → 网络): never touch the HTTP
            // endpoints (the 公网延迟 probe below is gated on the same flag), and drop any
            // cached address so both publications below render
            // "—"/collapse at once; the next-try timestamp resets so a re-enable
            // fetches immediately instead of waiting out the old refresh cadence.
            if (!_publicIpLookupEnabled)
            {
                _publicIp = null;
                _publicIpV6 = null;
                _publicIpNextTryMs = 0;
            }

            // Publish the fast local facts (type/SSID/rate/local IP + the cached public
            // IPs) BEFORE the slow probes below: on the first cycle those take seconds
            // (2× ICMP ≤1s each, then up to 3 HTTP endpoints × HttpTimeoutMs per stack),
            // and until the first publication the panel renders NetInfo.Empty — "未连接"
            // on a perfectly healthy connection. The copy keeps the publication immutable
            // while this instance gets its slow fields filled in.
            info.PublicIp = _publicIp;
            info.PublicIpPrev = _publicIpPrev;
            info.PublicIpChangedAt = _publicIpChangedAt;
            info.GeoLabel = _geoLabel;
            info.PublicIpV6 = hasGlobalV6 ? _publicIpV6 : null;

            // **延迟也要带上上一轮的值**：这一拍的 ICMP 探测在下面几十毫秒后才完成，
            // 而 NetInfo 是结构体，新建时 GatewayRttMs/PublicRttMs 默认是 -1。
            // 以前这里直接发布 → UI 每秒刷新时大多撞上"还没探测"的那一版，于是延迟显示 "—"
            // （宿主实测：采样层算出 4/37，UI 收到 -1；虚拟机轮询节奏不同所以看着正常）。
            // 先填上次的有效值并标 stale，探测完成后下面会覆盖成最新值。
            info.GatewayRttMs = _gwLast;
            info.GatewayRttStale = _gwLast >= 0 && _gwLastStale;
            info.PublicRttMs = _pubLast;
            info.PublicRttStale = _pubLast >= 0 && _pubLastStale;

            _latest = info.ShallowCopy();

            // The two latency probes (named ProbeRttMs — "Ping" would collide with the type).
            // ICMP needs no elevation (the app is elevated anyway). The gateway probe is
            // LAN-only and always runs; the 公网延迟 probe shares the 公网 IP switch — off
            // = the app sends no traffic to the public internet at all.
            // 延迟：3 次滑动窗口取中位数（抹掉抖动），失败时沿用上一次有效值并标 stale。
            info.GatewayRttMs = ProbeSmooth(gateway != null ? gateway.ToString() : null,
                                            _gwSamples, ref _gwLast, out bool gwStale, out bool gwTcp,
                                            allowTcpFallback: false);   // 网关通常不开 443，不做 TCP 兜底
            info.GatewayRttStale = gwStale;
            info.GatewayRttViaTcp = gwTcp;

            bool pubStale = false, pubTcp = false;
            info.PublicRttMs = _publicIpLookupEnabled
                ? ProbeSmooth(PublicProbeHost, _pubSamples, ref _pubLast, out pubStale, out pubTcp,
                              allowTcpFallback: true)                   // www.baidu.com:443 可握手
                : -1;
            info.PublicRttStale = _publicIpLookupEnabled && pubStale;
            info.PublicRttViaTcp = _publicIpLookupEnabled && pubTcp;

            // 记住本轮真实状态，供下一次「快发布」沿用（避免 ~ 与正常值来回跳）
            _gwLastStale = gwStale;
            _pubLastStale = pubStale;

            if (nic.Id != _adapterId)
            {
                _adapterId = nic.Id;
                _publicIpNextTryMs = 0;
            }
            // 开 / 关代理都会换出口 IP：状态一变就立刻重查，不等 10 分钟节奏，也不等用户点刷新。
            if (!string.Equals(info.ProxyNote, _lastProxyNote, StringComparison.Ordinal))
            {
                _lastProxyNote = info.ProxyNote;
                _publicIpNextTryMs = 0;
                _geoFetchedAtMs = 0;      // 换出口了，地区也跟着重取
                _proxyStreak = 0;         // 刚开关代理：回到最紧的 15s 档
                Logger.Debug(info.ProxyNote == null
                    ? "代理已关闭——立即重查出口 IP"
                    : $"代理已启用（{info.ProxyNote}）——立即重查出口 IP");
            }
            long nowMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            if (_publicIpLookupEnabled && nowMs >= _publicIpNextTryMs)
            {
                string ip = FetchPublicIp(!string.IsNullOrEmpty(_lastProxyNote));
                // The v6 lookup is skipped entirely without a global v6 address — otherwise
                // a v4-only machine would eat up to 3 endpoints × HttpTimeoutMs of blocking
                // on this thread every retry cycle for a value that can never exist.
                string ip6 = hasGlobalV6 ? FetchPublicIpV6() : null;
                if (ip != null || ip6 != null)
                {
                    _publicIpFailStreak = 0;
                    // 节奏：直连 10 分钟；经代理走阶梯（15s → 30s → 60s 封顶）。
                    // 只有 **v4 真的拿到值** 才升一档 —— v6 成功但 v4 失败时不该被判成「稳定」，
                    // 否则会一路放宽到 60 秒，而 v4 其实一直没查到。
                    bool viaProxy = !string.IsNullOrEmpty(_lastProxyNote);
                    long cadence;
                    if (!viaProxy)
                    {
                        cadence = PublicIpRefreshMinutes * 60_000L;   // 直连：10 分钟
                    }
                    else
                    {
                        if (ip != null) _proxyStreak++;
                        cadence = _proxyStreak <= ProxyFastStreak ? ProxyRefreshMs
                                : _proxyStreak <= ProxyMidStreak ? ProxyRefreshMidMs
                                : ProxyRefreshSlowMs;
                    }
                    _publicIpNextTryMs = nowMs + cadence;
                }
                else
                {
                    // 指数退避：3s、6s、12s、24s、48s、60s（封顶）
                    _publicIpFailStreak = Math.Min(_publicIpFailStreak + 1, 6);
                    long backoff = PublicIpFirstRetryMs << (_publicIpFailStreak - 1);
                    _publicIpNextTryMs = nowMs + Math.Min(backoff, PublicIpRetrySeconds * 1000L);
                    if (_publicIpFailStreak <= 3)
                        Logger.Debug($"公网 IP 查询失败（连续第 {_publicIpFailStreak} 次）"
                                   + $"——{Math.Min(backoff, PublicIpRetrySeconds * 1000L) / 1000} 秒后重试");
                }
                if (ip != null)
                {
                    if (_publicIp != null && !string.Equals(_publicIp, ip, StringComparison.Ordinal))
                    {
                        _publicIpPrev = _publicIp;
                        _publicIpChangedAt = DateTime.UtcNow;
                        // 出口变了 = 多半刚换节点：回到最紧的 15s 档，好让后面这次切换跟得上。
                        // （换节点不改系统代理配置，所以「代理状态变化」那条路径不会触发，
                        //  必须在这里单独清零 —— 否则会一路按 60 秒查，用户觉得"换节点不跟"。）
                        _proxyStreak = 0;
                        Logger.Debug("出口 IP 已变化——查询节奏回到 15 秒档");
                    }
                    _publicIp = ip;
                }
                if (ip6 != null) _publicIpV6 = ip6;
            }
            if (!hasGlobalV6) _publicIpV6 = null;   // v6 went away — don't show a stale one
            MaybeFetchGeo(nowMs);                   // 地区：失败静默，不影响 IP
            info.PublicIp = _publicIp;
            info.PublicIpPrev = _publicIpPrev;
            info.PublicIpChangedAt = _publicIpChangedAt;
            info.GeoLabel = _geoLabel;
            info.PublicIpV6 = _publicIpV6;
            info.ShowRefreshHint = _proxyHintOn;   // 与早发布取同一个值

            _latest = info;
        }

        // Connection type + negotiated link rate (+ SSID/standard/band/width for Wi-Fi).
        // Never calls wlanapi — Wi-Fi details come from the on-demand cache filled by
        // TryRefreshWifiCache. No cache yet (panel never opened, or adapter switched) → the
        // Wi-Fi cells stay blank; ConnectionType and the generic fields still populate.
        private void FillConnection(NetworkInterface nic, NetInfo info)
        {
            bool isWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            // The managed enum spells the wired family *Ethernet* (Ethernet, FastEthernetT,
            // GigabitEthernet…); anything else (Ppp…) shows its enum name.
            info.ConnectionType = isWifi ? "WLAN"
                : nic.NetworkInterfaceType.ToString().IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "有线"
                    : nic.NetworkInterfaceType.ToString();

            if (isWifi && TryApplyWifiCache(nic, info)) return;

            // Wired / Wi-Fi with no usable cache: the NIC's negotiated link speed, same value
            // for both directions.
            try { info.LinkRxBps = info.LinkTxBps = nic.Speed; }
            catch { /* leave 0 — the band omits the rate */ }
        }

        // Copy the cached Wi-Fi details into NetInfo. Match key is adapter Id ONLY — not the
        // timeout: a pinned Network panel keeps showing the values it opened with until the
        // adapter actually changes (the timeout governs only whether the NEXT open re-queries).
        // Returning false leaves the Wi-Fi cells blank and falls back to nic.Speed.
        private bool TryApplyWifiCache(NetworkInterface nic, NetInfo info)
        {
            var cache = _wifiCache;
            if (cache == null || cache.AdapterId != nic.Id) return false;
            var wifi = cache.Info;
            info.Ssid = wifi.Ssid;
            info.WifiStandard = PhyTypeToString(wifi.PhyType);
            info.WifiBand = BandToString(wifi.CenterFreqKhz);
            info.WifiChannelWidth = wifi.ChannelWidthMHz > 0
                ? wifi.EightyPlusEighty ? "80+80 MHz" : wifi.ChannelWidthMHz + " MHz"
                : null;
            info.LinkRxBps = wifi.RxRateKbps * 1000L;
            info.LinkTxBps = wifi.TxRateKbps * 1000L;
            return true;
        }

        // Decide whether THIS poll cycle touches wlanapi. Called only when _wifiDetailsRequested
        // was just set (panel opened). Cache hit (same adapter, fresh) → no query at all. Every
        // wlanapi call in the whole sampler lives inside TryQueryWifiLive below.
        private void TryRefreshWifiCache(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) return;

            long nowMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            if (_wifiCache != null
                && _wifiCache.AdapterId == nic.Id
                && nowMs - _wifiCache.RefreshedAtMs <= WifiCacheTimeoutMs)
                return;   // still fresh — reuse, no wlanapi call

            if (TryQueryWifiLive(nic, out WlanInterop.WlanConnectionInfo fresh) && fresh != null)
            {
                _wifiCache = new WifiCache
                {
                    AdapterId = nic.Id,
                    Info = fresh,
                    RefreshedAtMs = nowMs,
                };
            }
            // On failure leave the previous cache (possibly null); the next open retries.
        }

        // Local IP (IPv4 preferred, global IPv6 when there's no IPv4) + the default gateway.
        // Returns the gateway address (null when there isn't one) for the latency probe;
        // hasGlobalV6 reports whether the adapter holds any non-link-local v6 address
        // (gates the public-v6 lookup — no point querying when v6 can't route).
        private static IPAddress FillAddresses(NetworkInterface nic, NetInfo info, out bool hasGlobalV6)
        {
            IPAddress gateway = null;
            hasGlobalV6 = false;
            try
            {
                var props = nic.GetIPProperties();
                string v4 = null, v6 = null;
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) v4 ??= ua.Address.ToString();
                    else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6
                             && !ua.Address.IsIPv6LinkLocal)
                    {
                        v6 ??= ua.Address.ToString();
                        hasGlobalV6 = true;
                    }
                }
                info.LocalIp = v4 ?? v6;

                foreach (var gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        gateway = gw.Address;
                        break;
                    }
                }
            }
            catch { /* a vanishing NIC can throw mid-query — leave everything unknown */ }
            return gateway;
        }

        // One-shot Wi-Fi query: open the client, read the connection, close the client — all
        // wlanapi calls confined here, the handle Zero again before return (finally). Called
        // only from TryRefreshWifiCache, i.e. only when a Network panel just opened AND the
        // cache is stale. Never throws; a failure (wlansvc restarting, service stopped, Server
        // Core) returns false and leaves the previous cache for the next open to retry.
        private bool TryQueryWifiLive(NetworkInterface nic, out WlanInterop.WlanConnectionInfo wifi)
        {
            wifi = null;
            if (_wlanUnavailable || !Guid.TryParse(nic.Id, out Guid guid)) return false;

            try
            {
                EnsureWlanHandle();
                if (_wlan == IntPtr.Zero) return false;
                return WlanInterop.TryQueryConnection(_wlan, guid, out wifi);
            }
            catch (DllNotFoundException) { _wlanUnavailable = true; return false; }
            catch (EntryPointNotFoundException) { _wlanUnavailable = true; return false; }
            finally
            {
                // Always close: _wlan must be Zero between poll cycles so the idle path touches
                // no wlanapi at all. (A stale-handle retry no longer applies — every call opens
                // a fresh handle, so there is nothing to go stale.)
                CloseWlanHandle();
            }
        }

        private void EnsureWlanHandle()
        {
            if (_wlan != IntPtr.Zero || _wlanUnavailable) return;
            try
            {
                if (WlanInterop.WlanOpenHandle(WlanInterop.ClientVersion, IntPtr.Zero,
                        out _, out IntPtr h) != WlanInterop.ERROR_SUCCESS)
                    h = IntPtr.Zero;   // e.g. the WLAN service is stopped — retry next cycle
                _wlan = h;
            }
            catch (DllNotFoundException) { _wlanUnavailable = true; }
            catch (EntryPointNotFoundException) { _wlanUnavailable = true; }
        }

        private void CloseWlanHandle()
        {
            if (_wlan == IntPtr.Zero) return;
            try { WlanInterop.WlanCloseHandle(_wlan, IntPtr.Zero); } catch { }
            _wlan = IntPtr.Zero;
        }

        // One ICMP echo; the RTT in ms, or -1 on timeout/failure. Takes a hostname or a
        // literal — for PublicProbeHost the DNS resolution happens inside Send and can add
        // its own seconds on a broken resolver; acceptable here (only ever called on the
        // poll thread, never the taskbar STA). Never throws.
        // 延迟的滑动窗口（最近 3 次成功采样）与最后有效值
        private readonly System.Collections.Generic.Queue<long> _gwSamples = new System.Collections.Generic.Queue<long>();
        private readonly System.Collections.Generic.Queue<long> _pubSamples = new System.Collections.Generic.Queue<long>();
        private long _gwLast = -1, _pubLast = -1;
        // 上一轮**探测结果**里的 stale 标记。快发布时连它一起沿用，
        // 这样两拍之间显示不会在 "6 ms" / "~6 ms" 之间来回跳（~ 只代表"上一轮没探到"）。
        private bool _gwLastStale, _pubLastStale;

        // 关闭时用来打断在飞的 HTTP 请求：HttpWebRequest 的超时是 3 秒，而退出只等 1 秒，
        // 不打断就会出现「采样线程停止等待超时」、进程多留 1 秒（2026-11-16 实测 808ms）。
        // Abort() 可从任意线程调用，会让阻塞中的 GetResponse/ReadToEnd 立刻抛异常。
        private volatile System.Net.HttpWebRequest _reqV4InFlight;
        private volatile System.Net.HttpWebRequest _reqV6InFlight;
        // 地区查询（cip.cc）也要登记：它是最容易在退出时正开着的一路 —— 启动后紧接着
        // 就会查一次地区，用户往往正好在这个窗口里退出（2026-11-16 VM 日志：
        // 第一次退出 39ms 正常，第二次因为地区查询在飞又飙到 1017ms）。
        private volatile System.Net.HttpWebRequest _reqGeoInFlight;

        // 在飞的 ICMP 探测（网关 / 公网各一次，同一线程内先后执行，一个字段够用）。
        // Ping 没有取消接口，但 Dispose() 会让阻塞中的 Send 立刻抛异常 —— 我们正是靠它
        // 把「退出时还要等 1 秒」的最后一路（PingTimeoutMs = 1000）也打断
        // （2026-11-16 VM 日志：三次退出里两次 20ms、一次正好 1039ms）。
        private volatile System.Net.NetworkInformation.Ping _pingInFlight;

        /// <summary>公网 IP 连续失败次数：决定下一次重试要等多久（成功即清零）。</summary>
        private int _publicIpFailStreak;
        private int _rttFailLogs;   // 延迟「从未成功」日志的限流计数
        private int _geoFailLogs;   // 地区查询失败日志的限流计数
        /// <summary>上一拍是否经代理 + 代理来源串；用于「开关代理」时立刻重查出口 IP。</summary>
        private string _lastProxyNote;

        /// <summary>代理态下连续成功次数 —— 决定走 15s / 30s / 60s 哪一档（换 IP 或开关代理时清零）。</summary>
        private int _proxyStreak;

        /// <summary>经代理时的端点轮换游标：每次查询从不同端点起步，
        /// 把请求摊到多个服务上（避免总打第一个，也避免单个服务把我们限流）。</summary>
        private int _proxiedStart;

        /// <summary>
        /// 「换节点后可点刷新」提示的当前状态。
        /// <para>判定原则（2026-11-17 两轮反馈后定）：**开关代理要"立刻"反映**，不能挂粘滞 ——
        /// 关掉代理后提示还赖着 5 秒，比闪一下更让人困惑。所以改成"二次确认"：
        /// 第一遍拿不到就立刻用注册表再读一次（很便宜），仍然没有就认定为真关了。</para>
        /// </summary>
        private bool _proxyHintOn;

        /// <summary>
        /// 探测一次并平滑：成功 → 入窗、取中位数、stale=false；失败 → 沿用上次有效值、stale=true。
        /// 为什么取中位数而不是平均：ICMP 偶发尖峰（几十 ms）会把平均值拉高，中位数不受影响。
        /// why keep-last：一次超时就显示 "—" 会让延迟看起来忽有忽无，用户无法判断"是真的差还是探测失败"
        /// （2026-11-16 反馈）。显示 "~12 ms" 表示"上次测到的值，本次未取到"。
        /// </summary>
        private long ProbeSmooth(string hostOrAddress,
                                        System.Collections.Generic.Queue<long> ring,
                                        ref long last, out bool stale, out bool viaTcp,
                                        bool allowTcpFallback)
        {
            viaTcp = false;
            long rtt = string.IsNullOrEmpty(hostOrAddress) ? -1 : ProbeRttMs(hostOrAddress);
            if (rtt >= 0)
            {
                ring.Enqueue(rtt);
                while (ring.Count > 3) ring.Dequeue();
                var arr = ring.ToArray();
                System.Array.Sort(arr);
                last = arr[arr.Length / 2];
                stale = false;
                return last;
            }

            // ICMP 没回包。两种处理：
            //   ① 历史上有过 ICMP 成功 → 说明只是这次丢了，沿用上次值并标 stale（口径保持一致）；
            //   ② 从来没有过 ICMP 成功 → 多半是网络/防火墙整体拦 ICMP（虚拟机 NAT 很常见），
            //      改用 TCP 443 握手量一次，给个能看的数（口径不同，UI 会显示 "≈"）。
            if (ring.Count == 0 && allowTcpFallback)
            {
                long tcp = ProbeTcpMs(hostOrAddress, 443);
                if (tcp >= 0) { stale = false; viaTcp = true; return tcp; }
            }

            stale = true;
            if (last < 0 && _rttFailLogs < 3)
            {
                _rttFailLogs++;
                Logger.Debug($"延迟探测失败且从无成功值（host={hostOrAddress}）——界面显示 —，下一拍继续试");
            }
            return last;      // -1 = 从未成功过（UI 显示 —）
        }

        /// <summary>
        /// 用 TCP 三次握手量延迟（只作 ICMP 的兜底）：
        /// <c>ConnectAsync</c> 到 443，等 <see cref="TcpProbeTimeoutMs"/>，成功则取握手耗时。
        /// 不依赖 ICMP 是否被拦；对目标就是一次正常的 HTTPS 连接，随即关闭。
        /// </summary>
        private static long ProbeTcpMs(string host, int port)
        {
            System.Net.Sockets.TcpClient client = null;
            try
            {
                client = new System.Net.Sockets.TcpClient();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var task = client.ConnectAsync(host, port);

                if (!task.Wait(TcpProbeTimeoutMs))
                {
                    // **超时不能在这里 Dispose**（2026-11-16 崩溃修复）：
                    // 连接任务还在飞，client 一释放，任务完成时 EndConnect 会读到空状态抛
                    // NullReferenceException；而那个异常没有 await 观察，终结器线程会重新
                    // 抛出 → 「程序遇到未处理的异常」直接弹崩溃框。
                    // 正确做法：挂一个续体，等任务真结束再观察异常 + 释放。
                    var c2 = client;
                    client = null;                       // 交给续体释放，避免 finally 重复 Dispose
                    task.ContinueWith(tt =>
                    {
                        var observed = tt.Exception;     // 显式观察，消除“未观察的任务异常”
                        try { c2.Dispose(); } catch { }
                    }, System.Threading.CancellationToken.None,
                       System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                       System.Threading.Tasks.TaskScheduler.Default);
                    return -1;
                }

                sw.Stop();
                return client.Connected ? sw.ElapsedMilliseconds : -1;
            }
            catch { return -1; }
            finally
            {
                try { client?.Dispose(); } catch { }
            }
        }

        private long ProbeRttMs(string hostOrAddress)
        {
            try
            {
                using (var p = new Ping())
                {
                    _pingInFlight = p;                       // 退出时 Dispose 它，打断阻塞
                    try
                    {
                        PingReply reply = p.Send(hostOrAddress, PingTimeoutMs);
                        return reply != null && reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
                    }
                    finally { _pingInFlight = null; }
                }
            }
            catch { return -1; }
        }

        // ── 地区：复用 cip.cc 的「地址 / 运营商」，按 IP 缓存 10 分钟 ──────────────────
        // 只在 IP 变了或缓存过期时查一次；失败一律静默（地区是锦上添花，不能影响 IP 显示）。
        // 响应是键值纯文本（制表符分隔）：
        //     IP<TAB>: 182.33.158.242
        //     地址<TAB>: 中国 山东
        //     运营商<TAB>: 电信
        private const int GeoCacheMinutes = 10;
        private const int GeoTimeoutMs = 2500;
        /// <summary>地区查询失败后的重试间隔（成功则按 GeoCacheMinutes 缓存）。</summary>
        private const int GeoRetryMs = 30_000;

        private void MaybeFetchGeo(long nowMs)
        {
            if (!_publicIpLookupEnabled) return;
            if (_publicIp == null) return;
            bool sameIp = string.Equals(_geoIp, _publicIp, StringComparison.Ordinal);
            if (sameIp && nowMs - _geoFetchedAtMs < GeoCacheMinutes * 60_000L) return;

            string label = FetchGeo(_publicIp);
            if (label == null)
            {
                // **失败不要占满 10 分钟缓存**（2026-11-16 实测）：cip.cc 会间歇返回 503，
                // 以前一次失败就把 _geoFetchedAtMs 也写掉 → 接下来 10 分钟都不再查，
                // 用户看到的是「地区那一行永远空着」。改成失败只退避 30 秒。
                _geoFetchedAtMs = nowMs - GeoCacheMinutes * 60_000L + GeoRetryMs;
                if (_geoFailLogs < 3)
                {
                    _geoFailLogs++;
                    Logger.Debug($"地区查询失败（IP={_publicIp}）——{GeoRetryMs / 1000} 秒后重试");
                }
                return;
            }
            _geoFetchedAtMs = nowMs;                  // 成功才记满 10 分钟
            _geoIp = _publicIp;
            _geoLabel = label;
        }

        /// <summary>「中国 山东 · 电信」；解析不到返回 null。</summary>
        // 地区查询：三级链，按「速度 + 中文质量」排序（2026-11-16 实测数据）：
        //   pconline 0.22s 简体（省+市+运营商）→ cip.cc 0.6s 简体但间歇 503 → ipwho.is 0.8s 繁体兜底
        // 被淘汰的：ip-api.com（连测两次 8 秒超时、且免费版仅 HTTP）、api.vore.top（超时）、
        //           ip.cn（302 跳转）、ip.sb（全英文）。
        private string FetchGeo(string ip)
        {
            string label = FetchGeoPconline(ip);
            if (!string.IsNullOrEmpty(label)) return label;
            label = FetchGeoCipCc(ip);
            if (!string.IsNullOrEmpty(label)) return label;
            return FetchGeoIpWhoIs(ip);
        }

        /// <summary>
        /// 公网 IP / 地区这类「必须反映当前出口」的请求，统一这样配：
        /// <para>
        /// <b>KeepAlive = false</b>（发 Connection: close）是关键（2026-11-16 实测）：
        /// 加速器「换节点」不改变本地代理端口，连接池里的旧 TCP 连接仍绑在**旧节点**的会话上，
        /// 复用它会一直返回上一个出口 IP —— 表现就是「换了节点、点了刷新，IP 还是老的，
        /// 只有重启程序才变」。每次新建连接才能真正走新节点。
        /// </para>
        /// 同时禁掉 HTTP 缓存（有的公共 IP 服务会被中间层缓存）。
        /// </summary>
        private static void ConfigureProbeRequest(HttpWebRequest req, bool bypassProxy)
        {
            req.Timeout = HttpTimeoutMs;
            req.ReadWriteTimeout = HttpTimeoutMs;
            req.UserAgent = "task_monitor";
            req.KeepAlive = false;          // ← 不复用连接，等价于 Connection: close
            req.Pipelined = false;
            req.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            req.Headers[HttpRequestHeader.Pragma] = "no-cache";
            if (bypassProxy) req.Proxy = null;
        }

        /// <summary>
        /// 主源：太平洋电脑网。最快且直接给简体「省+市+运营商」：
        /// <c>{"ip":"...","pro":"&lt;省&gt;","city":"&lt;市&gt;","addr":"&lt;省&gt;&lt;市&gt; &lt;运营商&gt;"}</c>
        /// 注意返回 **GBK** 编码，必须按 936 解码，否则中文全乱。
        /// </summary>
        private string FetchGeoPconline(string ip)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(
                    "https://whois.pconline.com.cn/ipJson.jsp?ip=" + Uri.EscapeDataString(ip) + "&json=true");
                ConfigureProbeRequest(req, bypassProxy: false);
                req.Timeout = GeoTimeoutMs;
                req.ReadWriteTimeout = GeoTimeoutMs;
                _reqGeoInFlight = req;
                string body;
                using (var resp = req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(),
                                                     System.Text.Encoding.GetEncoding(936)))
                    body = reader.ReadToEnd();

                if (!string.IsNullOrEmpty(JsonValue(body, "err"))) return null;
                string addr = JsonValue(body, "addr");     // 「<省><市> <运营商>」
                string pro = JsonValue(body, "pro");
                if (string.IsNullOrEmpty(addr))
                {
                    if (string.IsNullOrEmpty(pro)) return null;
                    addr = pro;                     // 只保留省，不拼市
                }
                // **只取到省，不取市**（2026-11-16 实测结论，注意：此处不记录具体地理位置）：
                // 对同一条宽带出口，多个公开库给出的"市"互不相同、且都与实际位置不符 ——
                // 原因是运营商 IP 池按地市共享、各库更新口径不同。市级结论不可靠，
                // 摆在界面上反而显得程序不准；而"省 + 运营商"在各库之间是一致的。
                int sp = addr.LastIndexOf(' ');
                string isp = sp > 0 ? addr.Substring(sp + 1).Trim() : null;
                string place = string.IsNullOrEmpty(pro) ? null : pro.Trim();
                if (string.IsNullOrEmpty(place))
                {
                    // pro 缺失时退回 addr 的地址段（同样丢掉“市”）
                    place = sp > 0 ? addr.Substring(0, sp).Trim() : addr.Trim();
                }
                if (string.IsNullOrEmpty(place)) return null;
                return ComposeGeoLabel(place, isp);
            }
            catch { return null; }
            finally { _reqGeoInFlight = null; }
        }

        /// <summary>
        /// 拼「地区 · 运营商」。**国名只在国外时保留**：
        /// 国内 IP 显示「山东省 · 电信」就够了，前面加「中国」是噪音；
        /// 但走代理 / 加速器时出口可能是国外（实测 8.8.8.8 → 美国 加利福尼亚州），
        /// 这时必须带国名，否则「加利福尼亚州 · 谷歌公司」看不出在哪个国家。
        /// </summary>
        private static string ComposeGeoLabel(string place, string isp)
        {
            if (string.IsNullOrEmpty(place)) return null;
            string p = place.Trim();
            if (p.StartsWith("中国 ", StringComparison.Ordinal)) p = p.Substring(3);
            else if (p.Equals("中国", StringComparison.Ordinal)) p = "";
            if (p.Length == 0) p = place.Trim();
            return string.IsNullOrEmpty(isp) ? p : p + " · " + isp;
        }

        /// <summary>末级兜底：ipwho.is（免 key、字段全；中文偏繁体，故排在最后）。</summary>
        private string FetchGeoIpWhoIs(string ip)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(
                    "https://ipwho.is/" + Uri.EscapeDataString(ip) + "?lang=zh-CN");
                ConfigureProbeRequest(req, bypassProxy: false);
                req.Timeout = GeoTimeoutMs;
                req.ReadWriteTimeout = GeoTimeoutMs;
                _reqGeoInFlight = req;
                string body;
                using (var resp = req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream()))
                    body = reader.ReadToEnd();
                if (body.IndexOf("\"success\":false", StringComparison.Ordinal) >= 0) return null;

                string country = JsonValue(body, "country");
                string region = JsonValue(body, "region");
                string place = ((country ?? "") + (string.IsNullOrEmpty(region) ? "" : " " + region)).Trim();
                if (place.Length == 0) return null;
                return place;
            }
            catch { return null; }
            finally { _reqGeoInFlight = null; }
        }

        /// <summary>从极简 JSON 里取一个字符串字段（不引 JSON 库，够用即可）。</summary>
        private static string JsonValue(string body, string key)
        {
            int i = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int colon = body.IndexOf(':', i);
            if (colon < 0) return null;
            int q1 = body.IndexOf('"', colon);
            if (q1 < 0) return null;
            int q2 = body.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return body.Substring(q1 + 1, q2 - q1 - 1).Trim();
        }

        private string FetchGeoCipCc(string ip)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://cip.cc/" + Uri.EscapeDataString(ip));
                req.Timeout = GeoTimeoutMs;
                req.ReadWriteTimeout = GeoTimeoutMs;
                req.UserAgent = "task_monitor";
                _reqGeoInFlight = req;                        // 退出时 Abort 它
                string body;
                using (var resp = req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream()))
                    body = reader.ReadToEnd();

                string addr = ExtractField(body, "地址");
                string isp = ExtractField(body, "运营商");
                // 「国家 省 [市]」→ 只保留国家 + 省：市级在国内外都不可靠（见下方说明）
                if (addr != null)
                {
                    string[] parts = addr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    addr = parts.Length >= 2 ? parts[0] + " " + parts[1]
                         : parts.Length == 1 ? parts[0] : null;
                }
                if (string.IsNullOrWhiteSpace(addr)) return null;
                addr = addr.Trim();
                isp = isp == null ? null : isp.Trim();
                return string.IsNullOrWhiteSpace(isp) ? addr : addr + " · " + isp;
            }
            catch { return null; }
            finally { _reqGeoInFlight = null; }
        }

        // 从「键 : 值」里取值：cip.cc 用制表符分隔，冒号两侧空格数量不固定。
        private static string ExtractField(string body, string key)
        {
            string[] lines = body.Split('\n');
            foreach (string line in lines)
            {
                int i = line.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) continue;
                int colon = line.IndexOf(':', i + key.Length);
                if (colon < 0) continue;
                return line.Substring(colon + 1).Trim();
            }
            return null;
        }

        // First endpoint that answers with an IPv4 literal wins; null when all fail. Never throws.
        private string FetchPublicIp(bool viaProxy)
        {
            string[] list = viaProxy ? PublicIpEndpointsProxied : PublicIpEndpointsDirect;
            // 经代理时**轮换起点**：把请求摊到多个服务上（每个约 1/N），
            // 既减轻单一免费服务的压力，也避免某一家把我们限流后就一直失败（2026-11-17）。
            // 直连列表只有 3~4 个且顺序有意义（快的在前），不做轮换。
            int start = viaProxy ? (_proxiedStart++ & int.MaxValue) % list.Length : 0;
            for (int k = 0; k < list.Length; k++)
            {
                string endpoint = list[(start + k) % list.Length];
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(endpoint);
                    ConfigureProbeRequest(req, bypassProxy: false);
                    _reqV4InFlight = req;            // 退出时 Abort 它
                    using (var resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream()))
                    {
                        // The body is the bare address — validate it anyway rather than trust it.
                        var m = Regex.Match(reader.ReadToEnd(), @"\d{1,3}(?:\.\d{1,3}){3}");
                        if (m.Success && IPAddress.TryParse(m.Value, out _)) return m.Value;
                    }
                }
                catch { /* try the next endpoint */ }
                finally { _reqV4InFlight = null; }
            }
            return null;
        }

        // First endpoint that answers with an IPv6 literal wins; null when all fail. Unlike
        // the v4 path the whole (trimmed) body must BE the address — a regex can't
        // distinguish a v6 literal from surrounding hex-ish text reliably. Never throws.
        private string FetchPublicIpV6()
        {
            foreach (string endpoint in PublicIpV6Endpoints)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(endpoint);
                    ConfigureProbeRequest(req, bypassProxy: true);   // v6 查询必须直连，见下方说明
                    // v6 查询必须绕开系统代理：HTTP 代理只转发 v4（CONNECT 到 v4 的域名），
                    // 走代理时 v6 端点必然失败 —— 而失败时 _publicIpV6 会保留旧值，
                    // 表现为「公网 IPv6 一直是那个地址，从不更新」。直连 v6 才是它的真实归属。
                    req.Proxy = null;
                    _reqV6InFlight = req;                    // 退出时 Abort 它
                    using (var resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream()))
                    {
                        string body = reader.ReadToEnd().Trim();
                        if (IPAddress.TryParse(body, out IPAddress addr)
                            && addr.AddressFamily == AddressFamily.InterNetworkV6)
                            return addr.ToString();   // canonical (compressed) form
                    }
                }
                catch { /* try the next endpoint */ }
                finally { _reqV6InFlight = null; }
            }
            return null;
        }

        // DOT11_PHY_TYPE → the marketing name (7=n, 8=ac, 10=ax, 11=be). 802.11ax over 6 GHz
        // is "Wi-Fi 6E" — indistinguishable from the PHY type alone, so it shows Wi-Fi 6.
        private static string PhyTypeToString(int phyType)
        {
            switch (phyType)
            {
                case 7: return "Wi-Fi 4 (802.11n)";    // dot11_phy_type_ht
                case 8: return "Wi-Fi 5 (802.11ac)";   // dot11_phy_type_vht
                case 10: return "Wi-Fi 6 (802.11ax)";  // dot11_phy_type_he
                case 11: return "Wi-Fi 7 (802.11be)";  // dot11_phy_type_eht
                case 9: return "802.11ad";             // dot11_phy_type_dmg
                case 4: return "802.11a";              // dot11_phy_type_ofdm
                case 5: return "802.11b";              // dot11_phy_type_hrdsss
                case 6: return "802.11g";              // dot11_phy_type_erp
                default: return "802.11";
            }
        }

        private static string BandToString(uint khz)
        {
            if (khz == 0) return null;
            if (khz < 3_000_000) return "2.4 GHz";
            if (khz < 5_925_000) return "5 GHz";
            return "6 GHz";
        }
    }
}
