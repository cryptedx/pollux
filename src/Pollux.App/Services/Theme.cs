using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Pollux.Core.Settings;
using Windows.UI;

namespace Pollux.App.Services;

/// <summary>
/// Pollux colors for code-built UI, defined in code (no resource-dictionary casts, which are fragile under Native AOT).
/// Mirrors the theme dictionaries in App.xaml.
/// </summary>
internal static class Theme
{
    private static readonly Dictionary<string, Brush> Cache = [];
    private static Color? _accentOverride;

    public static ElementTheme Current { get; private set; } = ElementTheme.Light;

    public static ElementTheme Resolve(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => IsSystemDark() ? ElementTheme.Dark : ElementTheme.Light,
    };

    public static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    public static void Apply(ThemeMode mode, string? accentHex)
    {
        Current = Resolve(mode);
        _accentOverride = TryParseColor(accentHex, out var c) ? c : null;
        Cache.Clear();
    }

    public static Color Accent
    {
        get
        {
            if (_accentOverride is { } o) return o;
            var sys = SystemAccent();
            return Current == ElementTheme.Dark ? Lighten(sys, 0.45) : sys;
        }
    }

    public static Brush Brush(string key)
    {
        if (Cache.TryGetValue(key, out var b)) return b;
        var dark = Current == ElementTheme.Dark;
        Color c = key switch
        {
            "PolluxSurfaceBrush" => dark ? Argb(0xCC, 0x27, 0x27, 0x27) : Argb(0xCC, 0xFF, 0xFF, 0xFF),
            "PolluxSurface2Brush" => dark ? Rgb(0x2C, 0x2C, 0x2C) : Rgb(0xF7, 0xF7, 0xF7),
            "PolluxChromeBrush" => Colors.Transparent,
            "PolluxLineBrush" => dark ? Rgb(0x3A, 0x3A, 0x3A) : Rgb(0xE3, 0xE3, 0xE3),
            "PolluxTextBrush" => dark ? Rgb(0xE9, 0xE9, 0xE9) : Rgb(0x1B, 0x1B, 0x1B),
            "PolluxMutedBrush" => dark ? Rgb(0x9A, 0x9A, 0x9A) : Rgb(0x6B, 0x6B, 0x6B),
            "PolluxAccentBrush" => Accent,
            "PolluxAccentSoftBrush" => WithAlpha(Accent, (byte)(dark ? 0x24 : 0x1F)),
            "PolluxHoverBrush" => dark ? Argb(0x0D, 0xFF, 0xFF, 0xFF) : Argb(0x0A, 0, 0, 0),
            "PolluxFolderBrush" => dark ? Rgb(0xE8, 0xB8, 0x4A) : Rgb(0xD9, 0xA2, 0x2C),
            "PolluxBadBrush" => dark ? Rgb(0xFF, 0x7B, 0x72) : Rgb(0xC4, 0x2B, 0x1C),
            "PolluxGoodBrush" => dark ? Rgb(0x6C, 0xCB, 0x8F) : Rgb(0x2E, 0x8B, 0x57),
            "PolluxWarnBrush" => dark ? Rgb(0xF0, 0xB9, 0x4A) : Rgb(0xC2, 0x7C, 0x0E),
            "PolluxOverlayBrush" => dark ? Argb(0x55, 0, 0, 0) : Argb(0x33, 0, 0, 0),
            _ => Colors.Magenta,
        };
        b = new SolidColorBrush(c);
        Cache[key] = b;
        return b;
    }

    /// <summary>Overrides the XAML theme dictionaries' accent brushes with the effective accent (custom or system).</summary>
    public static void PushAccentToResources()
    {
        try
        {
            foreach (var name in new[] { "Light", "Dark" })
            {
                if (Application.Current.Resources.ThemeDictionaries.TryGetValue(name, out var o) && o is ResourceDictionary dict)
                {
                    var accent = _accentOverride ?? (name == "Dark" ? Lighten(SystemAccent(), 0.45) : SystemAccent());
                    dict["PolluxAccentBrush"] = new SolidColorBrush(accent);
                    dict["PolluxAccentSoftBrush"] = new SolidColorBrush(WithAlpha(accent, 0x22));
                }
            }
        }
        catch (InvalidCastException)
        {
            // Keep XAML defaults.
        }
    }

    private static Color SystemAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is int abgr)
        {
            var v = unchecked((uint)abgr);
            return Color.FromArgb(0xFF, (byte)v, (byte)(v >> 8), (byte)(v >> 16));
        }

        return Rgb(0x0F, 0x6C, 0xBD);
    }

    private static Color Lighten(Color c, double amount) => Color.FromArgb(c.A,
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    public static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 6) h = "FF" + h;
        if (h.Length != 8 || !uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}

/// <summary>Typed access to App.xaml resources that tolerates AOT projection quirks.</summary>
internal static class Ui
{
    private static readonly Dictionary<string, object?> Cache = [];

    public static Style? Style(string key) => Get(key) as Style;

    private static string? _monoName;
    private static FontFamily? _mono;

    /// <summary>The monospace font from settings (appearance.monoFont), with Cascadia/Consolas fallbacks.</summary>
    public static FontFamily Mono
    {
        get
        {
            var name = App.Instance.Services?.Settings.Appearance.MonoFont is { Length: > 0 } n ? n : "Cascadia Mono";
            if (_mono is null || name != _monoName)
            {
                _monoName = name;
                _mono = new FontFamily($"{name}, Cascadia Mono, Consolas");
            }

            return _mono;
        }
    }

    private static Style? _flat;

    /// <summary>Borderless button style for code-built UI (XAML uses the PolluxFlatButton resource).</summary>
    public static Style FlatButton => _flat ??= CreateFlatButton();

    private static Style CreateFlatButton()
    {
        var s = new Style(typeof(Microsoft.UI.Xaml.Controls.Button));
        s.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
        s.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.Control.BorderThicknessProperty, new Thickness(0)));
        s.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        s.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        s.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        s.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.Control.CornerRadiusProperty, new CornerRadius(5)));
        return s;
    }

    private static object? Get(string key)
    {
        if (Cache.TryGetValue(key, out var v)) return v;
        try
        {
            Application.Current.Resources.TryGetValue(key, out v);
        }
        catch (InvalidCastException ex)
        {
            CrashLog.Write(new InvalidOperationException("Resource " + key, ex));
            v = null;
        }

        if (v is not null and not Microsoft.UI.Xaml.Style and not FontFamily)
        {
            CrashLog.Write(new InvalidOperationException($"Resource {key} has unexpected type {v.GetType().FullName}"));
        }

        Cache[key] = v;
        return v;
    }
}
