using System.Windows;

namespace task_monitor
{
    /// <summary>The 发现新版本 prompt — see the XAML header. Pure view: the outcome is
    /// read off <see cref="Result"/> by <see cref="UpdateChecker"/>, which owns the
    /// follow-up (open the releases page / persist 不再提醒).</summary>
    public partial class UpdateAvailableDialog : Window
    {
        public enum Choice { Later, Update, Ignore }

        public Choice Result { get; private set; } = Choice.Later;

        public UpdateAvailableDialog(string current, string latestTag)
        {
            InitializeComponent();
            MessageText.Text = $"上游有新版 {latestTag}（当前版本 v{current}）。\n"
                + "⚠ 那是官方原版，装上去会覆盖本修改版的所有改动。";
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            Result = Choice.Update;
            Close();
        }

        private void Ignore_Click(object sender, RoutedEventArgs e)
        {
            Result = Choice.Ignore;
            Close();
        }
    }
}
