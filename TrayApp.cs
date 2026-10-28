using System.Diagnostics;
using Microsoft.Win32;

namespace UsageTray;

sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(2);
    static readonly TimeSpan RamEvery = TimeSpan.FromSeconds(15);

    readonly PopupForm _popup = new(); // created first: installs the WinForms sync context for awaits
    readonly ProcessForm _procs = new();
    readonly SessionForm _sessions = new();
    readonly ContextMenuStrip _quick = new();
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)RefreshEvery.TotalMilliseconds };
    readonly System.Windows.Forms.Timer _ramTimer = new() { Interval = (int)RamEvery.TotalMilliseconds };
    readonly System.Windows.Forms.Timer _live = new() { Interval = 1000 }; // CPU/GPU rows while the flyout is open
    readonly ToolStripMenuItem _codexLogin, _claudeLogin;
    Bitmap? _check;
    IReadOnlyList<ProviderStatus> _data = [];
    DateTimeOffset? _updated;
    bool _busy, _again;

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
        _popup.SessionsRequested += (agent, head) =>
        {
            if (_sessions.Visible && _sessions.Agent == agent) _sessions.Hide();
            else _sessions.ShowBeside(_popup, agent, head.Top);
        };
        _popup.SettingsRequested += at => _quick.Show(_popup, at, ToolStripDropDownDirection.AboveLeft);
        _popup.Dismissed += _sessions.Hide;
        _sessions.Dismissed += _popup.Dismiss;
        BuildQuickSettings();
        _live.Tick += (_, _) =>
        {
            if (_popup.Visible && (Settings.ShowCpu || Settings.ShowGpu)) _popup.SetData(Shown, _updated, SystemSource.Read());
        };
        _live.Start();
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        // RAM moves faster than the limits: keep the bars icon and the tooltip current.
        _ramTimer.Tick += (_, _) => UpdateTray();
        _ramTimer.Start();
        _ = RefreshAsync();
    }

    void TogglePopup()
    {
        if (_popup.Visible) { _popup.Hide(); _sessions.Hide(); return; }
        // The click that deactivated (and hid) a flyout lands here right after; don't reopen.
        if (new[] { _popup.LastHidden, _procs.LastHidden, _sessions.LastHidden }.Any(t => (DateTime.Now - t).TotalMilliseconds < 300)) return;

        _popup.SetData(Shown, _updated, SystemSource.Read());
        _popup.ShowNearTray();
        if (_updated is null || DateTimeOffset.Now - _updated > TimeSpan.FromSeconds(30)) _ = RefreshAsync();
    }

    async Task RefreshAsync()
    {
        // A request during a refresh (a provider turned on, say) refreshes again when that one ends.
        if (_busy) { _again = true; return; }
        _busy = true;
        try
        {
            // A provider hidden in the quick settings isn't polled.
            var codex = Settings.ShowCodex ? Task.Run(CodexSource.ReadAsync) : Task.FromResult<ProviderStatus?>(null);
            var claude = Settings.ShowClaude ? Task.Run(ClaudeSource.ReadAsync) : Task.FromResult<ProviderStatus?>(null);
            _data = new[] { await codex, await claude }.OfType<ProviderStatus>().ToList();
            _updated = DateTimeOffset.Now;
            UpdateTray();
            _popup.SetData(Shown, _updated, SystemSource.Read());
        }
        finally
        {
            _busy = false;
        }
        if (_again)
        {
            _again = false;
            await RefreshAsync();
        }
    }

    // Providers turned off since the last refresh drop out at once.
    IReadOnlyList<ProviderStatus> Shown => _data.Where(Settings.Shows).ToList();

    /// <summary>The gear menu in the flyout: sections and features on or off. It stays open while toggling.</summary>
    void BuildQuickSettings()
    {
        void Toggle(string name, Func<bool> get, Action<bool> set, bool provider = false)
        {
            var item = new ToolStripMenuItem(name) { Checked = get(), CheckOnClick = true };
            item.CheckedChanged += (_, _) =>
            {
                set(item.Checked);
                if (_sessions.Visible) _sessions.Hide();
                UpdateTray();
                _popup.SetData(Shown, _updated, SystemSource.Read());
                if (provider && item.Checked) _ = RefreshAsync(); // a provider turned on has no data yet
            };
            _quick.Items.Add(item);
        }

        _quick.Items.Add(new ToolStripMenuItem(L.T("Show", "Tonen")) { Enabled = false });
        Toggle("Codex", () => Settings.ShowCodex, v => Settings.ShowCodex = v, provider: true);
        Toggle("Claude", () => Settings.ShowClaude, v => Settings.ShowClaude = v, provider: true);
        Toggle(L.T("Memory", "Geheugen"), () => Settings.ShowMemory, v => Settings.ShowMemory = v);
        Toggle("CPU", () => Settings.ShowCpu, v => Settings.ShowCpu = v);
        Toggle("GPU", () => Settings.ShowGpu, v => Settings.ShowGpu = v);
        _quick.Items.Add(new ToolStripSeparator());

        var stop = new ToolStripMenuItem(L.T("Offer stop after idle", "Stoppen aanbieden na stilte"));
        foreach (var minutes in new[] { 15, 30, 60, 120, 240, 0 })
        {
            var after = TimeSpan.FromMinutes(minutes);
            var item = new ToolStripMenuItem(minutes == 0 ? L.T("Never", "Nooit") : Fmt.Span(after)) { Checked = Settings.StopAfter == after };
            item.Click += (_, _) =>
            {
                Settings.StopAfter = after;
                foreach (ToolStripMenuItem other in stop.DropDownItems) other.Checked = other == item;
                if (_sessions.Visible) _sessions.Invalidate();
            };
            stop.DropDownItems.Add(item);
        }
        _quick.Items.Add(stop);
        _quick.Closing += (_, e) => e.Cancel = e.CloseReason == ToolStripDropDownCloseReason.ItemClicked;
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
        var shown = Shown;
        var ram = SystemSource.Ram();
        _tray.Icon = IconRenderer.Render(Settings.IconStyle, shown, ram);
        old?.Dispose();

        var lines = shown.Select(p => p.Error is not null
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
        _ramTimer.Dispose();
        _live.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _popup.Dispose();
        _procs.Dispose();
        _sessions.Dispose();
        _quick.Dispose();
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

    // Quick settings: what the flyout shows. Codex, Claude and Memory also apply to the tray icon.
    public static bool ShowCodex { get => Flag("ShowCodex", true); set => SetFlag("ShowCodex", value); }
    public static bool ShowClaude { get => Flag("ShowClaude", true); set => SetFlag("ShowClaude", value); }
    public static bool ShowMemory { get => Flag("ShowMemory", true); set => SetFlag("ShowMemory", value); }
    public static bool ShowCpu { get => Flag("ShowCpu", false); set => SetFlag("ShowCpu", value); }
    public static bool ShowGpu { get => Flag("ShowGpu", false); set => SetFlag("ShowGpu", value); }

    /// <summary>Idle time after which the session panel offers to stop a session; zero for never.</summary>
    public static TimeSpan StopAfter
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return TimeSpan.FromMinutes(key?.GetValue("StopAfterMinutes") is int minutes and >= 0 ? minutes : 30);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("StopAfterMinutes", (int)value.TotalMinutes, RegistryValueKind.DWord);
        }
    }

    public static bool Shows(ProviderStatus p) => p.Name switch
    {
        "Codex" => ShowCodex,
        "Claude" => ShowClaude,
        _ => true,
    };

    static bool Flag(string name, bool fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(name) is int value ? value != 0 : fallback;
    }

    static void SetFlag(string name, bool value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
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
