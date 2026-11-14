using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Common.IconKeys;
using iNKORE.UI.WPF.Modern.Controls;

namespace task_monitor
{
    /// <summary>
    /// The settings window — a WinUI-look shell (iNKORE modern window style + Mica)
    /// whose left pane is an iNKORE NavigationView, one item per 类别 (Win11 Settings
    /// style; PaneDisplayMode Auto — labeled ≥1008 DIP, icon-compact 641–1007, minimal
    /// hamburger below): 通用 (开机自启动 / 采样间隔 / 检查更新 / 更新源), 外观 (主题 / 任务栏位置开关 ——
    /// 一个展开项，里面的子项是任务栏位置；该项不存在时则退化成一张普通卡片，
    /// i.e. on the classical Win10 taskbar — plus 紧凑模式, 显示分割线, 项目顺序 (the shared
    /// taskbar/floating drag-to-reorder list), and 悬浮模式 with its five sub-cards), 采样项目 (one expander per metric, the toggle in
    /// its content area enabling/disabling that metric's sampling; 磁盘's and GPU's
    /// expanders also hold 显示方式 + the specific-device picker, GPU's additionally the
    /// 任务栏显示 (使用率/温度/两者) trio with 温度来源 and 温度单位, 网络's the 适配器
    /// picker, the 公网 IP lookup switch and the Clash/Mihomo integration (switch +
    /// endpoint + 测试连接); plus the section-wide 合并相同程序 card); 关于 lives at the
    /// pane's bottom-left (FooterMenuItems) and is NOT a card page — a centred hero
    /// (logo/name/version pill), the app's own live CPU waveform (push-fed by
    /// <see cref="RefreshAbout"/> off App's SnapshotChanged pump — no timer here), two
    /// plain link buttons and the copyright line. Each 类别
    /// is its own TouchDragScrollViewer page, switched by VISIBILITY in
    /// <see cref="NavView_SelectionChanged"/> — NOT Frame navigation, so the window
    /// keeps one namescope: every control/handler pair (and the ctor's initial-state
    /// push into all of them, visible page or not) works exactly as the old single
    /// scroll page did, and each page keeps its own scroll position across switches.
    /// Opened from the taskbar overlay's right-click menu (App keeps at most one
    /// instance). Every card is live: initial values + change callbacks arrive via the
    /// ctor (App owns settings.yaml, the scheduled task, the app theme and the taskbar
    /// overlay).
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly Action<bool, int> _placementChanged;
        private readonly Action<IReadOnlyList<TaskbarDisplaySelection>> _displaySelectionChanged;
        private readonly Action<bool> _autoStartChanged;
        private readonly Action<int> _themeChanged;           // 0=跟随系统 1=浅色 2=深色
        private readonly Action<bool> _compactChanged;        // 紧凑模式 (the item pitch)
        private readonly Action<bool> _showSeparatorsChanged; // 显示分割线 (taskbar + floating)
        private readonly Action<bool> _showNetMonthUsageChanged; // 弹窗显示本月流量
        private readonly Action<bool> _showNetTodayUsageChanged; // 弹窗显示今日流量
        private readonly Action<bool> _netUsageFixedGbChanged;   // 累计流量固定按 GB 显示
        private readonly Action<int> _netHistoryKeepChanged;      // 多月历史保留月数（1~12）
        private readonly Action<bool> _netHistoryShowChanged;     // 是否显示「往月」那一行
        private readonly Action<bool> _overlayUsageGraphChanged; // 已用走势 (taskbar + floating)
        private readonly Action<string> _overlayGraphColorChanged; // 曲线颜色 (hex, null = 跟随系统)
        private readonly List<Button> _graphSwatches = new List<Button>();
        private readonly Action<IReadOnlyList<int>> _itemOrderChanged;
        private readonly List<MetricOrderItem> _itemOrderItems = new List<MetricOrderItem>();
        private MetricOrderItem _dragItem;
        private Point _dragStart;
        private readonly Action<bool> _floatingChanged;       // 悬浮模式 (the whole form flips)
        private readonly Action<bool> _floatingTopmostChanged; // 悬浮模式 → 置顶显示
        private readonly Action<bool> _floatingOutOfBoundsChanged; // 悬浮模式 → 允许超出屏幕边界
        private readonly Action<bool> _floatingEdgeHideChanged; // 悬浮模式 → 贴边隐藏
        private readonly Action<bool> _floatingFullscreenHideChanged; // 悬浮模式 → 全屏时隐藏
        private readonly Action<double> _floatingOpacityChanged; // 悬浮模式 → 透明度 (0.2–1.0)
        private readonly Action<int> _intervalChanged;        // ms: 500 / 1000 / 2000
        private readonly Action<int, bool> _samplingChanged;  // (overlay hit slot 0–4, enabled)
        private readonly Action<bool> _mergeSamePathChanged;  // 合并相同程序
        private readonly Action<int, int> _diskDisplayChanged; // (mode 0=平均 1=最高 2=特定, PhysicalDrive index)
        private readonly Action<int, int> _gpuDisplayChanged;  // (mode 0=平均 1=最高 2=特定, "GPU N" number)
        private readonly Action<int, int, bool> _gpuTaskbarContentChanged; // (content 0=使用率 1=温度 2=两者, 温度来源 -1=最高, 华氏度)
        private readonly Action<string, string> _netAdapterChanged; // (adapter Id or "" = 自动, display name)
        private readonly Action<bool> _publicIpLookupChanged;       // 公网 IP lookup
        private readonly Action<bool, string, string> _clashApiChanged; // (enabled, controller address or "" = default, API secret)
        private readonly Action<bool> _updateCheckChanged;  // 检查更新
        private readonly Action<int> _updateSourceChanged;  // 0=CNB 1=GitHub
        // Reads SamplingController's latest snapshot — the About page's live
        // CPU waveform; the same single-source push model as the detail windows.
        private readonly Func<SystemSnapshot> _snapshotProvider;
        // 500ms single-shot debounce for the two Clash text boxes — a yaml write +
        // sampler retarget must not fire on every keystroke.
        private DispatcherTimer _clashDebounce;
        // Guards the event handlers while the ctor pushes the initial state into the
        // controls (setting IsOn / SelectedIndex fires Toggled / SelectionChanged).
        private bool _loaded;

        // 横排 + 「左对齐」时位置选择被锁死（没有任何横向空位）；_suppressToggle 用来抑制
        // 程序自己改 IsOn 时触发的 Toggled（否则会被当成用户操作、又弹一次窗）。
        private bool _anchorLocked;
        private bool _suppressToggle;

        // 上一次判定的 (竖直, 居中)。null = 还没判过。RefreshAnchorAvailability 只在这个值
        // 变化时重设界面，1 秒一次的轮询才不会把 Description 反复刷一遍。
        private (bool vertical, bool centered)? _alignCache;
        private DispatcherTimer _alignWatch;

        // 用户真正选的位置开关状态（= settings.yaml 里的值）。锁定期间界面被强制显示成「关」，
        // 但配置**不写**；解锁时按它恢复界面并通知覆盖层重新按 anchor 摆放 —— 否则会出现
        // 「界面显示关、程序却按开摆放」的分裂（2026-11-10）。用户在解锁状态下自己拨开关时同步更新。
        private bool _configuredOnLeft;
        // 任务栏位置开关挂在哪：Win11 用展开项，经典（Win10）任务栏用普通卡片
        // taskbar (see the ctor) — only the one actually shown gets enabled/disabled.
        private readonly Control _onLeftHost;

        private sealed class MetricOrderItem
        {
            public int Slot { get; set; }
            public string Name { get; set; }
        }

