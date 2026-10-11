using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

/// <summary>S-16: web messages are accepted only from the view's own page and parsed defensively.</summary>
[TestClass]
public sealed class WebMessageTests
{
    private const string Terminal = "https://pollux.assets/terminal.html";

    [TestMethod]
    public void MessageFromOwnPageIsRead()
    {
        var message = WebMessage.Read(Terminal, """{"type":"input","data":"dir\r"}""", WebViewKind.Terminal);

        Assert.IsNotNull(message);
        Assert.AreEqual("input", message.Type);
        Assert.AreEqual("dir\r", message.Text("data"));
    }

    [TestMethod]
    [DataRow("https://pollux.preview/evil.html", WebViewKind.Terminal)]
    [DataRow("https://pollux.assets/notes.html", WebViewKind.Terminal)]
    [DataRow("https://pollux.assets.evil.com/terminal.html", WebViewKind.Terminal)]
    [DataRow("https://evil.example/copilot.html", WebViewKind.CopilotPanel)]
    [DataRow("about:blank", WebViewKind.Notes)]
    [DataRow("", WebViewKind.CodePreview)]
    [DataRow(null, WebViewKind.Notes)]
    [DataRow("https://pollux.assets/blank.html", WebViewKind.FilePreview)]
    public void MessageFromForeignSourceIsIgnored(string? source, WebViewKind view)
    {
        // S-16: a framed or navigated foreign page posts a well-formed terminal input to reach the ConPTY.
        Assert.IsNull(WebMessage.Read(source, """{"type":"input","data":"calc.exe\r"}""", view));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("{\"type\":")]
    [DataRow("[1,2]")]
    [DataRow("\"ready\"")]
    [DataRow("{}")]
    [DataRow("{\"type\":42}")]
    [DataRow("{\"type\":null}")]
    public void MalformedMessageIsIgnored(string? json) =>
        Assert.IsNull(WebMessage.Read(Terminal, json, WebViewKind.Terminal));

    [TestMethod]
    public void MissingOrMistypedFieldsReadAsNull()
    {
        var message = WebMessage.Read(Terminal, """{"type":"changed","text":5,"n":"x"}""", WebViewKind.Terminal)!;

        Assert.IsNull(message.Text("text"));
        Assert.IsNull(message.Text("absent"));
        Assert.IsNull(message.TerminalSize("n"));
        Assert.IsNull(message.TerminalSize("absent"));
    }

    [TestMethod]
    [DataRow("80", (short)80)]
    [DataRow("1", (short)1)]
    [DataRow("1000", (short)1000)]
    [DataRow("0", (short)1)]
    [DataRow("-5", (short)1)]
    [DataRow("1001", (short)1000)]
    [DataRow("32768", (short)1000)]   // (short)32768 wrapped to -32768 before
    [DataRow("65616", (short)1000)]   // (short)65616 wrapped to 80 before
    [DataRow("1e300", (short)1000)]   // GetInt32 threw before
    [DataRow("80.6", (short)80)]
    public void TerminalSizesAreClamped(string value, short expected)
    {
        var message = WebMessage.Read(Terminal, $$"""{"type":"resize","cols":{{value}}}""", WebViewKind.Terminal)!;

        Assert.AreEqual(expected, message.TerminalSize("cols"));
    }
}
