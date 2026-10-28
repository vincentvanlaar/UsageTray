using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace UsageTray;

/// <summary>
/// Side panel next to the main flyout: the running sessions of one agent, with status, memory
/// (and optionally CPU and GPU) per session. A session idle for longer than the setting offers
/// a stop button, which ends its processes to free the memory.
/// </summary>
sealed class SessionForm : Flyout
{
    // Layout in 96-dpi units. A row has two lines: title and numbers, then status and details.
    const int W = 440, Pad = 12, HeadH = 24, RowH = 38, FootH = 24, DotW = 14, BarW = 64, MemW = 44, MeterW = 38, StopW = 48;
    static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(3);

    string _agent = "Claude";
    IReadOnlyList<AgentSession>? _sessions;
    string? _error;
    DateTimeOffset? _updated;
    Flyout? _beside;
    int _top; // screen y of the header that opened it
    int _hover = -1; // row under the cursor
    bool _hoverStop; // and the cursor is on its stop button
    int _scroll; // index of the first session shown; the mouse wheel moves it
    int _wheel; // wheel delta not scrolled yet: touchpads send small steps
    int _pageRows = int.MaxValue; // rows that fit on the screen
    Rectangle _footer;
    bool _busy, _reload, _modal;
    readonly List<(Rectangle Row, Rectangle Stop, AgentSession Session)> _rows = [];
    readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)RefreshEvery.TotalMilliseconds };
    readonly ContextMenuStrip _menu = new();

    /// <summary>It hid because focus left both flyouts; the main flyout should hide too.</summary>
    public event Action? Dismissed;

    public string Agent => _agent;

    public SessionForm()
    {
        _timer.Tick += (_, _) => _ = LoadAsync();
    }

    /// <summary>Opens beside <paramref name="beside"/> (left of it, or right when there is no room), level with <paramref name="top"/>.</summary>
    public void ShowBeside(Flyout beside, string agent, int top)
    {
        if (_agent != agent) _sessions = null; // no other agent's list while this one loads
        (_beside, _agent, _top, _hover, _scroll) = (beside, agent, top, -1, 0);
        Relayout();
        Show();
        Activate();
        _timer.Start();
        _ = LoadAsync();
    }

    /// <summary>Renders the panel to a PNG without showing it (docs and layout checks).</summary>
    public void RenderTo(string agent, IReadOnlyList<AgentSession> sessions, string path)
    {
        (_agent, _sessions, _updated) = (agent, sessions, DateTimeOffset.Now);
        Relayout();
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        bmp.Save(path);
    }

    async Task LoadAsync()
    {
        // A request during a load (another agent, a stop) loads again when that one ends.
        if (_busy) { _reload = true; return; }
        _busy = true;
        try
        {
            do
            {
                _reload = false;
                try
                {
                    var (agent, gpu) = (_agent, Settings.ShowGpu);
                    var sessions = await Task.Run(() => SessionSource.Read(agent, gpu));
                    if (agent == _agent) (_sessions, _error, _updated) = (sessions, null, DateTimeOffset.Now);
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                }
            } while (_reload);
        }
        finally
        {
            _busy = false;
        }
        if (!Visible) return;
        Relayout();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _timer.Stop();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Focus moving to the main flyout keeps both open; anywhere else closes both.
        BeginInvoke(() =>
        {
            if (_modal || !Visible || ActiveForm is PopupForm) return;
            HideOnDeactivate();
            Dismissed?.Invoke();
        });
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _wheel += e.Delta;
        var notches = _wheel / 120; // WHEEL_DELTA
        _wheel -= notches * 120;
        var scroll = Math.Clamp(_scroll - notches * 3, 0, Math.Max(0, (_sessions?.Count ?? 0) - _pageRows));
        if (scroll == _scroll) return;
        (_scroll, _hover) = (scroll, -1);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Hide(); _beside?.Activate(); }
        else if (e.KeyCode is Keys.F5 or Keys.R) _ = LoadAsync();
        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = _rows.FindIndex(r => r.Row.Contains(e.Location));
        var onStop = hover >= 0 && _rows[hover].Stop.Contains(e.Location);
        if (hover != _hover || onStop != _hoverStop) { (_hover, _hoverStop) = (hover, onStop); Invalidate(); }
        Cursor = ActionAt(e.Location) is null ? Cursors.Default : Cursors.Hand;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) ActionAt(e.Location)?.Invoke();
        else if (e.Button == MouseButtons.Right && _rows.FirstOrDefault(r => r.Row.Contains(e.Location)).Session is { } s) ShowMenu(s, e.Location);
    }

    /// <summary>What a left click at <paramref name="p"/> does; null for nothing.</summary>
    Action? ActionAt(Point p)
    {
        if (_footer.Contains(p)) return () => _ = LoadAsync();
        foreach (var (row, stop, session) in _rows)
        {
            if (stop.Contains(p)) return () => Stop(session);
            if (row.Contains(p) && session.Group.Link is { } link) return () => Open(link);
        }
        return null;
    }

    bool OffersStop(AgentSession s) =>
        s.CanStop && s.State == SessionState.Idle && Settings.StopAfter > TimeSpan.Zero
        && s.Since is { } since && DateTimeOffset.Now - since >= Settings.StopAfter;

    void ShowMenu(AgentSession s, Point at)
    {
        _menu.Items.Clear();
        if (s.Group.Link is { } link)
        {
            _menu.Items.Add(new ToolStripMenuItem(L.T("Open in Claude", "Openen in Claude"), null, (_, _) => Open(link)));
            _menu.Items.Add(new ToolStripSeparator());
        }
        _menu.Items.Add(new ToolStripMenuItem(L.T("Stop session", "Sessie stoppen"), null, (_, _) => Stop(s)) { Enabled = s.CanStop });
        _menu.Show(this, at);
    }

    void Open(string link)
    {
        Hide();
        Dismissed?.Invoke();
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "UsageTray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Ends the session's processes. A session of the Claude app keeps its conversation there and
    /// resumes on the next message, so only other sessions ask first.
    /// </summary>
    void Stop(AgentSession s)
    {
        _modal = true; // the dialogs take activation; stay open behind them
        try
        {
            if (s.Group.Link is null)
            {
                var ask = L.T(
                    $"Stop “{s.Title}”?\n\n{s.Group.Members.Count} process(es), {Fmt.Bytes(s.Commit)}. A conversation in a terminal ends; resume it later with the agent's resume command.",
                    $"“{s.Title}” stoppen?\n\n{s.Group.Members.Count} proces(sen), {Fmt.Bytes(s.Commit)}. Een gesprek in een terminal stopt; hervat het later met het resume-commando van de agent.");
                if (MessageBox.Show(this, ask, "UsageTray", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
            }
            if (ProcessSource.End(s.Group.Members) is var failed and > 0)
                MessageBox.Show(this,
                    L.T($"{failed} process(es) could not be ended: access denied.", $"{failed} proces(sen) niet beëindigd: geen toegang."),
                    "UsageTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _modal = false;
            Activate();
        }
        _ = LoadAsync();
    }

    protected override void Relayout()
    {
        // Beside the flyout, only as many rows as the screen holds; the rest scroll.
        var beside = _beside is { Visible: true } b ? b : null;
        var area = beside is null ? Rectangle.Empty : AvailableArea(Screen.FromControl(beside));
        _pageRows = beside is null ? int.MaxValue : Math.Max(1, (area.Height - Px(20) - Px(Pad + HeadH + 4 + FootH + 4)) / Px(RowH));
        var count = _sessions?.Count ?? 0;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, count - _pageRows));

        var rows = Math.Clamp(count, 1, _pageRows);
        var meters = (Settings.ShowCpu ? 1 : 0) + (Settings.ShowGpu ? 1 : 0);
        Size = new Size(Px(W + meters * MeterW), Px(Pad + HeadH + rows * RowH + 4 + FootH + 4));
        if (beside is null) return;

        var gap = Px(8);
        var x = beside.Left - gap - Width >= area.Left ? beside.Left - gap - Width
            : beside.Right + gap + Width <= area.Right ? beside.Right + gap
            : area.Left;
        var y = Math.Max(area.Top + Px(10), Math.Min(_top, area.Bottom - Px(10) - Height));
        Location = new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var title = new Font("Segoe UI Semibold", Px(13), GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", Px(11.5f), GraphicsUnit.Pixel);
        using var text = new Font("Segoe UI", Px(12.5f), GraphicsUnit.Pixel);
        using var mono = new Font("Consolas", Px(12), GraphicsUnit.Pixel);
        using var monoBold = new Font("Consolas", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);

        int left = Px(Pad), right = Width - Px(Pad), y = Px(Pad);
        var (cpu, gpu) = (Settings.ShowCpu, Settings.ShowGpu);
        _rows.Clear();

        // Columns from the right: stop button, gpu, cpu, memory, memory bar.
        var stopX = right - Px(StopW);
        var gpuX = gpu ? stopX - Px(MeterW) : stopX;
        var cpuX = cpu ? gpuX - Px(MeterW) : gpuX;
        var memX = cpuX - Px(MemW);
        var barX = memX - Px(BarW) - Px(4);

        var head = new Rectangle(left, y, right - left, Px(HeadH));
        var name = L.T($"{_agent} sessions", $"{_agent}-sessies");
        Label(g, name, title, Theme.Text, head);
        if (_error is not null)
            Label(g, _error, small, Theme.Hot, Rectangle.FromLTRB(left + Px(140), y, right, y + Px(HeadH)), right: true);
        else
        {
            if (_sessions is { Count: > 0 } all)
            {
                var nameW = TextRenderer.MeasureText(g, name, title, Size.Empty, TextFormatFlags.NoPadding).Width;
                var range = all.Count > _pageRows ? $" · {_scroll + 1}–{Math.Min(all.Count, _scroll + _pageRows)} · scroll" : "";
                Label(g, $"{all.Count} · {Fmt.Bytes(all.Sum(s => s.Commit))}{range}", small, Theme.Muted,
                    Rectangle.FromLTRB(left + nameW + Px(10), y, barX, y + Px(HeadH)));
            }
            Label(g, "ram", small, Theme.Muted, new Rectangle(memX, y, Px(MemW), Px(HeadH)), right: true);
            if (cpu) Label(g, "cpu", small, Theme.Muted, new Rectangle(cpuX, y, Px(MeterW), Px(HeadH)), right: true);
            if (gpu) Label(g, "gpu", small, Theme.Muted, new Rectangle(gpuX, y, Px(MeterW), Px(HeadH)), right: true);
        }
        y += Px(HeadH);

        if (_sessions is not { Count: > 0 })
        {
            var empty = _sessions is null ? L.T("loading…", "laden…") : L.T("No running sessions", "Geen actieve sessies");
            Label(g, empty, small, Theme.Muted, new Rectangle(left, y, right - left, Px(RowH)));
            y += Px(RowH);
        }
        else
        {
            var largest = Math.Max(1, _sessions.Max(s => s.Commit));
            foreach (var s in _sessions.Skip(_scroll).Take(_pageRows))
            {
                var row = new Rectangle(0, y, Width, Px(RowH));
                var stop = OffersStop(s) ? new Rectangle(stopX + Px(6), y + Px(4), Px(StopW - 6), Px(18)) : Rectangle.Empty;
                if (_rows.Count == _hover)
                    using (var hover = new SolidBrush(Color.FromArgb(0x24, 0x26, 0x2D)))
                        g.FillRectangle(hover, row);
                _rows.Add((row, stop, s));
                DrawSession(g, s, stop, left, y, barX, memX, cpu ? cpuX : -1, gpu ? gpuX : -1, largest, text, small, mono, monoBold);
                y += Px(RowH);
            }
        }

        y += Px(4);
        using (var line = new Pen(Theme.Track, 1))
            g.DrawLine(line, left, y, right, y);
        _footer = new Rectangle(0, y, Width, Height - y);
        var footer = Rectangle.FromLTRB(left, y, right, y + Px(FootH));
        if (_updated is { } u)
            Label(g, $"{u:HH:mm:ss} · " + L.T("click to refresh", "klik = verversen"), small, Theme.Muted, footer);
        var hint = Settings.StopAfter > TimeSpan.Zero
            ? L.T($"stop after {Fmt.Span(Settings.StopAfter)} idle · right-click = menu", $"stop na {Fmt.Span(Settings.StopAfter)} stil · rechtsklik = menu")
            : L.T("right-click = menu", "rechtsklik = menu");
        Label(g, hint, small, Theme.Muted, footer, right: true);
    }

    void DrawSession(Graphics g, AgentSession s, Rectangle stop, int left, int y, int barX, int memX, int cpuX, int gpuX, long largest,
        Font text, Font small, Font mono, Font monoBold)
    {
        var line1 = new Rectangle(0, y + Px(3), 0, Px(18));
        var line2 = new Rectangle(0, y + Px(20), 0, Px(15));
        var color = StateColor(s.State);

        var dot = Px(8);
        using (var brush = new SolidBrush(color))
            g.FillEllipse(brush, left, line1.Top + (line1.Height - dot) / 2f, dot, dot);

        var nameL = left + Px(DotW);
        Label(g, s.Title, text, Theme.Text, Rectangle.FromLTRB(nameL, line1.Top, barX - Px(8), line1.Bottom));

        // Second line: status in its color, then the details.
        var stateW = TextRenderer.MeasureText(g, s.StateText, small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        Label(g, s.StateText, small, color, new Rectangle(nameL, line2.Top, stateW + Px(2), line2.Height));
        if (s.Detail is { } detail)
            Label(g, "· " + detail, small, Theme.Muted, Rectangle.FromLTRB(nameL + stateW + Px(6), line2.Top, memX + Px(MemW), line2.Bottom));

        // Memory bar, relative to the largest session in the list.
        float barH = Px(6), barY = line1.Top + (line1.Height - barH) / 2f, barW = Px(BarW);
        using (var track = Theme.RoundRect(new RectangleF(barX, barY, barW, barH), barH / 2))
        using (var brush = new SolidBrush(Theme.Track))
            g.FillPath(brush, track);
        var fillW = barW * s.Commit / largest;
        if (fillW > 0.5f)
        {
            using var fill = Theme.RoundRect(new RectangleF(barX, barY, Math.Max(fillW, barH), barH), barH / 2);
            using var brush = new SolidBrush(Theme.Muted);
            g.FillPath(brush, fill);
        }
        Label(g, Fmt.Bytes(s.Commit), monoBold, Theme.Text, new Rectangle(memX, line1.Top, Px(MemW), line1.Height), right: true);
        if (cpuX >= 0) Label(g, Percent(s.Cpu), mono, Theme.Muted, new Rectangle(cpuX, line1.Top, Px(MeterW), line1.Height), right: true);
        if (gpuX >= 0) Label(g, Percent(s.Gpu), mono, Theme.Muted, new Rectangle(gpuX, line1.Top, Px(MeterW), line1.Height), right: true);

        if (stop.IsEmpty) return;
        using (var pill = Theme.RoundRect(stop, stop.Height / 2f))
        using (var brush = new SolidBrush(_hoverStop && _rows.Count - 1 == _hover ? Theme.Hot : Theme.Track))
            g.FillPath(brush, pill);
        TextRenderer.DrawText(g, L.T("stop", "stop"), small, stop, Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    static string Percent(double? pct) => pct is { } p ? $"{p:0}%" : "—";

    static Color StateColor(SessionState state) => state switch
    {
        SessionState.Working => Theme.Ok,
        SessionState.NeedsYou => Theme.Warn,
        _ => Theme.Muted,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
