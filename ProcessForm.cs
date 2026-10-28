using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace UsageTray;

/// <summary>
/// Second flyout: the apps that hold the most memory. Click a row to see its processes
/// (Claude sessions: click opens the session, the chevron shows the processes),
/// right-click to end the app or one process.
/// </summary>
sealed class ProcessForm : Flyout
{
    // Layout in 96-dpi units.
    const int W = 480, Pad = 12, HeadH = 24, RowH = 20, FootH = 24, Indent = 14, MemW = 52, MaxMembers = 8, PageRows = 15;

    IReadOnlyList<ProcGroup>? _groups;
    string? _error;
    DateTimeOffset? _updated;
    DateTime _shown;
    int? _open; // root pid of the expanded group
    int _scroll; // index of the first app shown; the mouse wheel moves it
    int _wheel; // wheel delta not scrolled yet: touchpads send small steps
    Point _anchor; // bottom-right corner to grow from
    Rectangle _footer;
    bool _busy, _reload, _modal;
    readonly List<(Rectangle Bounds, ProcGroup Group, ProcRow? Member)> _rows = [];
    readonly ContextMenuStrip _menu = new();

    /// <summary>Opens where <paramref name="anchor"/> (the main flyout) is, growing up and left from its corner.</summary>
    public void ShowNear(Rectangle anchor)
    {
        _anchor = new Point(anchor.Right, anchor.Bottom);
        (_groups, _open, _scroll, _shown) = (null, null, 0, DateTime.Now); // no old list while the new one loads
        Relayout();
        Show();
        Activate();
        _ = LoadAsync();
    }

    /// <summary>Renders the flyout to a PNG without showing it, first multi-process app expanded (layout checks).</summary>
    public void RenderTo(IReadOnlyList<ProcGroup> groups, string path)
    {
        (_groups, _updated) = (groups, DateTimeOffset.Now);
        _open = groups.FirstOrDefault(g => g.Members.Count > 1)?.RootPid;
        Relayout();
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        bmp.Save(path);
    }

