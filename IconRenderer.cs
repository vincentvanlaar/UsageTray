using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UsageTray;

enum IconStyle { Ring, Bars, Classic }

static class IconRenderer
{
    public static Icon Render(IconStyle style, IReadOnlyList<ProviderStatus> data, UsageWindow? ram = null)
    {
        using var bmp = Draw(style, data, Math.Max(16, SystemInformation.SmallIconSize.Width), LightTaskbar(), ram);
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public static Bitmap Draw(IconStyle style, IReadOnlyList<ProviderStatus> data, int size, bool light, UsageWindow? ram = null) => style switch
    {
        IconStyle.Bars => Bars(data, ram, size, light),
        IconStyle.Classic => Classic(Focus(data.SelectMany(p => p.Windows)), size),
        _ => Ring(Focus(data.SelectMany(p => p.Windows)), size, light),
    };

    /// <summary>The window most at risk: worst status first, then highest usage.</summary>
    public static UsageWindow? Focus(IEnumerable<UsageWindow> windows) =>
        windows.OrderByDescending(w => w.Level).ThenByDescending(w => w.UsedPct).FirstOrDefault();

    public static bool LightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    static Color Ink(bool light) => light ? Theme.Bg : Color.White;
    static Color Faint(bool light) => light ? Color.FromArgb(55, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255);

    /// <summary>Progress ring in the status color, pace tick on the ring, percentage inside.</summary>
    static Bitmap Ring(UsageWindow? focus, int s, bool light)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var t = Math.Max(2f, s * 0.13f);
        var ring = new RectangleF(t / 2, t / 2, s - t, s - t);
        using (var track = new Pen(Faint(light), t))
            g.DrawEllipse(track, ring);

        if (focus is not null)
        {
            var sweep = 360f * (float)Math.Clamp(focus.UsedPct / 100, 0, 1);
            if (sweep > 0)
            {
                using var arc = new Pen(Theme.For(focus.Level), t) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (sweep >= 359.5f) g.DrawEllipse(arc, ring);
                else g.DrawArc(arc, ring, -90, Math.Max(sweep, 1));
            }

            if (focus.EvenPct is { } even)
            {
                var angle = (-90 + 360 * even / 100) * Math.PI / 180;
                float r = ring.Width / 2, c = s / 2f;
                float inner = r - t * 0.5f, outer = r + t * 0.5f; // stays on the ring, clear of the digits
                using var tick = new Pen(Ink(light), Math.Max(1.5f, s * 0.08f));
                g.DrawLine(tick,
                    c + inner * (float)Math.Cos(angle), c + inner * (float)Math.Sin(angle),
                    c + outer * (float)Math.Cos(angle), c + outer * (float)Math.Sin(angle));
            }
        }

        var text = focus is null ? "?" : focus.UsedPct >= 99.5 ? "!" : $"{focus.UsedPct:0}";
        DrawCentered(g, text, s, s * (text.Length > 1 ? 0.41f : 0.52f), focus is null ? Theme.Muted : Ink(light));
        return bmp;
    }

    /// <summary>One vertical bar per provider (Codex | Claude), each with a pace tick, then RAM.</summary>
    static Bitmap Bars(IReadOnlyList<ProviderStatus> data, UsageWindow? ram, int s, bool light)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (data.Count == 0)
        {
            DrawCentered(g, "?", s, s * 0.56f, Theme.Muted);
            return bmp;
        }

        var bars = data.Take(2).Select(p => Focus(p.Windows)).ToList();
        if (ram is not null) bars.Add(ram);
        var n = bars.Count;
        float barW = s * n switch { 1 => 0.46f, 2 => 0.36f, _ => 0.25f }, gap = s * (n > 2 ? 0.1f : 0.14f);
        float x = (s - (n * barW + (n - 1) * gap)) / 2, top = s * 0.04f, h = s * 0.92f;

        foreach (var window in bars)
        {
            var bar = new RectangleF(x, top, barW, h);
            using var shape = Theme.RoundRect(bar, Math.Min(barW / 2, s * 0.14f));
            using (var track = new SolidBrush(Faint(light)))
                g.FillPath(track, shape);

            if (window is { } w)
            {
                var state = g.Save();
                g.SetClip(shape);
                var fillH = h * (float)Math.Clamp(w.UsedPct / 100, 0, 1);
                using (var fill = new SolidBrush(Theme.For(w.Level)))
                    g.FillRectangle(fill, x, top + h - fillH, barW, fillH);
                if (w.EvenPct is { } even)
                {
                    var tickH = Math.Max(1.2f, s * 0.08f);
                    using var tick = new SolidBrush(Ink(light));
                    g.FillRectangle(tick, x, top + h * (1 - (float)even / 100) - tickH / 2, barW, tickH);
                }
                g.Restore(state);
            }
            x += barW + gap;
        }
        return bmp;
    }

    /// <summary>Status-colored tile with the percentage.</summary>
    static Bitmap Classic(UsageWindow? focus, int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var tile = Theme.RoundRect(new RectangleF(0, 0, s, s), s * 0.22f))
        using (var fill = new SolidBrush(focus is null ? Theme.Muted : Theme.For(focus.Level)))
            g.FillPath(fill, tile);

        var text = focus is null ? "?" : focus.UsedPct >= 99.5 ? "!" : $"{focus.UsedPct:0}";
        DrawCentered(g, text, s, s * (text.Length > 1 ? 0.64f : 0.8f), Theme.Bg);
        return bmp;
    }

    static void DrawCentered(Graphics g, string text, int s, float px, Color color)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, new RectangleF(-2, s / 32f, s + 4, s), format);
    }

    /// <summary>Green disc with a white tick, for "signed in" menu items.</summary>
    public static Bitmap Check(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var inset = s * 0.06f;
        using (var disc = new SolidBrush(Theme.Ok))
            g.FillEllipse(disc, inset, inset, s - 2 * inset, s - 2 * inset);
        using var tick = new Pen(Color.White, Math.Max(1.5f, s * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(tick, [new PointF(s * 0.29f, s * 0.52f), new PointF(s * 0.44f, s * 0.67f), new PointF(s * 0.72f, s * 0.36f)]);
        return bmp;
    }

    /// <summary>App logo: two usage bars crossed by a pace tick.</summary>
    public static Bitmap Logo(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var inset = size * 0.03f;
        using (var tile = Theme.RoundRect(new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset), size * 0.22f))
        using (var bg = new SolidBrush(Theme.Bg))
            g.FillPath(bg, tile);

        float left = size * 0.2f, width = size * 0.6f, barH = Math.Max(2f, size * 0.12f);
        (float Y, float Fill, Color Color)[] bars = [(size * 0.36f, 0.72f, Theme.Warn), (size * 0.64f, 0.4f, Theme.Ok)];
        foreach (var (y, fill, color) in bars)
        {
            var top = y - barH / 2;
            using (var track = Theme.RoundRect(new RectangleF(left, top, width, barH), barH / 2))
            using (var brush = new SolidBrush(Theme.Track))
                g.FillPath(brush, track);
            using (var bar = Theme.RoundRect(new RectangleF(left, top, width * fill, barH), barH / 2))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, bar);
        }

        var tickW = Math.Max(1.5f, size * 0.045f);
        var tickX = left + width * 0.55f - tickW / 2;
        using var tick = new SolidBrush(Theme.Tick);
        g.FillRectangle(tick, tickX, size * 0.24f, tickW, size * 0.52f);
        return bmp;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
