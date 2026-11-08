using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace PS5PKGTool.Controls;

/// <summary>
/// A bordered container with a caption bar. Matches the DarkUI section panel the WinForms build used
/// for grouped settings and detail panes.
/// </summary>
public class SectionPanel : ContentControl
{
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<SectionPanel, string>(nameof(Header), string.Empty);

    public static readonly StyledProperty<string> HeaderNoteProperty =
        AvaloniaProperty.Register<SectionPanel, string>(nameof(HeaderNote), string.Empty);

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string HeaderNote
    {
        get => GetValue(HeaderNoteProperty);
        set => SetValue(HeaderNoteProperty, value);
    }
}
