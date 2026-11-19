using System.Windows;
using Forms = System.Windows.Forms;

namespace Satchel.Infrastructure;

/// <summary>Notification-area icon (WinForms NotifyIcon; WPF has none built in).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action show, Action checkNow, Action updateAll, Action exit)
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/satchel.ico"))?.Stream;
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open Satchel", null, (_, _) => show());
        menu.Items.Add("Check for updates", null, (_, _) => checkNow());
        menu.Items.Add("Update all", null, (_, _) => updateAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = stream != null ? new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application,
            Text = "Satchel",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) show();
        };
        _icon.BalloonTipClicked += (_, _) => show();
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
