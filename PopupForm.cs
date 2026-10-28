using System.Drawing.Drawing2D;

namespace UsageTray;

/// <summary>
/// Borderless flyout above the tray. Per window: label, bar with a pace tick,
/// used %, distance from even pace, time until reset. A provider's name opens its
/// sessions beside it; the gear in the footer opens the quick settings.
/// </summary>
sealed class PopupForm : Flyout
{
    // Layout in 96-dpi units.
    const int W = 340, Pad = 12, HeadH = 24, RowH = 20, GapH = 8, FootH = 24;
    const int LabelW = 46, PctW = 40, DeltaW = 36, ResetW = 56;

    IReadOnlyList<ProviderStatus> _data = [];
    ProviderStatus? _system; // RAM and commit, below the limits; a click opens the memory flyout
    DateTimeOffset? _updated;
    Point _trayPoint;
    Rectangle _footer, _systemArea, _gear;
    readonly List<(Rectangle Bounds, string Provider)> _logins = [];
    readonly List<(Rectangle Bounds, string Provider)> _heads = []; // provider names that open the session panel
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 30_000 };

    public event Action? RefreshRequested;
    public event Action<string>? LoginRequested;
    public event Action? SystemClicked;
    /// <summary>A provider's name was clicked; the rectangle is its header on screen.</summary>
    public event Action<string, Rectangle>? SessionsRequested;
    /// <summary>The gear was clicked; the point is where the menu should open, in client coordinates.</summary>
    public event Action<Point>? SettingsRequested;
    /// <summary>It hid because focus left it (and not for the session panel).</summary>
    public event Action? Dismissed;

    public PopupForm()
    {
        _tick.Tick += (_, _) => Invalidate();
    }

    public void SetData(IReadOnlyList<ProviderStatus> data, DateTimeOffset? updated, ProviderStatus? system = null)
    {
        _data = data;
        _updated = updated;
        _system = system;
        if (!Visible) return;
        Relayout();
        Invalidate();
    }

    public void ShowNearTray()
    {
        _trayPoint = Cursor.Position;
        Relayout();
        PositionNearTray();
        Show();
        Activate();
        _tick.Start();
    }

    void PositionNearTray()
    {
        var area = AvailableArea(Screen.FromPoint(_trayPoint));
        var margin = Px(10);
        var x = Math.Clamp(_trayPoint.X - Width / 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin));
        var y = _trayPoint.Y <= area.Top ? area.Top + margin : area.Bottom - Height - margin;
        Location = new Point(x, y);
    }

    /// <summary>Renders the flyout to a PNG without showing it (docs and layout checks).</summary>
    public void RenderTo(IReadOnlyList<ProviderStatus> data, string path, ProviderStatus? system = null)
    {
        _data = data;
        _system = system;
        _updated = DateTimeOffset.Now;
        Relayout();
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        bmp.Save(path);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Focus moving to the session panel beside it keeps both open.
        BeginInvoke(() => { if (ActiveForm is not SessionForm) Dismiss(); });
    }

    /// <summary>Hides as when focus leaves it, so a tray click right after does not reopen it.</summary>
    public void Dismiss()
    {
        if (!Visible) return;
        HideOnDeactivate();
        _tick.Stop();
        Dismissed?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Dismiss();
        else if (e.KeyCode is Keys.F5 or Keys.R) RefreshRequested?.Invoke();
        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = LoginAt(e.Location) is not null || HeadAt(e.Location) is not null || _footer.Contains(e.Location) || _systemArea.Contains(e.Location)
            ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (LoginAt(e.Location) is { } provider) { Hide(); LoginRequested?.Invoke(provider); }
        else if (HeadAt(e.Location) is { } head) SessionsRequested?.Invoke(head.Provider, RectangleToScreen(head.Bounds));
        else if (_gear.Contains(e.Location)) SettingsRequested?.Invoke(new Point(_gear.Left, _gear.Bottom));
        else if (_systemArea.Contains(e.Location)) SystemClicked?.Invoke();
        else if (_footer.Contains(e.Location)) RefreshRequested?.Invoke();
        base.OnMouseClick(e);
    }

    string? LoginAt(Point p) => _logins.FirstOrDefault(l => l.Bounds.Contains(p)).Provider;

    (Rectangle Bounds, string Provider)? HeadAt(Point p)
    {
        foreach (var head in _heads)
            if (head.Bounds.Contains(p)) return head;
        return null;
    }

    IEnumerable<ProviderStatus> Sections => _system is null ? _data : _data.Append(_system);

    // The "loading…" / none-found line; not when the quick settings hide both providers.
    bool ShowsEmpty => _data.Count == 0 && (Settings.ShowCodex || Settings.ShowClaude);

    protected override void Relayout()
    {
        // The "loading…" / none-found line takes a section's place when no provider is there.
        var heights = Sections.Select(p => HeadH + p.Windows.Count * RowH).ToList();
        if (ShowsEmpty || heights.Count == 0) heights.Insert(0, HeadH);
        Size = new Size(Px(W), Px(Pad + heights.Sum() + GapH * (heights.Count - 1) + FootH + 4));
        if (Visible) PositionNearTray();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var title = new Font("Segoe UI Semibold", Px(13), GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", Px(11.5f), GraphicsUnit.Pixel);
        using var mono = new Font("Consolas", Px(12), GraphicsUnit.Pixel);
        using var monoBold = new Font("Consolas", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);

        int left = Px(Pad), right = Width - Px(Pad), y = Px(Pad);
        _logins.Clear();
        _heads.Clear();
        _systemArea = Rectangle.Empty;

        if (ShowsEmpty)
        {
            var empty = _updated is null
                ? L.T("loading…", "laden…")
                : L.T("No Codex or Claude Code found", "Geen Codex of Claude Code gevonden");
            Label(g, empty, small, Theme.Muted, new Rectangle(left, y, right - left, Px(HeadH)));
            y += Px(HeadH + GapH);
        }

        foreach (var p in Sections)
        {
            var head = new Rectangle(left, y, right - left, Px(HeadH));
            Label(g, p.Name, title, Theme.Text, head);
            var nameW = TextRenderer.MeasureText(g, p.Name, title, Size.Empty, TextFormatFlags.NoPadding).Width + Px(12);
            if (p != _system)
            {
                Label(g, "›", title, Theme.Muted, new Rectangle(left + nameW - Px(8), y, Px(14), Px(HeadH)));
                nameW += Px(10);
                _heads.Add((Rectangle.FromLTRB(0, y, left + nameW, y + Px(HeadH)), p.Name));
            }
            var meta = p.Error ?? Fmt.Join(p.Plan, p.Note);
            if (meta is not null)
            {
                var metaBounds = Rectangle.FromLTRB(left + nameW, y, right, y + Px(HeadH));
                Label(g, meta, small, p.Error is null ? Theme.Muted : Theme.Hot, metaBounds, right: true);
                if (p.NeedsLogin) _logins.Add((metaBounds, p.Name));
            }
            y += Px(HeadH);

            foreach (var w in p.Windows)
            {
                DrawRow(g, w, mono, monoBold, left, right, y);
                y += Px(RowH);
            }
            if (p == _system) _systemArea = Rectangle.FromLTRB(0, head.Top, Width, y);
            y += Px(GapH);
        }
        y -= Px(GapH);

        y += Px(4);
        using (var line = new Pen(Theme.Track, 1))
            g.DrawLine(line, left, y, right, y);
        _footer = new Rectangle(0, y, Width, Height - y);
        var footer = Rectangle.FromLTRB(left, y, right, y + Px(FootH));
        if (_updated is { } u)
            Label(g, $"{u:HH:mm} · " + L.T("click to refresh", "klik = verversen"), small, Theme.Muted, footer);
        _gear = Rectangle.FromLTRB(right - Px(20), y, Width, y + Px(FootH));
        _footer.Width = _gear.Left;
        using (var symbol = new Font("Segoe UI Symbol", Px(13), GraphicsUnit.Pixel))
            Label(g, "⚙", symbol, Theme.Muted, Rectangle.FromLTRB(_gear.Left, y, right, footer.Bottom), right: true);
        Label(g, L.T("▏pace   + faster / − slower", "▏pace   + sneller / − trager"), small, Theme.Muted,
            Rectangle.FromLTRB(footer.Left, footer.Top, _gear.Left - Px(6), footer.Bottom), right: true);
    }

    void DrawRow(Graphics g, UsageWindow w, Font mono, Font monoBold, int left, int right, int y)
    {
        var rowH = Px(RowH);
        var color = Theme.For(w.Level);

        Label(g, w.Label, mono, Theme.Muted, new Rectangle(left, y, Px(LabelW), rowH));

        var resetX = right - Px(ResetW);
        var deltaX = resetX - Px(DeltaW);
        var pctX = deltaX - Px(PctW);
        int barL = left + Px(LabelW), barR = pctX - Px(4);
        float barW = barR - barL, barH = Px(6), barY = y + (rowH - barH) / 2f;

        using (var track = Theme.RoundRect(new RectangleF(barL, barY, barW, barH), barH / 2))
        using (var brush = new SolidBrush(Theme.Track))
            g.FillPath(brush, track);

        var fillW = barW * (float)Math.Clamp(w.UsedPct / 100, 0, 1);
        if (fillW > 0.5f)
        {
            using var fill = Theme.RoundRect(new RectangleF(barL, barY, Math.Max(fillW, barH), barH), barH / 2);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, fill);
        }

        // Subtle segment gaps: one per day for multi-day windows, one per hour otherwise.
        var segments = w.Length.TotalDays >= 2 ? (int)Math.Round(w.Length.TotalDays) : (int)Math.Round(w.Length.TotalHours);
        if (segments is > 1 and <= 24)
        {
            using var gap = new SolidBrush(Theme.Bg);
            var gapW = Math.Max(1, Px(1));
            for (var k = 1; k < segments; k++)
            {
                var gx = (int)Math.Round(barL + barW * k / segments);
                g.FillRectangle(gap, gx, (int)barY, gapW, (int)Math.Ceiling(barH));
            }
        }

        if (w.EvenPct is { } even)
        {
            var tx = barL + barW * (float)(even / 100);
            using var tick = new SolidBrush(Theme.Tick);
            g.FillRectangle(tick, tx - Px(1), y + Px(4), Math.Max(2, Px(2)), rowH - Px(8));
        }

        Label(g, $"{w.UsedPct:0}%", monoBold, Theme.Text, new Rectangle(pctX, y, Px(PctW), rowH), right: true);
        Label(g, Fmt.Delta(w.Delta), mono, w.Delta > 1 ? color : Theme.Ok, new Rectangle(deltaX, y, Px(DeltaW), rowH), right: true);
        var reset = w.Detail ?? (w.ResetsAt is { } r ? Fmt.Span(r - DateTimeOffset.Now) : "—");
        Label(g, reset, mono, Theme.Muted, new Rectangle(resetX, y, Px(ResetW), rowH), right: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}
