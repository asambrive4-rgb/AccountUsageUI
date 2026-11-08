using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace AccountUsageUI.App;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private bool _disposed;

    public TrayIconService(Action onOpenRequested, Action onExitRequested)
    {
        ArgumentNullException.ThrowIfNull(onOpenRequested);
        ArgumentNullException.ThrowIfNull(onExitRequested);

        _menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("열기");
        openItem.Click += (_, _) => onOpenRequested();
        var exitItem = new Forms.ToolStripMenuItem("앱 종료");
        exitItem.Click += (_, _) => onExitRequested();

        _menu.Items.Add(openItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Antigravity Account Switcher",
            Icon = LoadAppIcon(),
            ContextMenuStrip = _menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => onOpenRequested();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AccountUsageUI.ico"));
            if (streamInfo?.Stream is not null)
            {
                using var stream = streamInfo.Stream;
                return new Icon(stream);
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }
}
