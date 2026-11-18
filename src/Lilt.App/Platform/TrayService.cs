using System.Drawing;
using System.Drawing.Drawing2D;
using Lilt.Core;
using Forms = System.Windows.Forms;

namespace Lilt.App.Platform;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _ownedIcon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.ToolStripMenuItem _show = new("Hide");
    private readonly Forms.ToolStripMenuItem _ignore = new("Ignore clicks") { CheckOnClick = true, ShortcutKeyDisplayString = "Ctrl+Alt+Shift+L" };
    private readonly Forms.ToolStripMenuItem _topmost = new("Always on top") { CheckOnClick = true };
    private readonly Forms.ToolStripMenuItem _glass = new("Glass") { CheckOnClick = true };
    private readonly Forms.ToolStripMenuItem _startup = new("Start with Windows") { CheckOnClick = true };
    private readonly Forms.ToolStripMenuItem _connect = new("Connect Spotify…");
    private readonly Forms.ToolStripMenuItem _disconnect = new("Disconnect Spotify");
    private readonly Forms.ToolStripMenuItem _source = new("This PC") { Enabled = false };
    private readonly Dictionary<double, Forms.ToolStripMenuItem> _opacityItems = new();
    private bool _updating;
    private bool _disposed;

    public event Action? ToggleVisibilityRequested;
    public event Action<bool>? IgnoreClicksChanged;
    public event Action<bool>? TopmostChanged;
    public event Action<bool>? GlassChanged;
    public event Action<bool>? StartupChanged;
    public event Action<double>? OpacityChanged;
    public event Action? SettingsRequested;
    public event Action? ConnectRequested;
    public event Action? DisconnectRequested;
    public event Action? OpenSpotifyRequested;
    public event Action? ResetPositionRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _ownedIcon = CreateIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon,
            Text = "Lilt · This PC",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ToggleVisibilityRequested?.Invoke();
        _show.ShortcutKeyDisplayString = "Ctrl+Alt+L";
        _show.Click += (_, _) => ToggleVisibilityRequested?.Invoke();
        _ignore.CheckedChanged += (_, _) => { if (!_updating) IgnoreClicksChanged?.Invoke(_ignore.Checked); };
        _topmost.CheckedChanged += (_, _) => { if (!_updating) TopmostChanged?.Invoke(_topmost.Checked); };
        _glass.CheckedChanged += (_, _) => { if (!_updating) GlassChanged?.Invoke(_glass.Checked); };
        _startup.CheckedChanged += (_, _) => { if (!_updating) StartupChanged?.Invoke(_startup.Checked); };
        _connect.Click += (_, _) => ConnectRequested?.Invoke();
        _disconnect.Click += (_, _) => DisconnectRequested?.Invoke();

        var opacity = new Forms.ToolStripMenuItem("Glass opacity");
        foreach (var (value, label) in new[] { (.35, "Clear"), (WidgetSettings.DefaultGlassOpacity, "Balanced"), (.68, "Tinted"), (.9, "Dark") })
        {
            var captured = value;
            var item = new Forms.ToolStripMenuItem(label);
            item.Click += (_, _) => OpacityChanged?.Invoke(captured);
            _opacityItems.Add(value, item);
            opacity.DropDownItems.Add(item);
        }

        _menu.Items.Add(_source);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_show);
        _menu.Items.Add(_ignore);
        _menu.Items.Add(_topmost);
        _menu.Items.Add(_glass);
        _menu.Items.Add(opacity);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(MenuItem("Settings…", () => SettingsRequested?.Invoke()));
        _menu.Items.Add(_startup);
        _menu.Items.Add(MenuItem("Reset position", () => ResetPositionRequested?.Invoke()));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_connect);
        _menu.Items.Add(_disconnect);
        _menu.Items.Add(MenuItem("Open Spotify", () => OpenSpotifyRequested?.Invoke()));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(MenuItem("Quit", () => ExitRequested?.Invoke()));
        SetState(true, false, true, false);
    }

    public void SetState(bool isVisible, bool ignoreClicks, bool topmost, bool isConnected,
        string? status = null, bool glass = true, bool startup = false, double opacity = WidgetSettings.DefaultGlassOpacity)
    {
        _updating = true;
        try
        {
            _show.Text = isVisible ? "Hide" : "Show";
            _ignore.Checked = ignoreClicks;
            _topmost.Checked = topmost;
            _glass.Checked = glass;
            _startup.Checked = startup;
            _connect.Visible = !isConnected;
            _disconnect.Visible = isConnected;
            foreach (var pair in _opacityItems) pair.Value.Checked = Math.Abs(pair.Key - opacity) < .02;
            SetStatus(status ?? (isConnected ? "Spotify connected" : "This PC"));
        }
        finally { _updating = false; }
    }

    public void SetStatus(string status)
    {
        if (_disposed) return;
        var text = string.IsNullOrWhiteSpace(status) ? "This PC" : status;
        _source.Text = text.Length > 100 ? text[..97] + "…" : text;
        var tooltip = "Lilt · " + text;
        _icon.Text = tooltip.Length > 63 ? tooltip[..60] + "…" : tooltip;
    }

    private static Forms.ToolStripMenuItem MenuItem(string label, Action callback)
    {
        var item = new Forms.ToolStripMenuItem(label);
        item.Click += (_, _) => callback();
        return item;
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(255, 20, 26, 29));
            graphics.FillEllipse(background, 2, 2, 60, 60);
            using var pen = new Pen(Color.FromArgb(255, 182, 226, 211), 5)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(pen, 20, 28, 20, 36);
            graphics.DrawLine(pen, 28, 20, 28, 44);
            graphics.DrawLine(pen, 36, 25, 36, 39);
            graphics.DrawLine(pen, 44, 29, 44, 35);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { NativeMethods.DestroyIcon(handle); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _ownedIcon.Dispose();
    }
}
