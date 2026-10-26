using System.Drawing.Imaging;

namespace UsageTray;

/// <summary>Generates the README images and the app icon (`UsageTray --render-assets`).</summary>
static class DocAssets
{
    public static void Render(string docsDir, string assetsDir)
    {
        Directory.CreateDirectory(docsDir);
        Directory.CreateDirectory(assetsDir);

        using (var popup = new PopupForm())
            popup.RenderTo(Demo(), Path.Combine(docsDir, "flyout.png"), DemoSystem());
        using (var logo = IconRenderer.Logo(256))
            logo.Save(Path.Combine(docsDir, "logo.png"), ImageFormat.Png);
        File.Delete(Path.Combine(docsDir, "tray-icons.png"));
        foreach (var style in Enum.GetValues<IconStyle>())
        {
            using var strip = IconStrip(style, 32);
            strip.Save(Path.Combine(docsDir, $"icon-{style.ToString().ToLowerInvariant()}.png"), ImageFormat.Png);
        }
        WriteIco(Path.Combine(assetsDir, "app.ico"), [16, 20, 24, 32, 40, 48, 64, 128, 256]);
    }

    // One window per status color, so the screenshot shows the whole palette.
    static ProviderStatus[] Demo()
    {
        var now = DateTimeOffset.Now;
        return
        [
            new("Codex", [new("7d", 64, TimeSpan.FromDays(7), now.AddHours(79))], "plus", "1240 credits · 1 reset"),
            new("Claude",
            [
                new("5h", 42, TimeSpan.FromHours(5), now.AddMinutes(130)),
                new("7d", 92, TimeSpan.FromDays(7), now.AddHours(52)),
            ], "max"),
        ];
    }

    static ProviderStatus DemoSystem() => new(L.T("System", "Systeem"),
    [
        new("ram", 71, TimeSpan.Zero, null, "23/32G"),
        new("commit", 58, TimeSpan.Zero, null, "37/64G"),
    ], Note: L.T("click for processes", "klik voor processen"));

    static readonly Color DarkTaskbar = Color.FromArgb(0x20, 0x20, 0x20);
    static readonly Color LightTaskbar = Color.FromArgb(0xF3, 0xF3, 0xF3);

    // Week window whose reset is placed so that even pace sits at the given percentage.
    static UsageWindow Week(double used, double even) =>
        new("7d", used, TimeSpan.FromDays(7), DateTimeOffset.Now + TimeSpan.FromDays(7) * (1 - even / 100));

    // Green, orange and red situations, with both providers present.
    static readonly IReadOnlyList<ProviderStatus>[] IconStates =
    [
        [new("Codex", [Week(42, 57)]), new("Claude", [Week(30, 50)])],
        [new("Codex", [Week(64, 53)]), new("Claude", [Week(20, 40)])],
        [new("Codex", [Week(50, 60)]), new("Claude", [Week(92, 69)])],
    ];

    // RAM for the bars icon, one per state.
    static readonly UsageWindow[] IconRam = [new("ram", 45, TimeSpan.Zero, null), new("ram", 71, TimeSpan.Zero, null), new("ram", 93, TimeSpan.Zero, null)];

    /// <summary>One strip per icon style, showing the three states on a dark taskbar.</summary>
    static Bitmap IconStrip(IconStyle style, int size)
    {
        int pad = size / 2, gap = size / 2;
        var strip = new Bitmap(pad * 2 + IconStates.Length * size + (IconStates.Length - 1) * gap, size + pad * 2);
        using var g = Graphics.FromImage(strip);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var bg = Theme.RoundRect(new RectangleF(0, 0, strip.Width, strip.Height), size * 0.3f))
        using (var brush = new SolidBrush(DarkTaskbar))
            g.FillPath(brush, bg);
        for (var i = 0; i < IconStates.Length; i++)
        {
            using var icon = IconRenderer.Draw(style, IconStates[i], size, light: false, IconRam[i]);
            g.DrawImage(icon, pad + i * (size + gap), pad);
        }
        return strip;
    }

    /// <summary>
    /// Every style and state at real tray sizes on dark and light taskbars, magnified 4×
    /// (`UsageTray --preview-icons out.png`), for checking legibility.
    /// </summary>
    public static void PreviewIcons(string path)
    {
        int[] sizes = [16, 20, 24, 32];
        const int Zoom = 4, Cell = 32 * Zoom + 16;
        var styles = Enum.GetValues<IconStyle>();
        using var sheet = new Bitmap(styles.Length * IconStates.Length * Cell, sizes.Length * 2 * Cell);
        using var g = Graphics.FromImage(sheet);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        var row = 0;
        foreach (var size in sizes)
            foreach (var light in new[] { false, true })
            {
                using (var bg = new SolidBrush(light ? LightTaskbar : DarkTaskbar))
                    g.FillRectangle(bg, 0, row * Cell, sheet.Width, Cell);
                var col = 0;
                foreach (var style in styles)
                    for (var i = 0; i < IconStates.Length; i++)
                    {
                        using var icon = IconRenderer.Draw(style, IconStates[i], size, light, IconRam[i]);
                        g.DrawImage(icon, col * Cell + 8, row * Cell + 8, size * Zoom, size * Zoom);
                        col++;
                    }
                row++;
            }
        sheet.Save(path, ImageFormat.Png);
    }

    // PNG-compressed ICO (supported since Vista).
    static void WriteIco(string path, int[] sizes)
    {
        var images = sizes.Select(s =>
        {
            using var bmp = IconRenderer.Logo(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();

        using var w = new BinaryWriter(File.Create(path));
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(dim);
            w.Write(dim);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) w.Write(image);
    }
}
