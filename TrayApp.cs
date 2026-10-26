using System.Diagnostics;
using Microsoft.Win32;

namespace UsageTray;

sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(2);
    static readonly TimeSpan RamEvery = TimeSpan.FromSeconds(15);

    readonly PopupForm _popup = new(); // created first: installs the WinForms sync context for awaits
    readonly ProcessForm _procs = new();
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)RefreshEvery.TotalMilliseconds };
    readonly System.Windows.Forms.Timer _ramTimer = new() { Interval = (int)RamEvery.TotalMilliseconds };
    readonly ToolStripMenuItem _codexLogin, _claudeLogin;
    Bitmap? _check;
    IReadOnlyList<ProviderStatus> _data = [];
    DateTimeOffset? _updated;
    bool _busy;

    public TrayApp()
    {
        var autostart = new ToolStripMenuItem(L.T("Start with Windows", "Start met Windows"))
        {
            Checked = Autostart.Enabled,
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => Autostart.Enabled = autostart.Checked;

        var iconMenu = new ToolStripMenuItem(L.T("Icon", "Pictogram"));
        foreach (var (style, name) in new[]
        {
            (IconStyle.Ring, L.T("Ring", "Ring")),
            (IconStyle.Bars, L.T("Bars (Codex | Claude | RAM)", "Balkjes (Codex | Claude | RAM)")),
            (IconStyle.Classic, L.T("Classic", "Klassiek")),
        })
        {
            var item = new ToolStripMenuItem(name) { Checked = style == Settings.IconStyle };
            item.Click += (_, _) =>
            {
                Settings.IconStyle = style;
                foreach (ToolStripMenuItem other in iconMenu.DropDownItems) other.Checked = other == item;
                UpdateTray();
            };
            iconMenu.DropDownItems.Add(item);
        }

        _codexLogin = new ToolStripMenuItem(L.T("Sign in to Codex…", "Inloggen bij Codex…"), null, (_, _) => _ = LoginAsync("Codex"));
        _claudeLogin = new ToolStripMenuItem(L.T("Sign in to Claude…", "Inloggen bij Claude…"), null, (_, _) => _ = LoginAsync("Claude"));

        _tray.ContextMenuStrip = new ContextMenuStrip
        {
            Items =
            {
                new ToolStripMenuItem(L.T("Refresh", "Vernieuwen"), null, (_, _) => _ = RefreshAsync()),
                _codexLogin,
                _claudeLogin,
                new ToolStripSeparator(),
                iconMenu,
                autostart,
                new ToolStripSeparator(),
                new ToolStripMenuItem(L.T("Quit", "Afsluiten"), null, (_, _) => ExitThread()),
            },
        };
        // Green check on the sign-in items whose provider is signed in; drawn per DPI when the menu opens.
        _tray.ContextMenuStrip.Opening += (_, _) => UpdateLoginItems();
        _tray.Icon = IconRenderer.Render(Settings.IconStyle, []);
        // Light/dark taskbar switch: redraw with matching ink.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _tray.Text = "UsageTray";
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
        _tray.Visible = true;

        _popup.RefreshRequested += () => _ = RefreshAsync();
        _popup.LoginRequested += provider => _ = LoginAsync(provider);
        _popup.SystemClicked += () => _procs.ShowNear(_popup.Bounds);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        // RAM moves faster than the limits: keep the bars icon and the tooltip current.
        _ramTimer.Tick += (_, _) => UpdateTray();
        _ramTimer.Start();
        _ = RefreshAsync();
    }

    void TogglePopup()
    {
        if (_popup.Visible) { _popup.Hide(); return; }
        // The click that deactivated (and hid) a flyout lands here right after; don't reopen.
        if (new[] { _popup.LastHidden, _procs.LastHidden }.Any(t => (DateTime.Now - t).TotalMilliseconds < 300)) return;

        _popup.SetData(_data, _updated, SystemSource.Read());
        _popup.ShowNearTray();
        if (_updated is null || DateTimeOffset.Now - _updated > TimeSpan.FromSeconds(30)) _ = RefreshAsync();
    }

    async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var codex = Task.Run(CodexSource.ReadAsync);
            var claude = Task.Run(ClaudeSource.ReadAsync);
            _data = new[] { await codex, await claude }.OfType<ProviderStatus>().ToList();
            _updated = DateTimeOffset.Now;
            UpdateTray();
            _popup.SetData(_data, _updated, SystemSource.Read());
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Runs the provider's official CLI login in a console window, then refreshes.</summary>
    async Task LoginAsync(string provider)
    {
        var command = provider == "Codex" ? "codex login" : "claude auth login";
        try
        {
            using var login = Process.Start(new ProcessStartInfo("cmd.exe", $"/c title {provider} sign-in & {command} || pause")
            {
                UseShellExecute = true,
            });
            if (login is not null) await login.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "UsageTray", ex.Message, ToolTipIcon.Error);
        }
        await RefreshAsync();
    }

    void UpdateTray()
    {
        var old = _tray.Icon;
        var ram = SystemSource.Read().Windows.FirstOrDefault(w => w.Label == "ram");
        _tray.Icon = IconRenderer.Render(Settings.IconStyle, _data, ram);
        old?.Dispose();

        var lines = _data.Select(p => p.Error is not null
            ? $"{p.Name}: {p.Error}"
            : $"{p.Name}  " + string.Join("  ", p.Windows.Select(w => $"{w.Label} {w.UsedPct:0}% ({Fmt.Delta(w.Delta)})"))).ToList();
        if (ram is not null) lines.Add($"RAM  {ram.UsedPct:0}% ({ram.Detail})");
        var tip = string.Join("\n", lines);
        if (tip.Length == 0) tip = "UsageTray";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    void UpdateLoginItems()
    {
        var menu = _tray.ContextMenuStrip!;
        var size = (int)Math.Round(16 * menu.DeviceDpi / 96f);
        if (_check?.Width != size)
        {
            _check?.Dispose();
            _check = IconRenderer.Check(size);
            menu.ImageScalingSize = new Size(size, size);
        }

        foreach (var (item, name) in new[] { (_codexLogin, "Codex"), (_claudeLogin, "Claude") })
            item.Image = _data.Any(p => p.Name == name && !p.NeedsLogin) ? _check : null;
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) UpdateTray();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _popup.Dispose();
        _procs.Dispose();
        base.ExitThreadCore();
    }
}

static class Settings
{
    const string Key = @"Software\UsageTray";

    public static IconStyle IconStyle
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return Enum.TryParse<IconStyle>(key?.GetValue("IconStyle") as string, out var style) ? style : IconStyle.Ring;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("IconStyle", value.ToString());
        }
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "UsageTray";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
