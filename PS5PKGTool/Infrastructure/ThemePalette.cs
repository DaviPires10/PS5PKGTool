using Avalonia.Media;

namespace PS5PKGTool.Infrastructure;

/// <summary>
/// One colour palette. The property names match the DarkUI theme tokens so the mapping between the
/// WinForms theme and the Avalonia one stays obvious.
/// </summary>
public sealed record ThemePalette(
    string Name,
    Color GreyBackground,
    Color HeaderBackground,
    Color MediumBackground,
    Color LightBackground,
    Color LighterBackground,
    Color LightestBackground,
    Color DarkBackground,
    Color BlueBackground,
    Color DarkBlueBackground,
    Color LightBorder,
    Color DarkBorder,
    Color DarkBlueBorder,
    Color LightBlueBorder,
    Color LightText,
    Color DisabledText,
    Color BlueHighlight,
    Color BlueSelection,
    Color GreyHighlight,
    Color GreySelection,
    Color DarkGreySelection,
    Color ActiveControl,
    Color MenuItemToggledOnFill,
    Color MenuItemToggledOnBorder)
{
    /// <summary>
    /// The foreground used on top of <see cref="BlueSelection"/>. DarkUI picks whichever of black or
    /// white has the stronger contrast, so the same rule is applied here.
    /// </summary>
    public Color SelectionText =>
        ContrastRatio(Colors.Black, BlueSelection) >= ContrastRatio(Colors.White, BlueSelection)
            ? Colors.Black
            : Colors.White;

    private static double ContrastRatio(Color first, Color second)
    {
        double a = RelativeLuminance(first);
        double b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045d ? value / 12.92d : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }

        return 0.2126d * Linearize(color.R) + 0.7152d * Linearize(color.G) + 0.0722d * Linearize(color.B);
    }
}
