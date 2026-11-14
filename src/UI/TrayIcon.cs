using System;
using System.Drawing;
using System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;

namespace task_monitor
{
    /// <summary>
    /// 系统托盘图标。存在的唯一理由：**取消置顶后点「显示桌面」，悬浮窗会被桌面盖住**，
    /// 或者任务栏那边忽然没了（explorer 重启把覆盖层连带销毁）——这些时候需要一个
    /// 随手可点、能把界面叫回来的入口（用户 2026-11-11 实报：只能「随便点一个网页」）。
    ///
    /// <para>左键 = 召回 / 复原（App 侧决定具体做什么）；右键 = 复用悬浮窗自己的那份菜单
    /// （悬浮模式 / 置顶显示 / 贴边隐藏 / 全屏时隐藏 / 紧凑模式 / 设置 / 退出），不另做一套，
    /// 避免两处状态漂移。</para>
    ///
    /// <para>用 WinForms 的 <see cref="NotifyIcon"/> 而不是手写 Shell_NotifyIcon：后者要自己
    /// 维护一个隐藏窗口收回调、还要处理 NOTIFYICONDATA 各版本的结构体大小，而 NotifyIcon
    /// 连「资源管理器重启后重新添加图标」都处理好了。</para>
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private NotifyIcon _icon;

        /// <summary>左键单击（托盘图标的惯例动作）。</summary>
        public event Action LeftClick;

        /// <summary>右键单击：调用方在光标处弹出菜单。</summary>
        public event Action RightClick;

        /// <summary>
        /// 创建并显示托盘图标。图标直接取可执行文件带的那一个（csproj 的 ApplicationIcon），
        /// 不再单独嵌一份，图标改了也不会两边不一致。
        /// </summary>
        public void Show(string tooltip)
        {
            if (_icon != null) return;

            DrawingIcon icon = null;
            try { icon = DrawingIcon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); }
            catch { }

            _icon = new NotifyIcon
            {
                Icon = icon ?? SystemIcons.Application,
                // 悬停提示只写程序名：一行长说明在托盘气泡里既读不全也挡视线（用户 2026-11-11
                // 明确要去掉后面那串「左键召回…右键菜单」）。用法在右键菜单里自明。
                Text = string.IsNullOrWhiteSpace(tooltip) ? "TaskMonitor" : Trim(tooltip),
                Visible = true,
            };
            _icon.MouseClick += OnMouseClick;
        }

        /// <summary>NotifyIcon.Text 上限 63 个字符，超了会抛异常。</summary>
        private static string Trim(string text) => text.Length <= 63 ? text : text.Substring(0, 63);

        private void OnMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) LeftClick?.Invoke();
            else if (e.Button == MouseButtons.Right) RightClick?.Invoke();
        }

        public void Dispose()
        {
            if (_icon == null) return;
            _icon.Visible = false;   // 先摘图标，再 Dispose（否则托盘里会留一个死图标）
            _icon.MouseClick -= OnMouseClick;
            _icon.Dispose();
            _icon = null;
        }
    }
}
