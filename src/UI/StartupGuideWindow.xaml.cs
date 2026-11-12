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
            if (!_taskbarCentered && !_taskbarVertical)
            {
                // Nothing is chosen yet: make the user acknowledge the single option first,
                // otherwise the hint below it never gets read. A side bar has nothing to pick,
                // so 完成 stays live there.
                TrayOnlyOption.IsChecked = false;
                SwitchTaskbarHint.IsOpen = false;
                PrimaryButton.IsEnabled = false;
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
