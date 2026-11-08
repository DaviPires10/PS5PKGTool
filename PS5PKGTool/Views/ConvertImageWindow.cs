using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PS5PKGTool.Ffpfsc;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Views;

/// <summary>The target, path and overwrite choice made in the image conversion dialog.</summary>
internal sealed record ConvertImageResult(Ps5ImageConversionTarget Target, string OutputPath, bool Overwrite);

/// <summary>Image conversion dialog: source, target format, output path and overwrite option.</summary>
internal sealed class ConvertImageWindow : Window
{
    private static readonly (Ps5ImageConversionTarget Target, string Name, string Extension, string Filter)[] Targets =
    [
        (Ps5ImageConversionTarget.Exfat, "exFAT image", ".exfat", "exFAT image"),
        (Ps5ImageConversionTarget.Ffpkg, "FFPKG image", ".ffpkg", "FFPKG image"),
        (Ps5ImageConversionTarget.Ffpfsc, "FFPFSC image", ".ffpfsc", "FFPFSC image")
    ];

    private readonly string _sourcePath;
    private readonly ComboBox _cboTarget = new() { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _txtOutput = new();
    private readonly CheckBox _chkOverwrite = new() { Content = "Overwrite the output if it exists" };
    private readonly List<Ps5ImageConversionTarget> _allowed;

    private ConvertImageWindow(string sourcePath, IReadOnlyList<Ps5ImageConversionTarget> targets,
        Ps5ImageConversionTarget? preferred)
    {
        _sourcePath = sourcePath;
        _allowed = [.. targets];
        Title = "Convert Image";
        Width = 700;
        Height = 240;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(ThemeManager.Active.GreyBackground);

        foreach (var target in Targets.Where(entry => targets.Contains(entry.Target)))
            _cboTarget.Items.Add(target.Name);
        if (_cboTarget.ItemCount > 0)
        {
            int index = 0;
            if (preferred is { } preferredTarget)
            {
                int match = Targets.ToList().FindIndex(entry =>
                    entry.Target == preferredTarget && targets.Contains(entry.Target));
                if (match >= 0) index = match;
            }
            _cboTarget.SelectedIndex = Math.Min(index, _cboTarget.ItemCount - 1);
        }
        _cboTarget.SelectionChanged += (_, _) => UpdateForTarget();

        var browse = new Button { Content = "Browse...", Width = 84 };
        browse.Click += async (_, _) => await BrowseAsync();

        var convert = new Button { Content = "Convert", Width = 110 };
        convert.Click += (_, _) => Confirm();
        var cancel = new Button { Content = "Cancel", Width = 94 };
        cancel.Click += (_, _) => Close(null);

        var sourceLabel = new TextBlock
        {
            Text = "Source: " + sourcePath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var form = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("130,*,Auto"),
            Margin = new Thickness(16)
        };
        Grid.SetRow(sourceLabel, 0);
        Grid.SetColumnSpan(sourceLabel, 3);
        form.Children.Add(sourceLabel);

        var targetLabel = new TextBlock { Text = "Convert to", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(targetLabel, 1);
        form.Children.Add(targetLabel);
        Grid.SetRow(_cboTarget, 1);
        Grid.SetColumn(_cboTarget, 1);
        form.Children.Add(_cboTarget);

        var outputLabel = new TextBlock
        {
            Text = "Output file",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(outputLabel, 2);
        form.Children.Add(outputLabel);
        Grid.SetRow(_txtOutput, 2);
        Grid.SetColumn(_txtOutput, 1);
        _txtOutput.Margin = new Thickness(0, 8, 8, 0);
        form.Children.Add(_txtOutput);
        Grid.SetRow(browse, 2);
        Grid.SetColumn(browse, 2);
        browse.Margin = new Thickness(0, 8, 0, 0);
        form.Children.Add(browse);

        Grid.SetRow(_chkOverwrite, 3);
        Grid.SetColumn(_chkOverwrite, 1);
        _chkOverwrite.Margin = new Thickness(0, 10, 0, 0);
        form.Children.Add(_chkOverwrite);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, convert }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(form);
        var footer = new Border { Child = buttons, Margin = new Thickness(16, 0, 16, 16) };
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;

        UpdateForTarget();
    }

    public static Task<ConvertImageResult?> ShowAsync(Window owner, string sourcePath,
        IReadOnlyList<Ps5ImageConversionTarget> targets, Ps5ImageConversionTarget? preferred = null)
    {
        var dialog = new ConvertImageWindow(sourcePath, targets, preferred);
        return dialog.ShowDialog<ConvertImageResult?>(owner);
    }

    private (Ps5ImageConversionTarget Target, string Name, string Extension, string Filter)? Current
    {
        get
        {
            if (_cboTarget.SelectedItem is not string selected) return null;
            foreach (var entry in Targets)
                if (entry.Name == selected) return entry;
            return null;
        }
    }

    private void UpdateForTarget()
    {
        if (Current is not { } info) return;
        _txtOutput.Text = string.IsNullOrWhiteSpace(_txtOutput.Text)
            ? DefaultOutput(info.Extension)
            : Path.ChangeExtension(_txtOutput.Text, info.Extension);
    }

    private string DefaultOutput(string extension)
    {
        bool directory = Directory.Exists(_sourcePath);
        string name = directory
            ? new DirectoryInfo(_sourcePath).Name
            : Path.GetFileNameWithoutExtension(_sourcePath);
        string? parent = directory
            ? Directory.GetParent(_sourcePath)?.FullName
            : Path.GetDirectoryName(_sourcePath);
        return Path.Combine(parent ?? _sourcePath, name + extension);
    }

    private async Task BrowseAsync()
    {
        if (Current is not { } info) return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Convert image",
            SuggestedFileName = Path.GetFileName(_txtOutput.Text ?? string.Empty),
            FileTypeChoices =
            [
                new FilePickerFileType(info.Filter) { Patterns = ["*" + info.Extension] },
                FilePickerFileTypes.All
            ],
            ShowOverwritePrompt = true
        });
        if (file?.TryGetLocalPath() is { } path) _txtOutput.Text = path;
    }

    private void Confirm()
    {
        string output = _txtOutput.Text?.Trim() ?? string.Empty;
        if (output.Length == 0)
        {
            AppDialog.ShowWarning("Select an output file.", "Convert image");
            return;
        }
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_sourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            AppDialog.ShowWarning("The output must differ from the source.", "Convert image");
            return;
        }
        if (Current is not { } info) return;
        Close(new ConvertImageResult(info.Target, output, _chkOverwrite.IsChecked == true));
    }
}
