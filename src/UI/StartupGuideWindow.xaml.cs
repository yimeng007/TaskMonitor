using System.Windows;

namespace task_monitor
{
    public partial class StartupGuideWindow : Window
    {
        // A left-aligned taskbar leaves no empty space at the left end — Start and the task
        // icons fill it — so 「任务栏最左端」 and 「贴近开始按钮」 both fall back to the tray side
        // inside CalcPosition. The guide therefore shows ONE option in that case instead of
        // three cards that all land on the same pixel (2026-11-10).
        private readonly bool _taskbarCentered;

        // A SIDE taskbar goes further: none of the three horizontal anchors apply at all. The
        // overlay keeps clear of 开始按钮 by itself — top when Start sits mid-bar, tray end when
        // Start is docked at the top — so there is literally nothing for the user to choose.
        private readonly bool _taskbarVertical;

        public bool UseFloatingMode => FloatingOption.IsChecked == true;

        /// <summary>首次运行向导里「开机自启动」的勾选结果（由 App 在完成后应用）。</summary>
        public bool AutoStartChosen => AutoStartCheck.IsChecked == true;

        // The three placement cards, as anchors: 0 = far left, 1 = next to Start, 2 = tray side.
        // This used to be expressed as "not on the left" (OverlayOnLeft = TrayOption unchecked),
        // which was correct while onLeft:false meant the tray. The placement rework turned
        // onLeft:false into "pinned to the far left", so the tray card silently started landing at
        // the far left (2026-11-10 report). Anchors keep each card meaning what it draws.
        // On a left-aligned bar the answer is always 2, whatever the card says — writing 2 keeps
        // the saved config honest if the user later switches the taskbar back to centred.
        // On a side bar the anchor is never read (CalcPositionVertical ignores it); 2 is stored so
        // the yaml still carries a sane value if the bar later moves back to an edge.
        public int OverlayAnchor =>
            _taskbarVertical ? 2
            : !_taskbarCentered ? 2
            : TrayOption.IsChecked == true ? 2
            : StartOption.IsChecked == true ? 1 : 0;

        public StartupGuideWindow(bool snapToStartSupported, bool taskbarCentered, bool taskbarVertical)
        {
            InitializeComponent();
            _taskbarCentered = taskbarCentered;
            _taskbarVertical = taskbarVertical;
            StartOption.IsEnabled = snapToStartSupported;

            // **恒定默认勾上**（2026-11-16 用户定）。
            // 以前初值取「任务是否指向本程序」—— 同一个安装包在不同文件夹里跑，任务指向不同，
            // 勾选状态就会变，看起来像「有时开、有时关」。首次运行向导只在「没配置/删过配置」时出现，
            // 这种场景统一默认开：用户要关就自己取消，勾选状态从此刻起以他自己的操作为准。
            AutoStartCheck.IsChecked = true;

            // 压缩包临时目录里跑（程序落在 %TEMP%ðzip$Temp\…）：注册会被拒，
            // 与其让用户勾了没效果，不如直接置灰并说明。
            if (StartupTask.IsRunningFromTransientLocation())
            {
                AutoStartCheck.IsChecked = false;
                AutoStartCheck.IsEnabled = false;
                AutoStartCheck.Content = "开机自启动（当前在压缩包临时目录中运行，解压到文件夹后可开启）";
            }
            FloatingOption.Checked += ModeOption_Checked;
            TaskbarOption.Checked += ModeOption_Checked;
            // Three mutually exclusive placement pages: side bar (nothing to pick),
            // left-aligned bar (tray only), centred bar (all three cards).
            CenteredPlacement.Visibility = !taskbarVertical && taskbarCentered
                ? Visibility.Visible : Visibility.Collapsed;
            LeftAlignedPlacement.Visibility = !taskbarVertical && !taskbarCentered
                ? Visibility.Visible : Visibility.Collapsed;
            VerticalPlacement.Visibility = taskbarVertical ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ModeOption_Checked(object sender, RoutedEventArgs e)
        {
            PrimaryButton.Content = UseFloatingMode ? "完成" : "下一步";
            PrimaryButton.IsEnabled = true;   // a disabled state can survive stepping back
            TaskbarWarning.Visibility = UseFloatingMode ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            if (UseFloatingMode || PlacementPage.Visibility == Visibility.Visible)
            {
                DialogResult = true;
                return;
            }

            ModePage.Visibility = Visibility.Collapsed;
            PlacementPage.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
            PrimaryButton.Content = "完成";
            // 靠左对齐的水平任务栏只有一张卡：要求用户点一下（否则下面那条提示永远不会被读到）。
            // 但**只在「还没点过」时**这样处理 —— 以前每次进这一页都无条件清空，
            // 用户点上一步再回来就得重新点一遍（2026-11-16 反馈的「前后逻辑」问题）。
            if (!_taskbarCentered && !_taskbarVertical && TrayOnlyOption.IsChecked != true)
            {
                SwitchTaskbarHint.IsOpen = false;
                PrimaryButton.IsEnabled = false;
            }
            else
            {
                PrimaryButton.IsEnabled = true;
            }
        }

        private void TrayOnlyOption_Click(object sender, RoutedEventArgs e)
        {
            TrayOnlyOption.IsChecked = true;
            SwitchTaskbarHint.IsOpen = true;      // 「想放到别的位置？」 — shown after the click
            PrimaryButton.IsEnabled = true;
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            PlacementPage.Visibility = Visibility.Collapsed;
            ModePage.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = UseFloatingMode ? "完成" : "下一步";
            PrimaryButton.IsEnabled = true;
        }
    }
}