        /// <param name="overlayOnLeft">任务栏位置开关：开 = 按 anchor 摆放，关 = 固定最左端
        /// <param name="anchorIndex">当前任务栏位置：下拉的选中索引
        /// （旧配置的回退逻辑见 App.AnchorIndexOf）。</param>
        /// <param name="placementChanged">每次用户改动都回报 (onLeft, anchor)；anchor 是下拉选中索引。</param>
        /// <param name="autoStartOn">Whether the logon scheduled task currently exists.</param>
        /// <param name="autoStartChanged">Reported with the requested on/off; on a schtasks
        /// failure App calls <see cref="SyncAutoStart"/> to snap the toggle back.</param>
        /// <param name="themeIndex">0=跟随系统 (yaml null) 1=浅色 2=深色.</param>
        /// <param name="compactLayout">Current 紧凑模式 state (yaml null = the off default).</param>
        /// <param name="compactLayoutChanged">Reported with the requested on/off — applied
        /// live (the overlay re-lays out and resizes at once; both forms follow).</param>
        /// <param name="showSeparators">Current 显示分割线 state (yaml null = the on default).</param>
        /// <param name="showSeparatorsChanged">Reported with the requested on/off — redraws
        /// both overlay forms without changing their layout.</param>
        /// <param name="itemOrder">Current slot order, shared by the taskbar and floating forms.</param>
        /// <param name="itemOrderChanged">Reported once after a drag is dropped or the order is reset.</param>
        /// <param name="floatingMode">Current 悬浮模式 state (yaml null = the off default).</param>
        /// <param name="floatingChanged">Reported with the requested on/off — App rebuilds the
        /// overlay window in its other form (taskbar child ⟷ desktop widget).</param>
        /// <param name="floatingTopmost">Current 置顶显示 state (yaml null = the on default);
        /// floating form only.</param>
        /// <param name="floatingTopmostChanged">Reported with the requested on/off — applied
        /// live (no rebuild).</param>
        /// <param name="floatingEdgeHide">Current 贴边隐藏 state (yaml null = the off
        /// default); floating form only.</param>
        /// <param name="floatingEdgeHideChanged">Reported with the requested on/off — applied
        /// live (the taskbar thread re-derives the dock from the home position).</param>
        /// <param name="floatingFullscreenHide">Current 全屏时隐藏 state (yaml null = the on
        /// default); floating form only.</param>
        /// <param name="floatingFullscreenHideChanged">Reported with the requested on/off —
        /// applied live (the taskbar thread's fullscreen probe runs at once, then per
        /// tick).</param>
        /// <param name="floatingOpacity">Current 透明度 as 0.2–1.0 (yaml null = 1.0, the
        /// default); floating form only.</param>
        /// <param name="floatingOpacityChanged">Reported with the new 0.2–1.0 opacity on every
        /// slider step — App re-tints the widget live and persists debounced.</param>
        /// <param name="intervalMs">Current sampling interval (500/1000/2000).</param>
        /// <param name="intervalChanged">Reported with the new interval in ms.</param>
        /// <param name="samplingMask">Current per-metric sampling mask (SystemSampler.Mask*
        /// bits, in overlay hit-slot order).</param>
        /// <param name="samplingChanged">Reported with (slot, enabled) on every 采样 toggle.</param>
        /// <param name="mergeSamePath">Current 合并相同程序 state (yaml null = the on default).</param>
        /// <param name="diskDisplayIndex">Current 磁盘显示方式 combo index: 0=所有磁盘平均
        /// (yaml null) 1=最高利用率 2=特定磁盘.</param>
        /// <param name="pickedDiskIndex">The PhysicalDrive index the 特定磁盘 picker shows
        /// selected (yaml null = disk 0).</param>
        /// <param name="disks">The currently present physical disks (from the latest
        /// snapshot; null/empty when disk sampling is off) — the picker's items.</param>
        /// <param name="diskDisplayChanged">Reported with (modeIndex, PhysicalDrive index)
        /// on every 显示方式 / 特定磁盘 change.</param>
        /// <param name="gpuDisplayIndex">Current GPU 显示方式 combo index: 0=所有 GPU 平均
        /// 1=最高利用率 (yaml null, the default) 2=特定 GPU.</param>
        /// <param name="pickedGpuIndex">The "GPU N" number the 特定 GPU picker shows selected
        /// (yaml null = GPU 0).</param>
        /// <param name="gpus">The currently present GPU adapters (from the latest snapshot;
        /// null/empty when GPU sampling is off) — the picker's items.</param>
        /// <param name="gpuDisplayChanged">Reported with (modeIndex, "GPU N" number) on
        /// every 显示方式 / 特定 GPU change.</param>
        /// <param name="gpuContentIndex">0=使用率 (yaml null, the default) 1=温度
        /// 2=使用率+温度 — the 任务栏显示 combo.</param>
        /// <param name="pickedGpuTempIndex">The 温度来源 pick: -1 = 最高温度 (yaml null,
        /// the default), ≥0 = that "GPU N" number.</param>
        /// <param name="gpuTempUnitIndex">0=摄氏度 (yaml null, the default) 1=华氏度.</param>
        /// <param name="gpuTaskbarContentChanged">Reported with (contentIndex, 温度来源
        /// pick, 华氏度) on every 任务栏显示 / 温度来源 / 温度单位 change.</param>
        /// <param name="netAdapterId">The pinned adapter's NetworkInterface.Id (GUID);
        /// null/empty = 自动.</param>
        /// <param name="netAdapterName">The pinned adapter's display name, for the
        /// （未连接） placeholder when it's absent.</param>
        /// <param name="adapters">Every non-loopback adapter (Id + "Description (Name)"
        /// label), enumerated by App on settings open — the combo's items.</param>
        /// <param name="netAdapterChanged">Reported with (adapter Id or "" = 自动, its
        /// display name) on every 适配器 change.</param>
        /// <param name="publicIpLookupOn">The 公网 IP lookup switch state (yaml null = the
        /// on default).</param>
        /// <param name="publicIpLookupChanged">Reported with the requested on/off — takes
        /// effect live (the poll thread drops its cached address and stops BOTH the HTTP
        /// lookups and the 公网延迟 ICMP probe).</param>
        /// <param name="clashEnabled">The Clash/Mihomo integration switch state (yaml
        /// null = the on default).</param>
        /// <param name="clashApiAddress">The Clash/Mihomo controller address
        /// (host:port); null/empty = the 127.0.0.1:9090 default.</param>
        /// <param name="clashApiSecret">The controller's API secret; null/empty = none.</param>
        /// <param name="clashApiChanged">Reported with (enabled, address or "" = default,
        /// secret) — the switch reports immediately, text edits debounced 500ms.</param>
        /// <param name="updateCheckOn">Current 检查更新 state (yaml null = the on default).</param>
        /// <param name="updateCheckChanged">Reported with the requested on/off — takes
        /// effect on the next startup (the check runs once per launch).</param>
        /// <param name="updateSourceIndex">0=CNB (yaml null, the default) 1=GitHub.</param>
        /// <param name="snapshotProvider">Reads TaskbarWindow's LatestSnapshot (App's
        /// lambda) — the About page's live CPU waveform.</param>
        // internal: the signature mentions internal sampler types — App (same assembly) is
        // the only caller; the window is never instantiated from XAML.
        internal SettingsWindow(bool overlayOnLeft, int anchorIndex, Action<bool, int> placementChanged,
            bool snapToStartSupported,
            IReadOnlyList<TaskbarDisplayInfo> displays,
            IReadOnlyList<TaskbarDisplaySelection> selectedDisplays,
            Action<IReadOnlyList<TaskbarDisplaySelection>> displaySelectionChanged,
            bool autoStartOn, Action<bool> autoStartChanged,
            int themeIndex, Action<int> themeChanged,
            bool compactLayout, Action<bool> compactLayoutChanged,
            bool showSeparators, Action<bool> showSeparatorsChanged,
            bool showNetMonthUsage, Action<bool> showNetMonthUsageChanged,
            bool showNetTodayUsage, Action<bool> showNetTodayUsageChanged,
            bool netUsageFixedGb, Action<bool> netUsageFixedGbChanged,
            int netHistoryKeepMonths, Action<int> netHistoryKeepChanged,
            bool netHistoryShow, Action<bool> netHistoryShowChanged,
            bool overlayUsageGraph, Action<bool> overlayUsageGraphChanged,
            string overlayGraphColor, Action<string> overlayGraphColorChanged,
            IReadOnlyList<int> itemOrder, Action<IReadOnlyList<int>> itemOrderChanged,
            bool floatingMode, Action<bool> floatingChanged,
            bool floatingTopmost, Action<bool> floatingTopmostChanged,
            bool floatingAllowOutOfBounds, Action<bool> floatingAllowOutOfBoundsChanged,
            bool floatingEdgeHide, Action<bool> floatingEdgeHideChanged,
            bool floatingFullscreenHide, Action<bool> floatingFullscreenHideChanged,
            double floatingOpacity, Action<double> floatingOpacityChanged,
            int intervalMs, Action<int> intervalChanged,
            int samplingMask, Action<int, bool> samplingChanged,
            bool mergeSamePath, Action<bool> mergeSamePathChanged,
            int diskDisplayIndex, int pickedDiskIndex, IReadOnlyList<DiskInfo> disks,
            Action<int, int> diskDisplayChanged,
            int gpuDisplayIndex, int pickedGpuIndex, IReadOnlyList<GpuInfo> gpus,
            Action<int, int> gpuDisplayChanged,
            int gpuContentIndex, int pickedGpuTempIndex, int gpuTempUnitIndex,
            Action<int, int, bool> gpuTaskbarContentChanged,
            string netAdapterId, string netAdapterName,
            IReadOnlyList<(string Id, string Label)> adapters,
            Action<string, string> netAdapterChanged,
            bool publicIpLookupOn, Action<bool> publicIpLookupChanged,
            bool clashEnabled, string clashApiAddress, string clashApiSecret,
            Action<bool, string, string> clashApiChanged,
            bool updateCheckOn, Action<bool> updateCheckChanged,
            int updateSourceIndex, Action<int> updateSourceChanged,
            Func<SystemSnapshot> snapshotProvider)
        {
            InitializeComponent();
            // 版本水印（「通用」页底部那行淡字）：用运行时版本号，改版本不必动 XAML。
            VersionWatermark.Text = "个人修改版 v" + VersionInfo.Current
                + " · 非上游发布 · 上游 linesoft2/TaskMonitor（Apache-2.0）";
            _placementChanged = placementChanged;
            _displaySelectionChanged = displaySelectionChanged;
            _autoStartChanged = autoStartChanged;
            _themeChanged = themeChanged;
            _compactChanged = compactLayoutChanged;
            _showSeparatorsChanged = showSeparatorsChanged;
            _showNetMonthUsageChanged = showNetMonthUsageChanged;
            _showNetTodayUsageChanged = showNetTodayUsageChanged;
            _netUsageFixedGbChanged = netUsageFixedGbChanged;
            _netHistoryKeepChanged = netHistoryKeepChanged;
            _netHistoryShowChanged = netHistoryShowChanged;
            _overlayUsageGraphChanged = overlayUsageGraphChanged;
            _overlayGraphColorChanged = overlayGraphColorChanged;
            _itemOrderChanged = itemOrderChanged;
            _floatingChanged = floatingChanged;
            _floatingTopmostChanged = floatingTopmostChanged;
            _floatingOutOfBoundsChanged = floatingAllowOutOfBoundsChanged;
            _floatingEdgeHideChanged = floatingEdgeHideChanged;
            _floatingFullscreenHideChanged = floatingFullscreenHideChanged;
            _floatingOpacityChanged = floatingOpacityChanged;
            _intervalChanged = intervalChanged;
            _samplingChanged = samplingChanged;
            _mergeSamePathChanged = mergeSamePathChanged;
            _diskDisplayChanged = diskDisplayChanged;
            _gpuDisplayChanged = gpuDisplayChanged;
            _gpuTaskbarContentChanged = gpuTaskbarContentChanged;
            _netAdapterChanged = netAdapterChanged;
            _publicIpLookupChanged = publicIpLookupChanged;
            _clashApiChanged = clashApiChanged;
            _updateCheckChanged = updateCheckChanged;
            _updateSourceChanged = updateSourceChanged;
            _snapshotProvider = snapshotProvider;

            OnLeftSwitch.IsOn = overlayOnLeft;
            _configuredOnLeft = overlayOnLeft;       // 锁定期间界面会被改成「关」，解锁时按它恢复
            AnchorCombo.SelectedIndex = anchorIndex < 0 || anchorIndex > 2 ? 0 : anchorIndex;
            AnchorCombo.IsEnabled = overlayOnLeft;   // the anchor only matters while the switch is on
            RefreshAnchorAvailability();
            // Classical (Win10) taskbar: 靠左 has a single spot (the task-buttons band's
            // 只有靠近开始按钮那一处），所以「任务栏位置」这个子项不存在，展开项会变成一个
            // 点开什么都没有的箭头 —— 此时「任务栏位置开关」改成渲染成普通卡片
            // (OnLeftCard). Its icon and its ToggleSwitch MOVE over, so there is exactly
            // one switch and one piece of state; the expander's two properties are
            // cleared first, or its template would claim the same children the moment it
            // applies.
            _onLeftHost = OnLeftExpander;
            if (!snapToStartSupported)
            {
                var icon = OnLeftExpander.HeaderIcon;
                OnLeftExpander.HeaderIcon = null;
                OnLeftExpander.Content = null;
                OnLeftCard.HeaderIcon = icon;
                OnLeftCard.Content = OnLeftSwitch;
                OnLeftExpander.Visibility = Visibility.Collapsed;
                OnLeftCard.Visibility = Visibility.Visible;
                _onLeftHost = OnLeftCard;
            }
            PopulateDisplayPicker(displays, selectedDisplays);
            AutoStartSwitch.IsOn = autoStartOn;
            ThemeCombo.SelectedIndex = themeIndex < 0 || themeIndex > 2 ? 0 : themeIndex;
            CompactSwitch.IsOn = compactLayout;
            ShowSeparatorsSwitch.IsOn = showSeparators;
            ShowMonthUsageSwitch.IsOn = showNetMonthUsage;
            ShowTodayUsageSwitch.IsOn = showNetTodayUsage;
            FixedGbSwitch.IsOn = netUsageFixedGb;
            SelectNetHistoryKeep(netHistoryKeepMonths);
            NetHistoryShowSwitch.IsOn = netHistoryShow;
            OverlayUsageGraphSwitch.IsOn = overlayUsageGraph;
            PopulateGraphSwatches(overlayGraphColor);
            PopulateItemOrder(itemOrder);
            FloatingSwitch.IsOn = floatingMode;
            FloatingTopmostSwitch.IsOn = floatingTopmost;
            // 置顶显示 only means anything in the floating form (the embedded overlay inherits
            // 嵌在任务栏那条带子里），所以它跟着模式开关走 —— 和「任务栏位置」同一个形态。
            FloatingTopmostCard.IsEnabled = floatingMode;
            FloatingOutOfBoundsSwitch.IsOn = floatingAllowOutOfBounds;
            FloatingOutOfBoundsCard.IsEnabled = floatingMode;
            // …and so does 贴边隐藏 (the embedded overlay is anchored to the taskbar; it has
            // no edges to dock to).
            FloatingEdgeHideSwitch.IsOn = floatingEdgeHide;
            FloatingEdgeHideCard.IsEnabled = floatingMode;
            // …and so does 全屏时隐藏 (the embedded overlay sits in the taskbar, which a
            // fullscreen app covers on its own).
            FloatingFullscreenHideSwitch.IsOn = floatingFullscreenHide;
            FloatingFullscreenHideCard.IsEnabled = floatingMode;
            // …and so does 透明度 (the embedded overlay must match the opaque taskbar surface).
            // The slider stores whole percents (snapped); the ctor hands App back a 0.2–1.0
            // double. The readout text is set here because the guarded handler skips it.
            FloatingOpacitySlider.Value = Math.Round(Math.Min(1.0, Math.Max(0.2, floatingOpacity)) * 100);
            FloatingOpacityText.Text = $"{FloatingOpacitySlider.Value:0}%";
            FloatingOpacityCard.IsEnabled = floatingMode;
            // …and the two are mutually exclusive: a floating widget is positioned by dragging,
            // 由悬浮窗自己决定），所以开着的时候「任务栏位置开关 / 任务栏位置」都不起作用。
            _onLeftHost.IsEnabled = !floatingMode;
            DisplayExpander.IsEnabled = !floatingMode;
            IntervalCombo.SelectedIndex = IntervalToIndex(intervalMs);
            CpuSwitch.IsOn = (samplingMask & SystemSampler.MaskCpu) != 0;
            RamSwitch.IsOn = (samplingMask & SystemSampler.MaskRam) != 0;
            DiskSwitch.IsOn = (samplingMask & SystemSampler.MaskDisk) != 0;
            GpuSwitch.IsOn = (samplingMask & SystemSampler.MaskGpu) != 0;
            NetSwitch.IsOn = (samplingMask & SystemSampler.MaskNet) != 0;
            MergeSwitch.IsOn = mergeSamePath;
            DiskDisplayCombo.SelectedIndex = diskDisplayIndex < 0 || diskDisplayIndex > 2 ? 0 : diskDisplayIndex;
            PopulateDiskPicker(disks, pickedDiskIndex);
            DiskPickCombo.IsEnabled = DiskDisplayCombo.SelectedIndex == 2;
            GpuDisplayCombo.SelectedIndex = gpuDisplayIndex < 0 || gpuDisplayIndex > 2 ? 1 : gpuDisplayIndex;
            PopulateGpuPicker(gpus, pickedGpuIndex);
            GpuPickCombo.IsEnabled = GpuDisplayCombo.SelectedIndex == 2;
            // 任务栏显示 + its two temperature sub-cards; the sub-cards are enabled only
            // while a temperature actually shows (the DiskPickCombo pattern).
            GpuContentCombo.SelectedIndex = gpuContentIndex < 0 || gpuContentIndex > 2 ? 0 : gpuContentIndex;
            PopulateGpuTempPicker(gpus, pickedGpuTempIndex);
            GpuTempUnitCombo.SelectedIndex = gpuTempUnitIndex == 1 ? 1 : 0;
            GpuTempPickCombo.IsEnabled = GpuContentCombo.SelectedIndex != 0;
            GpuTempUnitCombo.IsEnabled = GpuContentCombo.SelectedIndex != 0;
            PopulateNetAdapterPicker(adapters, netAdapterId, netAdapterName);
            PublicIpSwitch.IsOn = publicIpLookupOn;
            ClashSwitch.IsOn = clashEnabled;
            ClashAddressBox.Text = clashApiAddress ?? "";
            ClashSecretBox.Text = clashApiSecret ?? "";
            SetClashInputsEnabled(clashEnabled);
            UpdateCheckSwitch.IsOn = updateCheckOn;
            UpdateSourceCombo.SelectedIndex = updateSourceIndex == 1 ? 1 : 0;
            UpdateSourceCombo.IsEnabled = updateCheckOn;   // the source only matters while 检查更新 is on
            VersionText.Text = $"版本 {VersionInfo.Current}";   // AssemblyInformationalVersion — 与发版 tag 一致
            _loaded = true;

            // 主动盯着对齐方式：用户去系统设置改完不一定会点回这个窗口，只靠 Activated 会漏。
            // 1 秒一次，状态没变就立刻返回（见 RefreshAnchorAvailability）。
            RefreshAnchorAvailability();
            // 「固定按 GB 显示」的初始可用性：两个开关都关着时它该是灰的。
            RefreshNetUsageUnitAvailability();
            _alignWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _alignWatch.Tick += (s, e) => { if (_loaded) RefreshAnchorAvailability(); };
            _alignWatch.Start();
            Activated += (s, e) => { if (_loaded) RefreshAnchorAvailability(); };
            Closed += (s, e) => { _alignWatch?.Stop(); _alignWatch = null; };
        }

