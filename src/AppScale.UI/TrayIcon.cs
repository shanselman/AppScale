using System.Drawing;
using System.Windows.Forms;

namespace AppScale.UI;

/// <summary>Notification-area icon. Disposed on exit so no ghost icon is left behind.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;

    public TrayIcon(Action open, Action resetAll, Action emergency, Action exit)
    {
        var menu = new ContextMenuStrip();
        _status = new ToolStripMenuItem("Not watching") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open AppScale", null, (_, _) => open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reset all", null, (_, _) => resetAll());
        menu.Items.Add("Emergency disable", null, (_, _) => emergency());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (resets all)", null, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AppScale (experimental)",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void ShowBalloon(string title, string text, bool isError) =>
        _icon.ShowBalloonTip(4000, title, text, isError ? ToolTipIcon.Warning : ToolTipIcon.Info);

    /// <summary>Updates the disabled status menu item and the tooltip (NotifyIcon.Text is limited to 127 chars).</summary>
    public void SetStatus(string status)
    {
        _status.Text = status;
        string tip = $"AppScale — {status}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
