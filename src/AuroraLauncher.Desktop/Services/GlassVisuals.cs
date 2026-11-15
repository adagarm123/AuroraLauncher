using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AuroraLauncher.Desktop.Services;

/// <summary>
/// Round-80: the visual half of the "faux glass" mode — translucent surface
/// brushes, a tiled noise texture and a hairline highlight. Everything here is
/// a plain fill (no Effect, no animation, no bitmap cache), so it costs the same
/// as the solid theme it replaces: WPF paints one more translucent rectangle.
///
/// Brushes are frozen and created once; the theme sweep only swaps six keys, and
/// <see cref="Restore"/> puts the originals back without pinning them into the
/// app dictionary (a pinned copy would survive theme switching).
/// </summary>
public static class GlassVisuals
{
    private static readonly string[] SurfaceKeys =
    {
        "PanelBrush", "PanelAltBrush", "BgDeepBrush", "InputBgBrush", "BorderBrush",
        // Round-81: the fig-1 look needs the TEXT to go light as well, plus a
        // translucent chip / nav highlight. Without these the dark glass panels
        // still carried dark text (that was the "ugly" part of the first attempt).
        "InputBorderBrush", "TextBrush", "TextMutedBrush", "ChipBrush", "NavActiveBrush",
        "ListHoverBrush", "HairlineBrush", "OutlineBorderBrush", "ScrollLaneBrush"
    };

    /// <summary>The override dictionary lives in MergedDictionaries (last one
    /// wins) so removing it restores the theme EXACTLY — writing keys into
    /// <c>Application.Resources</c> directly would pin the old theme's colour
    /// there forever and break theme switching.</summary>
    private static ResourceDictionary? _overrides;

    public static bool IsApplied =>
        _overrides != null && Application.Current?.Resources.MergedDictionaries.Contains(_overrides) == true;

    /// <summary>64×64 grain, tiled. Generated once at first use (≈16 KB) —
    /// shipping a PNG asset for this would be worse than 20 lines of code.</summary>
    private static Brush? _noise;
    public static Brush Noise => _noise ??= CreateNoiseBrush();

