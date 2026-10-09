using Microsoft.Win32;
using Pollux.Core.Clipboard;

namespace Pollux.App.Services;

/// <summary>Windows' own clipboard history switch, HKCU\Software\Microsoft\Clipboard\EnableClipboardHistory (no admin needed).</summary>
internal sealed class WinVRegistry : IWinVRegistry
{
    private const string KeyPath = @"Software\Microsoft\Clipboard";
    private const string Name = "EnableClipboardHistory";

    public int? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(Name) is int v ? v : null;
    }

    public void Write(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (value is { } v) key.SetValue(Name, v, RegistryValueKind.DWord);
        else if (key.GetValue(Name) is not null) key.DeleteValue(Name);
    }
}
