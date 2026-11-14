using Avalonia.Controls;

namespace PS5PKGTool.Controls;

/// <summary>
/// Search field used across the library, tasks, files and executable views. <see cref="SearchText"/>
/// mirrors <see cref="TextBox.Text"/> so the ported filtering code can stay close to the WinForms one.
/// </summary>
public class SearchBox : TextBox
{
    public SearchBox()
    {
        Classes.Add("search");
        PlaceholderText = "Search";
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    /// <summary>The raw query. Never null, so callers can use it without a null check.</summary>
    public string SearchText
    {
        get => Text ?? string.Empty;
        set => Text = value;
    }
}
