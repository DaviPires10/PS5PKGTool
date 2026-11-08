using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
namespace PS5PKGTool.Infrastructure;

/// <summary>
/// Applies a <see cref="ThemePalette"/> to the live Avalonia resource dictionary. The views bind to
/// the resource keys with <c>DynamicResource</c>, so swapping the values restyles the whole
/// application without recreating any window.
/// </summary>
public static class ThemeManager
{
    /// <summary>Raised after the palette changed, so open windows can refresh derived values.</summary>
    public static event EventHandler? ThemeChanged;

    private static ThemePalette _active = ThemeCatalog.Presets[0];

    /// <summary>The palette currently in use.</summary>
    public static ThemePalette Active => _active;

    /// <summary>All selectable palettes.</summary>
    public static IReadOnlyList<ThemePalette> Presets => ThemeCatalog.Presets;

    /// <summary>Applies the palette by name; unknown names fall back to the default one.</summary>
    public static void Apply(string? name) => Apply(ThemeCatalog.Find(name));

    /// <summary>Loads an embedded resource bitmap ("avares://PS5PKGTool/Resources/...") or null.</summary>
    public static Bitmap? LoadBitmap(string uri)
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Applies the given palette to the application resources.</summary>
    public static void Apply(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _active = palette;

        var resources = Application.Current?.Resources
            ?? throw new InvalidOperationException("The Avalonia application is not initialised yet.");

        Set(resources, "Theme.GreyBackground", palette.GreyBackground);
        Set(resources, "Theme.HeaderBackground", palette.HeaderBackground);
        Set(resources, "Theme.MediumBackground", palette.MediumBackground);
        Set(resources, "Theme.LightBackground", palette.LightBackground);
        Set(resources, "Theme.LighterBackground", palette.LighterBackground);
        Set(resources, "Theme.LightestBackground", palette.LightestBackground);
        Set(resources, "Theme.DarkBackground", palette.DarkBackground);
        Set(resources, "Theme.BlueBackground", palette.BlueBackground);
        Set(resources, "Theme.DarkBlueBackground", palette.DarkBlueBackground);
        Set(resources, "Theme.LightBorder", palette.LightBorder);
        Set(resources, "Theme.DarkBorder", palette.DarkBorder);
        Set(resources, "Theme.DarkBlueBorder", palette.DarkBlueBorder);
        Set(resources, "Theme.LightBlueBorder", palette.LightBlueBorder);
        Set(resources, "Theme.LightText", palette.LightText);
        Set(resources, "Theme.DisabledText", palette.DisabledText);
        Set(resources, "Theme.BlueHighlight", palette.BlueHighlight);
        Set(resources, "Theme.BlueSelection", palette.BlueSelection);
        Set(resources, "Theme.GreyHighlight", palette.GreyHighlight);
        Set(resources, "Theme.GreySelection", palette.GreySelection);
        Set(resources, "Theme.DarkGreySelection", palette.DarkGreySelection);
        Set(resources, "Theme.ActiveControl", palette.ActiveControl);
        Set(resources, "Theme.MenuItemToggledOnFill", palette.MenuItemToggledOnFill);
        Set(resources, "Theme.MenuItemToggledOnBorder", palette.MenuItemToggledOnBorder);
        Set(resources, "Theme.SelectionText", palette.SelectionText);

        // Mirror the palette into the Fluent theme so the built-in controls (buttons, text boxes,
        // scroll bars, the tab strip) follow the same colours as the custom ones.
        resources["SystemAccentColor"] = palette.BlueSelection;
        resources["SystemAccentColorLight1"] = Lighten(palette.BlueSelection, 0.2);
        resources["SystemAccentColorLight2"] = Lighten(palette.BlueSelection, 0.4);
        resources["SystemAccentColorLight3"] = Lighten(palette.BlueSelection, 0.6);
        resources["SystemAccentColorDark1"] = Darken(palette.BlueSelection, 0.2);
        resources["SystemAccentColorDark2"] = Darken(palette.BlueSelection, 0.4);
        resources["SystemAccentColorDark3"] = Darken(palette.BlueSelection, 0.6);
        resources["AccentBaseColor"] = palette.BlueSelection;

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void Set(IResourceDictionary resources, string key, Color value)
    {
        // Background/Foreground/BorderBrush expect an IBrush, so publish a brush under the key and
        // keep the raw colour under a ".Color" suffix for code that needs to derive from it.
        var brush = new SolidColorBrush(value);
        resources[key] = brush;
        resources[key + ".Color"] = value;
        // Also register the short alias so the XAML can use the shorter names.
        if (key.StartsWith("Theme.", StringComparison.Ordinal))
        {
            resources[key[6..]] = brush;
            resources[key[6..] + ".Color"] = value;
        }
    }

    private static Color Lighten(Color color, double amount) => Shift(color, amount);

    private static Color Darken(Color color, double amount) => Shift(color, -amount);

    private static Color Shift(Color color, double amount)
    {
        static byte Channel(byte value, double delta) =>
            (byte)Math.Clamp(Math.Round(value + delta * 255d), 0, 255);

        return Color.FromRgb(
            Channel(color.R, amount),
            Channel(color.G, amount),
            Channel(color.B, amount));
    }
}
