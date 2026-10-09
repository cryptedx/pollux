namespace Pollux.Core.Clipboard;

/// <summary>Access to HKCU\Software\Microsoft\Clipboard\EnableClipboardHistory; null means the value is absent.</summary>
public interface IWinVRegistry
{
    int? Read();

    /// <summary>Writes the DWORD, or removes the value when null.</summary>
    void Write(int? value);
}

/// <summary>
/// Win+V takeover: turns off Windows' own Clipboard History (so Win+V is free for Pollux) and remembers the previous value
/// in a state file, so it can be restored on turn-off, on quit, and after a crash.
/// </summary>
public sealed class WinVTakeover(IWinVRegistry registry, string stateFile)
{
    private const string Absent = "absent";

    public bool IsEngaged => File.Exists(stateFile);

    /// <summary>Saves the previous value (only the first time) and sets EnableClipboardHistory to 0.</summary>
    public void Engage()
    {
        if (!IsEngaged)
        {
            var previous = registry.Read();
            var dir = Path.GetDirectoryName(stateFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(stateFile, previous?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Absent);
        }

        registry.Write(0);
    }

    /// <summary>
    /// Puts the saved value back (or removes the value) and forgets it. Does nothing when not engaged.
    /// A state file that is neither a number nor the exact absent marker throws <see cref="InvalidDataException"/>
    /// and is kept, with the registry untouched, so the value is never removed on a guess.
    /// </summary>
    public void Restore()
    {
        if (!IsEngaged) return;
        var text = File.ReadAllText(stateFile).Trim();
        if (text == Absent)
        {
            registry.Write(null);
        }
        else if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            registry.Write(v);
        }
        else
        {
            throw new InvalidDataException($"Unrecognized Win+V restore state in {stateFile}; kept for recovery.");
        }

        File.Delete(stateFile);
    }
}
