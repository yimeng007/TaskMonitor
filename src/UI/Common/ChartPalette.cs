using System.Windows;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// The theme-dependent colors the five hand-drawn charts share. Every value is read from
    /// the iNKORE theme resources at use time, so a live theme switch only has to re-run the
    /// view's <c>ApplyTheme</c>.
    /// </summary>
    internal static class ChartPalette
    {
        /// <summary>
        /// 曲线颜色：由 App 解析后下发的颜色；跟随系统时取系统强调色。
        /// 一旦设置，所有手绘图表都用它代替 iNKORE 的强调色，这样弹窗里的图表和任务栏上的
        /// 走势曲线颜色完全一致。null = 从没下发过（设计器 / 没有资源的启动路径）→ 退回 iNKORE。
        /// </summary>
        public static Color? Override { get; set; }

        /// <summary>The iNKORE accent — the "primary" hue of the CPU/RAM history lines and of
        /// the network download line, with a fallback for a resource-less startup.</summary>
        public static Color Accent =>
            Override
            ?? (Color?)Application.Current.TryFindResource("SystemAccentColor")
            ?? Color.FromRgb(0x00, 0x78, 0xD4);

        /// <summary>SystemBaseLowColor re-tinted to <paramref name="alpha"/>. It adapts to the
        /// theme, but its native ~20% alpha reads too heavy as a guide line, so the chart
        /// chrome (grid / axis / frame / track) picks its own faint alpha.</summary>
        public static Color FaintBase(byte alpha)
        {
            var color = (Color?)Application.Current.TryFindResource("SystemBaseLowColor")
                        ?? Color.FromArgb(0x33, 0x00, 0x00, 0x00);
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        /// <summary>Faint horizontal guide under the history chart's area fill.</summary>
        public static Color Grid => FaintBase(0x1A);

        /// <summary>A faint full-height slot behind each core bar, so individual cores stay
        /// distinguishable.</summary>
        public static Color Track => FaintBase(0x21);

        /// <summary>Theme-adaptive gray for the network chart's edge annotations (each half's
        /// scale ceiling) — the same secondary color the rest of the panel uses for muted
        /// labels.</summary>
        public static Color LabelGray
        {
            get
            {
                var brush = Application.Current.TryFindResource("TextFillColorSecondaryBrush") as SolidColorBrush;
                return brush?.Color ?? Color.FromRgb(0x88, 0x88, 0x88);
            }
        }
    }
}
