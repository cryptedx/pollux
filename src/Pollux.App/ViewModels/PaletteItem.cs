namespace Pollux.App.ViewModels;

/// <summary>A row in the command palette / Quick Search.</summary>
public sealed partial class PaletteItem(string title, string detail, string keyText, Action run)
{
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string KeyText { get; } = keyText;
    public Action Run { get; } = run;
    public Microsoft.UI.Xaml.Visibility KeyVisibility => KeyText.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility DetailVisibility => Detail.Length > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}