    async Task LoadAsync()
    {
        // A request during a load (after End, say) loads again when that one ends.
        if (_busy) { _reload = true; return; }
        _busy = true;
        try
        {
            do
            {
                _reload = false;
                try
                {
                    (_groups, _error) = (await Task.Run(() => ProcessSource.Read()), null);
                    _updated = DateTimeOffset.Now;
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
        ScrollBy(0);
    }

    void ScrollBy(int by)
    {
        var scroll = Math.Clamp(_scroll + by, 0, Math.Max(0, (_groups?.Count ?? 0) - PageRows));
        if (by != 0 && scroll == _scroll) return;
        _scroll = scroll;
        Fit(keepTop: by != 0);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _wheel += e.Delta;
        var notches = _wheel / 120; // WHEEL_DELTA
        _wheel -= notches * 120;
        if (notches != 0) ScrollBy(-notches * 3);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_modal) HideOnDeactivate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Hide();
        else if (e.KeyCode is Keys.F5 or Keys.R) _ = LoadAsync();
        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = ActionAt(e.Location) is null ? Cursors.Default : Cursors.Hand;
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        // The second click of a double-click on the System section lands here.
        if ((DateTime.Now - _shown).TotalMilliseconds < SystemInformation.DoubleClickTime) return;
        if (e.Button == MouseButtons.Right && RowAt(e.Location) is { } row) ShowMenu(row.Group, row.Member, e.Location);
        else if (e.Button == MouseButtons.Left) ActionAt(e.Location)?.Invoke();
    }

    (ProcGroup Group, ProcRow? Member)? RowAt(Point p)
    {
        foreach (var (bounds, group, member) in _rows)
            if (bounds.Contains(p)) return (group, member);
        return null;
    }

    /// <summary>What a left click at <paramref name="p"/> does; null for nothing.</summary>
    Action? ActionAt(Point p)
    {
        if (_footer.Contains(p)) return () => _ = LoadAsync();
        if (RowAt(p) is not { Member: null, Group: var group }) return null;
        var expandable = group.Members.Count > 1;
        // A session row opens the session; its chevron shows the processes.
        if (group.Link is { } link && (!expandable || p.X > Px(Pad + Indent))) return () => Open(link);
        if (expandable) return () => Toggle(group);
        return null;
    }

    void Toggle(ProcGroup group)
    {
        _open = _open == group.RootPid ? null : group.RootPid;
        Fit(keepTop: true);
        Invalidate();
    }

    void ShowMenu(ProcGroup group, ProcRow? member, Point at)
    {
        _menu.Items.Clear();
        if (member is not null)
        {
            var name = $"{member.Name} (pid {member.Pid})";
            _menu.Items.Add(new ToolStripMenuItem(NoMnemonic(L.T($"End {name}", $"{name} beëindigen")), null, (_, _) => End(name, [member]))
            {
                Enabled = !member.Protected,
            });
        }
        else
        {
            if (group.Link is { } link)
            {
                _menu.Items.Add(new ToolStripMenuItem(L.T("Open in Claude", "Openen in Claude"), null, (_, _) => Open(link)));
                _menu.Items.Add(new ToolStripSeparator());
            }
            var all = group.Members.Count > 1 ? L.T($" and its {group.Members.Count} processes", $" en de {group.Members.Count} processen") : "";
            _menu.Items.Add(new ToolStripMenuItem(NoMnemonic(L.T($"End “{group.Title}”{all}", $"“{group.Title}”{all} beëindigen")), null,
                (_, _) => End($"“{group.Title}”", group.Members, group.Sessions))
            {
                Enabled = !group.Protected,
            });
        }
        _menu.Show(this, at);
    }

    // Menu items read '&' as a keyboard-shortcut marker.
    static string NoMnemonic(string text) => text.Replace("&", "&&");

    void Open(string link)
    {
        Hide();
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "UsageTray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void End(string what, IReadOnlyList<ProcRow> rows, int sessions = 0)
    {
        _modal = true; // the dialogs take activation; stay open behind them
        try
        {
            var size = Fmt.Bytes(rows.Sum(r => r.Commit));
            var ask = L.T(
                $"End {what}?\n\n{rows.Count} process(es), {size}. Unsaved work in them is lost.",
                $"{what} beëindigen?\n\n{rows.Count} proces(sen), {size}. Niet-opgeslagen werk gaat verloren.");
            if (sessions > 0)
                ask += "\n" + L.T(
                    $"{sessions} Claude Code or Codex session(s) started from it can end too.",
                    $"{sessions} Claude Code- of Codex-sessie(s) die het startte kunnen ook stoppen.");
            if (MessageBox.Show(this, ask, "UsageTray", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            if (ProcessSource.End(rows) is var failed and > 0)
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

    IEnumerable<ProcRow> Largest(ProcGroup group) => group.Members.OrderByDescending(m => m.Commit).Take(MaxMembers);

    // A group that is down to one process has nothing to expand.
    bool IsOpen(ProcGroup group) => _open == group.RootPid && group.Members.Count > 1;

    IEnumerable<ProcGroup> Page => _groups!.Skip(_scroll).Take(PageRows);

    protected override void Relayout() => Fit(keepTop: false);

    /// <param name="keepTop">Grow downward, so the row under the cursor stays where it is.</param>
    void Fit(bool keepTop)
    {
        var rows = _groups is null ? 1 : Page.Sum(g => 1 + (IsOpen(g) ? Largest(g).Count() : 0));
        Size = new Size(Px(W), Px(Pad + HeadH + rows * RowH + 4 + FootH + 4));

        var area = AvailableArea(Screen.FromPoint(_anchor));
        var margin = Px(10);
        var top = keepTop ? Math.Min(Top, area.Bottom - margin - Height) : Math.Min(_anchor.Y, area.Bottom - margin) - Height;
        Location = new Point(
            Math.Max(area.Left + margin, Math.Min(_anchor.X, area.Right - margin) - Width),
            Math.Max(area.Top + margin, top));
        _anchor.Y = Bottom;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var title = new Font("Segoe UI Semibold", Px(13), GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", Px(11.5f), GraphicsUnit.Pixel);
        using var text = new Font("Segoe UI", Px(12.5f), GraphicsUnit.Pixel);
        using var monoBold = new Font("Consolas", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);

        int left = Px(Pad), right = Width - Px(Pad), y = Px(Pad);
        _rows.Clear();

        var head = new Rectangle(left, y, right - left, Px(HeadH));
        Label(g, L.T("Memory", "Geheugen"), title, Theme.Text, head);
        var range = _groups is { Count: > PageRows } all
            ? L.T($" · {_scroll + 1}–{_scroll + Page.Count()} of {all.Count} · scroll", $" · {_scroll + 1}–{_scroll + Page.Count()} van {all.Count} · scroll")
            : "";
        Label(g, _error ?? L.T("commit per app", "commit per app") + range, small, _error is null ? Theme.Muted : Theme.Hot,
            Rectangle.FromLTRB(left + Px(80), y, right, y + Px(HeadH)), right: true);
        y += Px(HeadH);

        if (_groups is null)
        {
            Label(g, L.T("loading…", "laden…"), small, Theme.Muted, new Rectangle(left, y, right - left, Px(RowH)));
            y += Px(RowH);
        }
        else foreach (var group in Page)
        {
            var open = IsOpen(group);
            _rows.Add((new Rectangle(0, y, Width, Px(RowH)), group, null));
            var mark = group.Members.Count > 1 ? (open ? "▾" : "▸") : "";
            DrawRow(g, mark, group.Title, Theme.Text, group.Detail, group.Commit, left, right, y, text, small, monoBold);
            y += Px(RowH);
            if (!open) continue;

            foreach (var member in Largest(group))
            {
                _rows.Add((new Rectangle(0, y, Width, Px(RowH)), group, member));
                DrawRow(g, "", member.Name, Theme.Muted, member.Detail, member.Commit, left + Px(Indent), right, y, text, small, monoBold);
                y += Px(RowH);
            }
        }

        y += Px(4);
        using (var line = new Pen(Theme.Track, 1))
            g.DrawLine(line, left, y, right, y);
        _footer = new Rectangle(0, y, Width, Height - y);
        var footer = Rectangle.FromLTRB(left, y, right, y + Px(FootH));
        if (_updated is { } u)
            Label(g, $"{u:HH:mm} · " + L.T("click to refresh", "klik = verversen"), small, Theme.Muted, footer);
        Label(g, L.T("click = open / processes · right-click = end", "klik = openen / processen · rechtsklik = beëindigen"), small, Theme.Muted, footer, right: true);
    }

    void DrawRow(Graphics g, string mark, string name, Color color, string detail, long commit, int left, int right, int y, Font text, Font small, Font monoBold)
    {
        var h = Px(RowH);
        var memX = right - Px(MemW);
        Label(g, mark, small, Theme.Muted, new Rectangle(left, y, Px(Indent), h));

        // The detail keeps what it needs up to 45% of the row; the name gets the rest.
        int nameL = left + Px(Indent), nameR = memX - Px(8);
        var detailW = detail.Length == 0 ? 0
            : Math.Min(TextRenderer.MeasureText(g, detail, small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Px(2), (nameR - nameL) * 45 / 100);
        Label(g, name, text, color, Rectangle.FromLTRB(nameL, y, nameR - detailW - (detailW > 0 ? Px(10) : 0), y + h));
        if (detailW > 0) Label(g, detail, small, Theme.Muted, new Rectangle(nameR - detailW, y, detailW, h), right: true);

        Label(g, Fmt.Bytes(commit), monoBold, Theme.Text, new Rectangle(memX, y, Px(MemW), h), right: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}