    public static Brush BuildWindowGradient(bool dark)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        if (dark)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x12, 0x13, 0x1B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x1C, 0x1F, 0x33), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x11, 0x12, 0x1A), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0xED, 0xFB), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF7, 0xF1, 0xFF), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xEA, 0xF3, 0xFF), 1));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Round-81: the "light and shadow" backdrop behind the glass. Built from
    /// vector gradients (no photo asset, no effect): a dark base with four soft
    /// aurora glows. Frozen, so panning/scrolling never re-rasterises it — a
    /// single GeometryDrawing group is cheaper than a JPEG of the same size.
    /// </summary>
    public static Brush BuildWallpaperBrush(bool dark)
    {
        var group = new DrawingGroup();
        var area = new Rect(0, 0, 1000, 700);

        var baseBrush = new LinearGradientBrush { StartPoint = new Point(0.05, 0), EndPoint = new Point(0.95, 1) };
        if (dark)
        {
            baseBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0A, 0x0D, 0x18), 0));
            baseBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0x15, 0x28), 0.45));
            baseBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x09, 0x0B, 0x12), 1));
        }
        else
        {
            baseBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE7, 0xEC, 0xF9), 0));
            baseBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xD9, 0xE6, 0xF7), 0.45));
            baseBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xEF, 0xE9, 0xF8), 1));
        }
        group.Children.Add(new GeometryDrawing(baseBrush, null, new RectangleGeometry(area)));

        void Glow(double cx, double cy, double rx, double ry, Color color, double alpha)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B), 0));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0, color.R, color.G, color.B), 1));
            group.Children.Add(new GeometryDrawing(brush, null,
                new EllipseGeometry(new Point(cx, cy), rx, ry)));
        }

        if (dark)
        {
            Glow(150, 90, 560, 420, Color.FromRgb(0x4A, 0x38, 0xD8), 0.46);   // violet, upper left
            Glow(900, 90, 460, 320, Color.FromRgb(0x14, 0x6E, 0xB4), 0.30);   // blue, upper right
            Glow(760, 660, 560, 380, Color.FromRgb(0x18, 0x8F, 0x66), 0.24);  // aurora green
            Glow(90, 640, 420, 300, Color.FromRgb(0x77, 0x2F, 0xA0), 0.20);   // magenta
            Glow(560, 470, 380, 260, Color.FromRgb(0xB4, 0x6B, 0x28), 0.12);  // faint ember (fig-1 warmth)
            Glow(500, 720, 520, 300, Color.FromRgb(0x25, 0x36, 0x8C), 0.22);  // ambient floor glow
        }
        else
        {
            Glow(180, 120, 520, 380, Color.FromRgb(0x8F, 0x86, 0xF0), 0.30);
            Glow(880, 140, 420, 300, Color.FromRgb(0x8F, 0xCF, 0xF2), 0.26);
            Glow(700, 640, 520, 360, Color.FromRgb(0x9F, 0xE2, 0xC8), 0.22);
        }

        var brush2 = new DrawingBrush(group)
        {
            Stretch = Stretch.UniformToFill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = area,
            TileMode = TileMode.None
        };
        brush2.Freeze();
        return brush2;
    }

    /// <summary>Swaps the surface brushes for translucent variants. Called for
    /// Faux / System / Local — in every glass mode the surfaces must let the
    /// backdrop show through, otherwise nothing is visible.</summary>
    public static void ApplyTranslucentSurfaces(bool dark)
    {
        var app = Application.Current;
        if (app == null) return;
        Restore();
        var map = dark
            ? new Dictionary<string, Color>(StringComparer.Ordinal)
            {
                // Round-82: 30% dark-blue glass instead of 40% black — the card
                // now picks up the wallpaper tint behind it ("light" rather than
                // "a slab of iron").
                ["PanelBrush"] = Color.FromArgb(0x4D, 0x0F, 0x17, 0x2A),
                ["PanelAltBrush"] = Color.FromArgb(0x3D, 0x12, 0x1A, 0x2C),
                ["BgDeepBrush"] = Color.FromArgb(0x26, 0x00, 0x00, 0x00),
                ["InputBgBrush"] = Color.FromArgb(0x4D, 0x0A, 0x0C, 0x12),
                ["BorderBrush"] = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
                ["InputBorderBrush"] = Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF),
                ["TextBrush"] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                ["TextMutedBrush"] = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF),
                ["ChipBrush"] = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
                ["NavActiveBrush"] = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
                ["ListHoverBrush"] = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
                ["HairlineBrush"] = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
                ["OutlineBorderBrush"] = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF),
                ["ScrollLaneBrush"] = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)
            }
            : new Dictionary<string, Color>(StringComparer.Ordinal)
            {
                // Light theme keeps light panels; the wallpaper is dimmed so the
                // dark text still reads.
                ["PanelBrush"] = Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF),
                ["PanelAltBrush"] = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF),
                ["BgDeepBrush"] = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF),
                ["InputBgBrush"] = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF),
                ["BorderBrush"] = Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF),
                ["InputBorderBrush"] = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
                ["TextBrush"] = Color.FromArgb(0xFF, 0x16, 0x19, 0x22),
                ["TextMutedBrush"] = Color.FromArgb(0xB3, 0x16, 0x19, 0x22),
                ["ChipBrush"] = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
                ["NavActiveBrush"] = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
                ["ListHoverBrush"] = Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
                ["HairlineBrush"] = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
                ["OutlineBorderBrush"] = Color.FromArgb(0x73, 0x16, 0x19, 0x22),
                ["ScrollLaneBrush"] = Color.FromArgb(0x33, 0x00, 0x00, 0x00)
            };

        foreach (var key in SurfaceKeys)
        {
            if (!map.TryGetValue(key, out var color)) continue;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Overrides[key] = brush;
        }
        if (app.Resources.MergedDictionaries.Contains(Overrides))
            app.Resources.MergedDictionaries.Remove(Overrides);
        app.Resources.MergedDictionaries.Add(Overrides); // last wins
    }

    /// <summary>Removes every override so the theme dictionary wins again.</summary>
    public static void Restore()
    {
        var app = Application.Current;
        if (app == null) return;
        try { app.Resources.MergedDictionaries.Remove(Overrides); } catch { }
        Overrides.Clear();
    }

    private static ResourceDictionary Overrides => _overrides ??= new ResourceDictionary();

    /// <summary>Random-but-fixed grain: alpha 0–9 on 25% of pixels, rest fully
    /// transparent. Too much alpha turns into visible dirt; this reads as a
    /// subtle frost at 100% zoom.</summary>
    private static Brush CreateNoiseBrush()
    {
        const int size = 64;
        var stride = size * 4;
        var pixels = new byte[stride * size];
        var random = new Random(20261112); // deterministic: same look on every machine
        for (var i = 0; i < size * size; i++)
        {
            if (random.Next(100) >= 30) continue;
            var offset = i * 4;
            var alpha = (byte)random.Next(0, 15);
            pixels[offset] = 255;      // B
            pixels[offset + 1] = 255;  // G
            pixels[offset + 2] = 255;  // R
            pixels[offset + 3] = alpha;
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }
}