        private void PopulateItemOrder(IReadOnlyList<int> order)
        {
            _itemOrderItems.Clear();
            var cards = new List<object>();
            foreach (int slot in TaskbarWindow.NormalizeItemOrder(order))
            {
                var item = new MetricOrderItem { Slot = slot, Name = ItemOrderName(slot) };
                _itemOrderItems.Add(item);
                var handle = new FontIcon { Icon = FluentSystemIcons.ReOrderDotsVertical_20_Regular, FontSize = 16 };
                TouchDragScrollViewer.SetTouchDragExcluded(handle, true);
                var card = new SettingsCard
                {
                    Header = item.Name,
                    HeaderIcon = handle,
                    DataContext = item,
                    AllowDrop = true,
                    Cursor = Cursors.Hand,
                };
                card.PreviewMouseLeftButtonDown += ItemOrderCard_PreviewMouseLeftButtonDown;
                card.PreviewMouseMove += ItemOrderCard_PreviewMouseMove;
                card.DragEnter += ItemOrderCard_DragEnter;
                card.DragOver += ItemOrderCard_DragOver;
                card.Drop += ItemOrderCard_Drop;
                cards.Add(card);
            }
            // SettingsExpander's ItemsRepeater observes replacement, not mutations of the old list.
            ItemOrderExpander.Items = cards;
        }

