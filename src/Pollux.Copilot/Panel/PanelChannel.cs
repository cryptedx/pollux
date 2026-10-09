namespace Pollux.Copilot.Panel;

/// <summary>
/// Pollux's end of the Copilot Panel bridge: messages sent before the page reported "ready" are held and delivered in order
/// once it does, and incoming messages are parsed into <see cref="PanelEvent"/>s.
/// </summary>
public sealed class PanelChannel(Action<string> post)
{
    private readonly List<string> _pending = [];
    private readonly Lock _gate = new();
    private bool _ready;

    public bool IsReady { get { lock (_gate) return _ready; } }

    /// <summary>The page was reloaded or recreated: messages wait for its next "ready".</summary>
    public void Reset()
    {
        lock (_gate) _ready = false;
    }

    public void Send(string message)
    {
        lock (_gate)
        {
            if (!_ready) { _pending.Add(message); return; }
        }

        post(message);
    }

    /// <summary>Handles a raw message from the page; returns the parsed event (null when unknown).</summary>
    public PanelEvent? Receive(string? json)
    {
        var e = PanelMessages.Parse(json);
        if (e is PanelEvent.Ready) Flush();
        return e;
    }

    private void Flush()
    {
        string[] batch;
        lock (_gate)
        {
            _ready = true;
            batch = [.. _pending];
            _pending.Clear();
        }

        foreach (var m in batch) post(m);
    }
}