        private static string ItemOrderName(int slot)
        {
            switch (slot)
            {
                case 0: return "CPU";
                case 1: return "内存";
                case 2: return "磁盘";
                case 3: return "GPU";
                case 4: return "网络";
                default: return string.Empty;
            }
        }

        private int[] CurrentItemOrder()
        {
            var order = new int[_itemOrderItems.Count];
            for (int i = 0; i < order.Length; i++) order[i] = _itemOrderItems[i].Slot;
            return order;
        }

        private void ReportItemOrder() => _itemOrderChanged?.Invoke(CurrentItemOrder());

        private void ResetItemOrder_Click(object sender, RoutedEventArgs e)
        {
            PopulateItemOrder(null);
            ReportItemOrder();
        }

        private static SettingsCard ItemOrderCardFrom(DependencyObject source)
        {
            while (source != null)
            {
                if (source is SettingsCard card) return card;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        private static bool IsItemOrderTouchHandle(DependencyObject source)
        {
            while (source != null && !(source is SettingsCard))
            {
                if (TouchDragScrollViewer.GetTouchDragExcluded(source)) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void ItemOrderCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            _dragItem = e.StylusDevice != null && !IsItemOrderTouchHandle(source)
                ? null
                : ItemOrderCardFrom(source)?.DataContext as MetricOrderItem;
            _dragStart = e.GetPosition(ItemOrderExpander);
        }

        private void ItemOrderCard_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragItem = null; return; }
            Vector delta = e.GetPosition(ItemOrderExpander) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var data = new DataObject(typeof(MetricOrderItem), _dragItem);
            try { DragDrop.DoDragDrop(ItemOrderExpander, data, DragDropEffects.Move); }
            finally { _dragItem = null; }
        }

        private void ItemOrderCard_DragEnter(object sender, DragEventArgs e)
            => ItemOrderCard_DragOver(sender, e);

        private void ItemOrderCard_DragOver(object sender, DragEventArgs e)
        {
            bool reorder = e.Data.GetDataPresent(typeof(MetricOrderItem));
            e.Effects = reorder ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void ItemOrderCard_Drop(object sender, DragEventArgs e)
        {
            var source = e.Data.GetData(typeof(MetricOrderItem)) as MetricOrderItem;
            var target = ItemOrderCardFrom(e.OriginalSource as DependencyObject)?.DataContext as MetricOrderItem;
            if (source == null || target == null || ReferenceEquals(source, target)) return;
            int oldIndex = _itemOrderItems.IndexOf(source);
            int newIndex = _itemOrderItems.IndexOf(target);
            if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;
            _itemOrderItems.RemoveAt(oldIndex);
            _itemOrderItems.Insert(newIndex, source);
            int[] order = CurrentItemOrder();
            PopulateItemOrder(order);
            _itemOrderChanged?.Invoke(order);
            e.Handled = true;
        }

        private void PopulateDisplayPicker(IReadOnlyList<TaskbarDisplayInfo> displays,
            IReadOnlyList<TaskbarDisplaySelection> selectedDisplays)
        {
            var cards = new List<object>();
            var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selectedDisplays != null)
            {
                foreach (var selected in selectedDisplays)
                {
                    if (selected != null && !string.IsNullOrWhiteSpace(selected.Id)) selectedIds.Add(selected.Id);
                }
            }

            if (displays != null)
            {
                foreach (var display in displays)
                {
                    if (display == null || string.IsNullOrWhiteSpace(display.Id)) continue;
                    bool selected = selectedDisplays == null ? display.IsPrimary : selectedIds.Contains(display.Id);
                    AddDisplayCard(cards, display.Id, display.Name, display.DeviceName, display.IsPrimary,
                        display.HasTaskbar, selected, false);
                    seen.Add(display.Id);
                }
            }

            if (selectedDisplays != null)
            {
                foreach (var selected in selectedDisplays)
                {
                    if (selected == null || string.IsNullOrWhiteSpace(selected.Id) || seen.Contains(selected.Id)) continue;
                    AddDisplayCard(cards, selected.Id, TaskbarDisplayCatalog.ResolveDisplayName(selected.Id, selected.Name),
                        null, false, false, true, true);
                    seen.Add(selected.Id);
                }
            }
            DisplayExpander.Items = cards;
            if (CountCheckedDisplays() == 0)
            {
                foreach (var item in DisplayExpander.Items)
                {
                    if (item is SettingsCard card && card.Content is CheckBox check && check.IsEnabled)
                    {
                        check.IsChecked = true;
                        break;
                    }
                }
            }
        }

        internal void RefreshDisplays(IReadOnlyList<TaskbarDisplayInfo> displays,
            IReadOnlyList<TaskbarDisplaySelection> selectedDisplays)
        {
            bool wasLoaded = _loaded;
            _loaded = false;
            PopulateDisplayPicker(displays, selectedDisplays);
            DisplayExpander.UpdateLayout();
            _loaded = wasLoaded;
            Logger.Debug($"设置显示器列表已重建：在线={displays?.Count ?? 0}，保存选择={selectedDisplays?.Count ?? 0}");
        }

        private void AddDisplayCard(List<object> cards, string id, string name, string deviceName, bool primary,
            bool hasTaskbar, bool selected, bool offline)
        {
            var selection = new TaskbarDisplaySelection
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? "显示器" : name
            };
            var check = new CheckBox
            {
                Content = selection.Name,
                Tag = selection,
                IsChecked = selected,
                IsEnabled = hasTaskbar || selected,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            check.Checked += DisplayCheckChanged;
            check.Unchecked += DisplayCheckChanged;
            string title = selection.Name;
            if (primary) title += "（主显示器）";
            if (offline) title += "（未连接）";
            string description = offline
                ? "保留此选择，重新连接后自动恢复。"
                : hasTaskbar ? deviceName : "当前没有任务栏；请在 Windows 中开启“在所有显示器上显示任务栏”。";
            cards.Add(new SettingsCard
            {
                Header = title,
                Description = description,
                Content = check
            });
        }

        private void DisplayCheckChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded || !(sender is CheckBox changed)) return;
            if (changed.IsChecked != true && CountCheckedDisplays() == 0)
            {
                _loaded = false;
                changed.IsChecked = true;
                _loaded = true;
                return;
            }
            var selected = new List<TaskbarDisplaySelection>();
            foreach (var item in DisplayExpander.Items)
            {
                if (item is SettingsCard card && card.Content is CheckBox check
                    && check.IsChecked == true && check.Tag is TaskbarDisplaySelection display)
                {
                    selected.Add(new TaskbarDisplaySelection { Id = display.Id, Name = display.Name });
                }
            }
            _displaySelectionChanged?.Invoke(selected);
        }

        private int CountCheckedDisplays()
        {
            int count = 0;
            foreach (var item in DisplayExpander.Items)
            {
                if (item is SettingsCard card && card.Content is CheckBox check && check.IsChecked == true) count++;
            }
            return count;
        }

        // The 特定磁盘 picker: one item per present physical disk ("磁盘 0 (C: D:) · model"),
        // Tag = the PhysicalDrive index. A stored pick whose disk is currently absent (or
        // a list that came back empty — disk sampling off) still gets a （未连接） item so
        // the user's choice stays visible instead of silently snapping to another disk.
        private void PopulateDiskPicker(IReadOnlyList<DiskInfo> disks, int selectedIndex)
        {
            int sel = -1;
            if (disks != null)
            {
                foreach (var d in disks)
                {
                    var item = new ComboBoxItem { Content = $"{d.TabTitle} · {d.Name}", Tag = d.Index };
                    DiskPickCombo.Items.Add(item);
                    if (d.Index == selectedIndex) sel = DiskPickCombo.Items.Count - 1;
                }
            }
            if (sel < 0)
            {
                DiskPickCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"磁盘 {selectedIndex}（未连接）",
                    Tag = selectedIndex,
                });
                sel = DiskPickCombo.Items.Count - 1;
            }
            DiskPickCombo.SelectedIndex = sel;
        }

        private int PickedDiskIndex()
            => (DiskPickCombo.SelectedItem as ComboBoxItem)?.Tag is int i ? i : 0;

        // The 特定 GPU picker — same shape as the disk one (a missing stored pick gets a
        // （未连接） placeholder), items are "GPU 0 · NVIDIA …", Tag = the "GPU N" number.
        private void PopulateGpuPicker(IReadOnlyList<GpuInfo> gpus, int selectedIndex)
        {
            int sel = -1;
            if (gpus != null)
            {
                foreach (var g in gpus)
                {
                    var item = new ComboBoxItem { Content = $"{g.TabTitle} · {g.Name}", Tag = g.Index };
                    GpuPickCombo.Items.Add(item);
                    if (g.Index == selectedIndex) sel = GpuPickCombo.Items.Count - 1;
                }
            }
            if (sel < 0)
            {
                GpuPickCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"GPU {selectedIndex}（未连接）",
                    Tag = selectedIndex,
                });
                sel = GpuPickCombo.Items.Count - 1;
            }
            GpuPickCombo.SelectedIndex = sel;
        }

        private int PickedGpuIndex()
            => (GpuPickCombo.SelectedItem as ComboBoxItem)?.Tag is int i ? i : 0;

        // The 温度来源 picker: 最高温度（默认） first (Tag = -1 — the sampler's "hottest
        // adapter" sentinel), then one item per present GPU (Tag = the "GPU N" number) —
        // the same items as the 特定 GPU picker. A stored pick that's currently absent (or
        // GPU sampling off) keeps a （未连接） placeholder, same contract as the other two.
        private void PopulateGpuTempPicker(IReadOnlyList<GpuInfo> gpus, int selectedIndex)
        {
            GpuTempPickCombo.Items.Add(new ComboBoxItem { Content = "最高温度（默认）", Tag = -1 });
            int sel = 0;
            if (gpus != null)
            {
                foreach (var g in gpus)
                {
                    var item = new ComboBoxItem { Content = $"{g.TabTitle} · {g.Name}", Tag = g.Index };
                    GpuTempPickCombo.Items.Add(item);
                    if (g.Index == selectedIndex) sel = GpuTempPickCombo.Items.Count - 1;
                }
            }
            if (sel == 0 && selectedIndex >= 0)
            {
                GpuTempPickCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"GPU {selectedIndex}（未连接）",
                    Tag = selectedIndex,
                });
                sel = GpuTempPickCombo.Items.Count - 1;
            }
            GpuTempPickCombo.SelectedIndex = sel;
        }

        private int PickedGpuTempIndex()
            => (GpuTempPickCombo.SelectedItem as ComboBoxItem)?.Tag is int i ? i : -1;

        // The 网络 适配器 combo: "自动（默认）" first (Tag = ""), then one item per
        // enumerated non-loopback adapter (Tag = its Id). A stored pick whose adapter is
        // absent keeps a （未连接） placeholder — same contract as the disk/GPU pickers.
        private void PopulateNetAdapterPicker(IReadOnlyList<(string Id, string Label)> adapters,
            string selectedId, string selectedName)
        {
            NetAdapterCombo.Items.Add(new ComboBoxItem { Content = "自动（默认）", Tag = "" });
            int sel = 0;
            if (adapters != null)
            {
                foreach (var (id, label) in adapters)
                {
                    NetAdapterCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                    if (!string.IsNullOrEmpty(selectedId) && id == selectedId)
                        sel = NetAdapterCombo.Items.Count - 1;
                }
            }
            if (sel == 0 && !string.IsNullOrEmpty(selectedId))
            {
                NetAdapterCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{(string.IsNullOrEmpty(selectedName) ? "未知适配器" : selectedName)}（未连接）",
                    Tag = selectedId,
                });
                sel = NetAdapterCombo.Items.Count - 1;
            }
            NetAdapterCombo.SelectedIndex = sel;
        }

        private static int IntervalToIndex(int ms) => ms <= 500 ? 0 : ms >= 2000 ? 2 : 1;
        private static int IndexToInterval(int index) => index == 0 ? 500 : index == 2 ? 2000 : 1000;

        /// <summary>App pushes the real task state back after a failed schtasks call,
        /// without re-firing the change callback.</summary>
        public void SyncAutoStart(bool on)
        {
            _loaded = false;
            AutoStartSwitch.IsOn = on;
            _loaded = true;
        }

        /// <summary>悬浮模式 was flipped from the taskbar right-click menu: push the state
        /// back (incl. the sub-card enablement the Toggled handler maintains) without
        /// re-firing the change callback.</summary>
        public void SyncFloatingMode(bool on)
        {
            _loaded = false;
            FloatingSwitch.IsOn = on;
            FloatingTopmostCard.IsEnabled = on;
            FloatingOutOfBoundsCard.IsEnabled = on;
            FloatingEdgeHideCard.IsEnabled = on;
            FloatingFullscreenHideCard.IsEnabled = on;
            FloatingOpacityCard.IsEnabled = on;
            _onLeftHost.IsEnabled = !on;
            DisplayExpander.IsEnabled = !on;
            _loaded = true;
        }

        /// <summary>置顶显示 was flipped from the taskbar right-click menu, without
        /// re-firing the change callback.</summary>
        public void SyncFloatingTopmost(bool on)
        {
            _loaded = false;
            FloatingTopmostSwitch.IsOn = on;
            _loaded = true;
        }

        /// <summary>贴边隐藏 was flipped from the taskbar right-click menu, without
        /// re-firing the change callback.</summary>
        public void SyncFloatingEdgeHide(bool on)
        {
            _loaded = false;
            FloatingEdgeHideSwitch.IsOn = on;
            _loaded = true;
        }

        /// <summary>全屏时隐藏 was flipped from the taskbar right-click menu, without
        /// re-firing the change callback.</summary>
        public void SyncFloatingFullscreenHide(bool on)
        {
            _loaded = false;
            FloatingFullscreenHideSwitch.IsOn = on;
            _loaded = true;
        }

        /// <summary>紧凑模式 was flipped from the taskbar right-click menu, without
        /// re-firing the change callback.</summary>
        public void SyncCompactLayout(bool on)
        {
            _loaded = false;
            CompactSwitch.IsOn = on;
            _loaded = true;
        }

        private void CompactSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _compactChanged?.Invoke(CompactSwitch.IsOn);
        }

        private void ShowSeparatorsSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _showSeparatorsChanged?.Invoke(ShowSeparatorsSwitch.IsOn);
        }

        private void ShowMonthUsageSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _showNetMonthUsageChanged?.Invoke(ShowMonthUsageSwitch.IsOn);
            RefreshNetUsageUnitAvailability();
        }

        private void ShowTodayUsageSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _showNetTodayUsageChanged?.Invoke(ShowTodayUsageSwitch.IsOn);
            RefreshNetUsageUnitAvailability();
        }

        private void FixedGbSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _netUsageFixedGbChanged?.Invoke(FixedGbSwitch.IsOn);
        }

        /// <summary>
        /// 多月历史保留月数（下拉 3 / 6 / 12 / 24）。只影响 net_history.json 里留几条，
        /// 与本月/今日的累计无关，所以不跟「网络流量统计」那两个开关一起置灰。
        /// 值取不到（Tag 不是数字）就退回默认 3 个月，绝不传一个 0 进去。
        /// </summary>
        private void NetHistoryShowSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _netHistoryShowChanged?.Invoke(NetHistoryShowSwitch.IsOn);
        }

        private void NetHistoryKeepCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _netHistoryKeepChanged?.Invoke(SelectedNetHistoryKeep());
        }

        private int SelectedNetHistoryKeep()
        {
            var item = NetHistoryKeepCombo.SelectedItem as ComboBoxItem;
            int v;
            if (item != null && item.Tag != null && int.TryParse(item.Tag.ToString(), out v))
                return NetHistory.NormalizeKeep(v);
            return NetHistory.DefaultKeepMonths;
        }

        private void SelectNetHistoryKeep(int months)
        {
            int want = NetHistory.NormalizeKeep(months);
            foreach (var obj in NetHistoryKeepCombo.Items)
            {
                var item = obj as ComboBoxItem;
                if (item == null || item.Tag == null) continue;
                int v;
                if (int.TryParse(item.Tag.ToString(), out v) && v == want)
                {
                    NetHistoryKeepCombo.SelectedItem = item;
                    return;
                }
            }
            // 配置里是个非预设值（比如手改成 5）：不硬塞选项，退到最接近的 3 个月，
            // 主界面照样按 5 去裁 —— 这里只负责让下拉有个合理显示。
            NetHistoryKeepCombo.SelectedIndex = 0;
        }

        /// <summary>
        /// 「固定按 GB 显示」是「网络流量统计」的子选项：本月、今日两个开关**都关**的时候，
        /// 弹窗里一行累计流量都不显示，单位怎么显示就没意义了 —— 这时把这张卡置灰、不接受点击。
        ///
        /// 必须挂在两个开关的 Toggled 里实时刷新，不能只在打开设置页时算一次：
        /// 用户先开两个、再把两个都关掉，页面不重建，只在初始化时算就永远停在「可用」。
        /// </summary>
        private void RefreshNetUsageUnitAvailability()
        {
            if (FixedGbCard == null) return;
            bool anyRowVisible = ShowMonthUsageSwitch.IsOn || ShowTodayUsageSwitch.IsOn;
            FixedGbCard.IsEnabled = anyRowVisible;
            FixedGbCard.Opacity = anyRowVisible ? 1.0 : 0.45;
        }

        private void OverlayUsageGraphSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _overlayUsageGraphChanged?.Invoke(OverlayUsageGraphSwitch.IsOn);
        }

        // 曲线颜色：6 个预设 + 跟随系统。它们是用精简模板的 Button（见 XAML 里的
        // GraphSwatchButtonStyle），**不是 Border**：Border 不捕获鼠标，外层页面的
        // TouchDragScrollViewer 会截走抬起事件，导致每个色板都点不动
        // （2026-11-09 报的「色板点不到」）。ButtonBase 自己捕获并触发 Click；
        // 模板把 iNKORE 的边框铬去掉，只留选中环。
        private static readonly Brush GraphSwatchRing = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A));

        private void PopulateGraphSwatches(string color)
        {
            _graphSwatches.Clear();
            _graphSwatches.Add(GraphColorSwatchBlue);
            _graphSwatches.Add(GraphColorSwatchTeal);
            _graphSwatches.Add(GraphColorSwatchGreen);
            _graphSwatches.Add(GraphColorSwatchOrange);
            _graphSwatches.Add(GraphColorSwatchPurple);
            _graphSwatches.Add(GraphColorSwatchRed);
            _graphSwatches.Add(GraphColorSwatchFollow);
            SetGraphSwatchSelection(color);
        }

        private void SetGraphSwatchSelection(string color)
        {
            string want = (color ?? string.Empty).Trim().ToUpperInvariant();
            foreach (var swatch in _graphSwatches)
            {
                string tag = (swatch.Tag as string ?? string.Empty).Trim().ToUpperInvariant();
                swatch.BorderBrush = tag == want ? GraphSwatchRing : Brushes.Transparent;
            }
        }

        private void GraphColorSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            string hex = ((sender as Button)?.Tag as string ?? string.Empty).Trim();
            SetGraphSwatchSelection(hex);
            _overlayGraphColorChanged?.Invoke(string.IsNullOrEmpty(hex) ? null : hex);
        }

        /// <summary>
        /// 任务栏「左对齐」时 CalcPosition 只走托盘分支：左端被开始按钮和图标占满，没有空位，
        /// 所以「任务栏最左端」「贴近开始按钮」以及「开关关闭（固定最左端）」都会停靠到托盘左侧。
        /// 横排时这等于「位置选择整体失效」，所以开关会被**锁死**：自动关闭、点了会弹回来并提示去改系统设置，
        /// 直到系统改回「居中」才解锁（2026-11-10 用户要求）。
        /// 竖排不走这套 —— 那边由 CalcPositionVertical 自动避开开始按钮，与对齐方式无关。
        /// </summary>
        /// <summary>
        /// 每隔 1 秒主动查一次对齐方式。只挂 <c>Window.Activated</c> 是不够的 —— 用户改完系统设置
        /// 如果没点回这个窗口，它永远不 Activation，锁定就永远不会发生（2026-11-10 实测「没有自动关闭」）。
        /// 状态没变就直接返回，避免每秒重设一遍 Description。
        /// </summary>
        private void RefreshAnchorAvailability()
        {
            bool vertical = TaskbarWindow.IsTaskbarVertical();
            bool centered = TaskbarWindow.IsTaskbarCenterAligned();
            if (_alignCache == ( vertical, centered )) return;
            _alignCache = (vertical, centered);
            ApplyAnchorAlignmentHint(vertical, centered);
        }

        private void ApplyAnchorAlignmentHint(bool vertical, bool centered)
        {
            _anchorLocked = !vertical && !centered;

            const string when = "系统任务栏在底部（默认）或顶部时生效：开启则按下面的「任务栏位置」摆放；"
                                + "关闭则固定在任务栏最左端。";
            const string verticalTail = "切到左侧或右侧时按下方「竖排任务栏」的规则。";
            const string lockedTail = "当前任务栏为「左对齐」，左端被开始按钮和图标占满、没有位置可放，"
                                      + "所以这个开关已锁定，任务栏窗口暂时停靠托盘左侧。"
                                      + "把「任务栏对齐方式」改回「居中」后会自动解锁。";

            string switchDesc, anchorDesc;
            if (vertical)
            {
                switchDesc = when + verticalTail;
                anchorDesc = "仅在上面的开关开启时生效。";
            }
            else if (_anchorLocked)
            {
                switchDesc = when + lockedTail + verticalTail;
                anchorDesc = "已锁定：左对齐任务栏没有可选的横向位置。";
            }
            else
            {
                switchDesc = when + verticalTail;
                anchorDesc = "仅在上面的开关开启时生效。";
            }

            OnLeftExpander.Description = switchDesc;
            OnLeftCard.Description = switchDesc;
            AnchorCard.Description = anchorDesc;

            if (_anchorLocked)
            {
                // 自动关闭 = 落到托盘左侧（左对齐下唯一可用的位置），并把下拉一起锁上。
                _suppressToggle = true;
                OnLeftSwitch.IsOn = false;
                _suppressToggle = false;
                AnchorCombo.IsEnabled = false;
            }
            else
            {
                HideAnchorLockedHint();   // 解锁后提示不该再挂着
                // 解锁：界面恢复成用户真正选过的值，并让覆盖层按 anchor 重摆一次。
                // 配置值在锁定期间**没被改过**，所以这里只是把界面和实际行为重新对齐。
                if (OnLeftSwitch.IsOn != _configuredOnLeft)
                {
                    _suppressToggle = true;
                    OnLeftSwitch.IsOn = _configuredOnLeft;
                    _suppressToggle = false;
                    _placementChanged?.Invoke(_configuredOnLeft, Math.Max(0, AnchorCombo.SelectedIndex));
                }
                AnchorCombo.IsEnabled = OnLeftSwitch.IsOn;
            }
        }

        /// <summary>
        /// 锁死时用户仍然可以点开关 —— 点了立刻弹回并说明原因。真的把 IsEnabled 设成 false
        /// 就点不动了，用户只会觉得「控件坏了」，得不到任何解释。
        /// </summary>
        // 左对齐锁定提示：页内条，8 秒自动收起（可点 × 提前关）。
        // 原来是 System.Windows.MessageBox —— 模态、打断操作、与设置页的 Fluent 风格不搭，2026-11-14 换掉。
        private System.Windows.Threading.DispatcherTimer _anchorHintTimer;

        // 动画时长：150ms —— 观感上「滑出来」而不是「蹦出来」。
        private static readonly TimeSpan HintAnimDuration = TimeSpan.FromMilliseconds(150);

        private void ShowAnchorLockedHint()
        {
            if (AnchorLockedHint == null) return;

            if (AnchorLockedHint.Visibility != Visibility.Visible)
            {
                // 先量出目标高度：可见后按 Auto 排一次版拿 ActualHeight，再把高度压回 0 开始动画。
                AnchorLockedHint.BeginAnimation(FrameworkElement.HeightProperty, null);
                AnchorLockedHint.Visibility = Visibility.Visible;
                AnchorLockedHint.Height = double.NaN;
                AnchorLockedHint.Opacity = 0;
                AnchorLockedHint.UpdateLayout();
                double target = AnchorLockedHint.ActualHeight;
                if (target <= 0) target = 96;          // 兜底：还没排过版时给个可信值

                var ease = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                };
                var grow = new System.Windows.Media.Animation.DoubleAnimation(0, target, HintAnimDuration)
                {
                    EasingFunction = ease
                };
                grow.Completed += (s2, e2) =>
                {
                    // 结束后交回 Auto：文字换行变化时高度仍能自适应
                    AnchorLockedHint.BeginAnimation(FrameworkElement.HeightProperty, null);
                    AnchorLockedHint.Height = double.NaN;
                };
                AnchorLockedHint.BeginAnimation(FrameworkElement.HeightProperty, grow);
                AnchorLockedHint.BeginAnimation(UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 1, HintAnimDuration));
            }

            if (_anchorHintTimer == null)
            {
                _anchorHintTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(8)
                };
                _anchorHintTimer.Tick += (s, e) => { _anchorHintTimer.Stop(); HideAnchorLockedHint(); };
            }
            _anchorHintTimer.Stop();
            _anchorHintTimer.Start();
        }

        private void HideAnchorLockedHint()
        {
            _anchorHintTimer?.Stop();
            if (AnchorLockedHint == null || AnchorLockedHint.Visibility != Visibility.Visible) return;

            double current = AnchorLockedHint.ActualHeight;
            if (current <= 0) { AnchorLockedHint.Visibility = Visibility.Collapsed; return; }

            var ease = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
            };
            var shrink = new System.Windows.Media.Animation.DoubleAnimation(current, 0, HintAnimDuration)
            {
                EasingFunction = ease
            };
            shrink.Completed += (s2, e2) =>
            {
                AnchorLockedHint.BeginAnimation(FrameworkElement.HeightProperty, null);
                AnchorLockedHint.BeginAnimation(UIElement.OpacityProperty, null);
                AnchorLockedHint.Height = double.NaN;
                AnchorLockedHint.Opacity = 1;
                AnchorLockedHint.Visibility = Visibility.Collapsed;
            };
            AnchorLockedHint.BeginAnimation(FrameworkElement.HeightProperty, shrink);
            AnchorLockedHint.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(1, 0, HintAnimDuration));
        }

        private void AnchorLockedHintClose_Click(object sender, RoutedEventArgs e) => HideAnchorLockedHint();

        private void OnLeftSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded || _suppressToggle) return;

            if (_anchorLocked && OnLeftSwitch.IsOn)
            {
                // 拒绝打开：弹回原位 + 说清楚为什么（2026-11-10 用户要求）
                _suppressToggle = true;
                OnLeftSwitch.IsOn = false;
                _suppressToggle = false;
                ShowAnchorLockedHint();
                return;
            }

            AnchorCombo.IsEnabled = OnLeftSwitch.IsOn;
            _configuredOnLeft = OnLeftSwitch.IsOn;   // 解锁状态下用户的选择才算「真正的配置」
            _placementChanged?.Invoke(OnLeftSwitch.IsOn, Math.Max(0, AnchorCombo.SelectedIndex));
        }

        private void AnchorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _placementChanged?.Invoke(OnLeftSwitch.IsOn, Math.Max(0, AnchorCombo.SelectedIndex));
        }

        private void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _autoStartChanged?.Invoke(AutoStartSwitch.IsOn);
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _themeChanged?.Invoke(ThemeCombo.SelectedIndex);
        }

        private void FloatingSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            FloatingTopmostCard.IsEnabled = FloatingSwitch.IsOn;
            FloatingOutOfBoundsCard.IsEnabled = FloatingSwitch.IsOn;
            FloatingEdgeHideCard.IsEnabled = FloatingSwitch.IsOn;
            FloatingFullscreenHideCard.IsEnabled = FloatingSwitch.IsOn;
            FloatingOpacityCard.IsEnabled = FloatingSwitch.IsOn;
            _onLeftHost.IsEnabled = !FloatingSwitch.IsOn;   // anchors don't apply to a dragged widget
            DisplayExpander.IsEnabled = !FloatingSwitch.IsOn;
            _floatingChanged?.Invoke(FloatingSwitch.IsOn);
        }

        private void FloatingTopmostSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _floatingTopmostChanged?.Invoke(FloatingTopmostSwitch.IsOn);
        }

        private void FloatingOutOfBoundsSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _floatingOutOfBoundsChanged?.Invoke(FloatingOutOfBoundsSwitch.IsOn);
        }

        private void FloatingEdgeHideSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _floatingEdgeHideChanged?.Invoke(FloatingEdgeHideSwitch.IsOn);
        }

        private void FloatingFullscreenHideSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _floatingFullscreenHideChanged?.Invoke(FloatingFullscreenHideSwitch.IsOn);
        }

        // 悬浮模式 → 透明度 slider: fires per step while dragging, and each step is reported
        // at once — the widget must re-tint live under the thumb, so no debounce here. App
        // applies immediately and defers only the settings.yaml write.
        private void FloatingOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_loaded) return;
            FloatingOpacityText.Text = $"{FloatingOpacitySlider.Value:0}%";
            _floatingOpacityChanged?.Invoke(FloatingOpacitySlider.Value / 100.0);
        }

        private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _intervalChanged?.Invoke(IndexToInterval(IntervalCombo.SelectedIndex));
        }

        // 检查更新 toggle: reports immediately (persisted for the NEXT startup's check)
        // and gates the 更新源 picker (same pattern as the Clash switch).
        private void UpdateCheckSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            UpdateSourceCombo.IsEnabled = UpdateCheckSwitch.IsOn;
            _updateCheckChanged?.Invoke(UpdateCheckSwitch.IsOn);
        }

        private void UpdateSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _updateSourceChanged?.Invoke(UpdateSourceCombo.SelectedIndex);
        }

        // One handler for all five 采样 toggles — the switch's Tag carries the overlay
        // hit slot (0=CPU 1=内存 2=磁盘 3=GPU 4=网络). The last enabled metric cannot
        // be turned off; restore it without re-firing this handler.
        private void MetricSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            if (sender is ToggleSwitch sw && int.TryParse(sw.Tag as string, out int slot))
            {
                if (!sw.IsOn && !CpuSwitch.IsOn && !RamSwitch.IsOn && !DiskSwitch.IsOn
                    && !GpuSwitch.IsOn && !NetSwitch.IsOn)
                {
                    _loaded = false;
                    sw.IsOn = true;
                    _loaded = true;
                    return;
                }
                _samplingChanged?.Invoke(slot, sw.IsOn);
            }
        }

        private void MergeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _mergeSamePathChanged?.Invoke(MergeSwitch.IsOn);
        }

        // 磁盘 → 显示方式 combo: 0=所有磁盘平均 1=最高利用率 2=特定磁盘 (the picker below
        // is enabled only in 特定磁盘 mode — the AnchorCombo pattern).
        private void DiskDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            DiskPickCombo.IsEnabled = DiskDisplayCombo.SelectedIndex == 2;
            _diskDisplayChanged?.Invoke(DiskDisplayCombo.SelectedIndex, PickedDiskIndex());
        }

        private void DiskPickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            // Re-picking only matters while 特定磁盘 is the active mode.
            if (DiskDisplayCombo.SelectedIndex != 2) return;
            _diskDisplayChanged?.Invoke(2, PickedDiskIndex());
        }

        // GPU → 显示方式 combo: 0=所有 GPU 平均 1=最高利用率 2=特定 GPU (the GPU default is
        // 最高, unlike the disk's 平均; the picker below is enabled only in 特定 GPU mode).
        private void GpuDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            GpuPickCombo.IsEnabled = GpuDisplayCombo.SelectedIndex == 2;
            _gpuDisplayChanged?.Invoke(GpuDisplayCombo.SelectedIndex, PickedGpuIndex());
        }

        private void GpuPickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            // Re-picking only matters while 特定 GPU is the active mode.
            if (GpuDisplayCombo.SelectedIndex != 2) return;
            _gpuDisplayChanged?.Invoke(2, PickedGpuIndex());
        }

        // GPU → 任务栏显示 combo: 0=使用率（默认） 1=温度 2=使用率 + 温度 — 温度 replaces
        // the utilization row, 两者 packs BOTH rows. The two temperature sub-cards below are
        // enabled only while a temperature shows (index != 0).
        private void GpuContentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            bool tempShows = GpuContentCombo.SelectedIndex != 0;
            GpuTempPickCombo.IsEnabled = tempShows;
            GpuTempUnitCombo.IsEnabled = tempShows;
            _gpuTaskbarContentChanged?.Invoke(GpuContentCombo.SelectedIndex, PickedGpuTempIndex(),
                GpuTempUnitCombo.SelectedIndex == 1);
        }

        // GPU → 温度来源 combo: the "-1"-tagged first item is 最高温度; every other item's
        // Tag is the "GPU N" number. Re-picking only matters while a temperature shows.
        private void GpuTempPickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            if (GpuContentCombo.SelectedIndex == 0) return;
            _gpuTaskbarContentChanged?.Invoke(GpuContentCombo.SelectedIndex, PickedGpuTempIndex(),
                GpuTempUnitCombo.SelectedIndex == 1);
        }

        // GPU → 温度单位 combo: 0=摄氏度（默认） 1=华氏度 — applies to the taskbar row AND
        // the detail popup's temperatures. Only matters while a temperature shows.
        private void GpuTempUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            if (GpuContentCombo.SelectedIndex == 0) return;
            _gpuTaskbarContentChanged?.Invoke(GpuContentCombo.SelectedIndex, PickedGpuTempIndex(),
                GpuTempUnitCombo.SelectedIndex == 1);
        }

        // 网络 → 适配器 combo: the ""-tagged first item is 自动; every other item's Tag is
        // the adapter's NetworkInterface.Id. The display name rides along so the picker can
        // show a （未连接） placeholder for a missing pick later.
        private void NetAdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            var item = NetAdapterCombo.SelectedItem as ComboBoxItem;
            string id = item?.Tag as string ?? "";
            _netAdapterChanged?.Invoke(id, id.Length == 0 ? null : item.Content as string);
        }

        // The 公网 IP switch: a discrete action, so it reports immediately (same as the
        // Clash switch) — NetInfoSampler's poll thread stops BOTH the HTTP lookups and
        // the 公网延迟 ICMP probe, and drops the cached address on off.
        private void PublicIpSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _publicIpLookupChanged?.Invoke(PublicIpSwitch.IsOn);
        }

        // The Clash/Mihomo switch: a discrete action, so it reports immediately (the text
        // boxes debounce instead), and the endpoint inputs follow its state.
        private void ClashSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            SetClashInputsEnabled(ClashSwitch.IsOn);
            _clashApiChanged?.Invoke(ClashSwitch.IsOn, ClashAddressBox.Text.Trim(), ClashSecretBox.Text.Trim());
        }

        private void SetClashInputsEnabled(bool on)
        {
            ClashAddressBox.IsEnabled = on;
            ClashSecretBox.IsEnabled = on;
            ClashTestButton.IsEnabled = on;
            if (!on) ClashTestResult.Text = "";
        }

        // Both Clash/Mihomo boxes funnel here per keystroke; the actual report is
        // debounced 500ms so a yaml write + sampler retarget doesn't fire per character.
        private void ClashApiBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded) return;
            if (_clashDebounce == null)
            {
                _clashDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _clashDebounce.Tick += (s2, e2) =>
                {
                    _clashDebounce.Stop();
                    _clashApiChanged?.Invoke(ClashSwitch.IsOn, ClashAddressBox.Text.Trim(), ClashSecretBox.Text.Trim());
                };
            }
            _clashDebounce.Stop();
            _clashDebounce.Start();
        }

        // 测试连接: probes the CURRENT box contents (empty address = the 127.0.0.1:9090
        // default — the same value the poller would use) via GET /version, off the UI
        // thread. The result line is colored per outcome (iNKORE scheme brushes).
        private async void ClashTestButton_Click(object sender, RoutedEventArgs e)
        {
            string address = ClashAddressBox.Text.Trim();
            string secret = ClashSecretBox.Text.Trim();
            ClashTestButton.IsEnabled = false;
            ClashTestResult.Text = "测试中…";
            ClashTestResult.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
            var (ok, detail) = await Task.Run(() => ClashSampler.TestConnection(address, secret));
            ClashTestResult.Text = detail;
            ClashTestResult.Foreground = (Brush)FindResource(ok
                ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
            ClashTestButton.IsEnabled = true;
        }

        private const string GitHubUrl = "https://github.com/linesoft2/TaskMonitor";
        private const string AcknowledgementsUrl = "https://github.com/linesoft2/TaskMonitor#参考--致谢--开源许可";

        // The nav pane's 类别 switch. Only ever flips the four pages' Visibility —
        // deliberately without the _loaded guard: it carries no change callback to
        // App, and it must also run for the selection IsSelected="True" establishes
        // in XAML (the pages' XAML defaults already match, so the early fire during
        // InitializeComponent is a no-op there — the null check only covers it firing
        // before the pages themselves are parsed).
        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (GeneralPage == null) return;
            object item = args.SelectedItem;
            GeneralPage.Visibility = item == NavGeneral ? Visibility.Visible : Visibility.Collapsed;
            AppearancePage.Visibility = item == NavAppearance ? Visibility.Visible : Visibility.Collapsed;
            SamplingPage.Visibility = item == NavSampling ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = item == NavAbout ? Visibility.Visible : Visibility.Collapsed;
        }

        // The About page's live CPU waveform. Driven by App's SnapshotChanged pump (the
        // same push model as the detail windows — this window holds no timer): a no-op
        // unless the About page is actually showing, and the whole band collapses while
        // CPU sampling is off (the snapshot's history then stays empty — a flat line at
        // zero would be a lie). Points map oldest→newest left→right, 2px inset so the
        // 100% peak never clips.
        internal void RefreshAbout()
        {
            if (AboutPage.Visibility != Visibility.Visible) return;
            var s = _snapshotProvider?.Invoke();
            var history = s?.CpuHistory;
            double w = LiveWave.ActualWidth, h = LiveWave.ActualHeight;
            if (history == null || history.Length < 2 || w < 10 || h < 10)
            {
                LiveBand.Visibility = Visibility.Collapsed;
                return;
            }
            LiveBand.Visibility = Visibility.Visible;
            LiveCpuText.Text = $"{s.CpuPercent:0}%";

            int n = history.Length;
            var pts = new Point[n];
            double step = w / (n - 1);
            for (int i = 0; i < n; i++)
            {
                double v = history[i];
                if (v < 0) v = 0; else if (v > 100) v = 100;
                pts[i] = new Point(i * step, h - 2 - v / 100.0 * (h - 4));
            }
            LiveLine.Points = new PointCollection(pts);

            // The soft area under the curve (the logo's chart motif) — one StreamGeometry,
            // frozen so the render thread can share it.
            var fill = new StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                ctx.PolyLineTo(pts, true, false);
                ctx.LineTo(new Point(w, h), true, false);
            }
            fill.Freeze();
            LiveFill.Data = fill;

            Canvas.SetLeft(LiveDot, pts[n - 1].X - LiveDot.Width / 2);
            Canvas.SetTop(LiveDot, pts[n - 1].Y - LiveDot.Height / 2);
        }

        private void GitHubCard_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

        private void AcknowledgementsCard_Click(object sender, RoutedEventArgs e) => OpenUrl(AcknowledgementsUrl);

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { System.Diagnostics.Process.Start(url); }
            catch { /* no browser handler / malformed url — ignore */ }
        }
    }
}
