using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class ShellPreviewHandlersTests
{
    private const string Word = "{84f66100-ff7c-4fb4-b0c0-02cd7fb668fe}";
    private const string Font = "{8d1cfd0e-1a8f-4e15-8b31-0f2b33f4e0d4}";
    private const string ShellEx = @"\ShellEx\" + ShellPreviewHandlers.ShellExKey;

    private sealed class FakeRegistry : IAssociationRegistry
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Choices { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Reads { get; private set; }

        public string? ClassesRoot(string key, string? name = null)
        {
            Reads++;
            return Values.GetValueOrDefault(name is null ? key : key + "@" + name);
        }

        public bool ClassesRootHasKey(string key) => Keys.Contains(key);
        public string? UserChoice(string extension) => Choices.GetValueOrDefault(extension);

        /// <summary>Registers <paramref name="clsid"/> like a prevhost-hosted handler.</summary>
        public FakeRegistry Surrogate(string clsid)
        {
            Values[$@"CLSID\{clsid}@AppID"] = "{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}";
            return this;
        }
    }

    [TestMethod]
    public void FindsTheHandlerOfTheProgId()
    {
        var reg = new FakeRegistry().Surrogate(Word);
        reg.Values[".docx"] = "Word.Document.12";
        reg.Values["Word.Document.12" + ShellEx] = Word;

        Assert.AreEqual(Guid.Parse(Word), new ShellPreviewHandlers(reg).Find("docx"));
    }

    [TestMethod]
    public void FindsTheHandlerOnTheExtensionAndInSystemFileAssociations()
    {
        var reg = new FakeRegistry().Surrogate(Word).Surrogate(Font);
        reg.Values[".msg" + ShellEx] = Word;
        reg.Values[@"SystemFileAssociations\.ttf" + ShellEx] = Font;

        var handlers = new ShellPreviewHandlers(reg);
        Assert.AreEqual(Guid.Parse(Word), handlers.Find("MSG"));
        Assert.AreEqual(Guid.Parse(Font), handlers.Find("ttf"));
    }

    [TestMethod]
    public void FindsTheHandlerOfThePerceivedType()
    {
        var reg = new FakeRegistry().Surrogate(Font);
        reg.Values[".otf@PerceivedType"] = "font";
        reg.Values[@"SystemFileAssociations\font" + ShellEx] = Font;

        Assert.AreEqual(Guid.Parse(Font), new ShellPreviewHandlers(reg).Find("otf"));
    }

    [TestMethod]
    public void TheUsersChoiceComesFirstAndFallsThroughWhenItHasNoHandler()
    {
        var reg = new FakeRegistry().Surrogate(Word).Surrogate(Font);
        reg.Values[".docx"] = "Word.Document.12";
        reg.Values["Word.Document.12" + ShellEx] = Word;
        reg.Choices[".docx"] = "Other.Doc";
        reg.Values["Other.Doc" + ShellEx] = Font;
        Assert.AreEqual(Guid.Parse(Font), new ShellPreviewHandlers(reg).Find("docx"));

        reg.Choices[".docx"] = @"Applications\notepad.exe";
        Assert.AreEqual(Guid.Parse(Word), new ShellPreviewHandlers(reg).Find("docx"));
    }

    [TestMethod]
    public void OnlyOutOfProcessHandlersCount()
    {
        var reg = new FakeRegistry();
        reg.Values[".docx" + ShellEx] = Word;
        Assert.IsNull(new ShellPreviewHandlers(reg).Find("docx"), "in-process only (no AppID, no LocalServer32)");

        reg.Keys.Add($@"CLSID\{Word}\LocalServer32");
        Assert.AreEqual(Guid.Parse(Word), new ShellPreviewHandlers(reg).Find("docx"), "a local server");

        reg.Surrogate(Word);
        reg.Values[$@"CLSID\{Word}@DisableProcessIsolation"] = "1";
        Assert.IsNull(new ShellPreviewHandlers(reg).Find("docx"), "opted out of process isolation");
    }

    [TestMethod]
    public void TheMostSpecificRegistrationDecidesEvenWhenUnusable()
    {
        var reg = new FakeRegistry().Surrogate(Font);
        reg.Values[".docx"] = "Word.Document.12";
        reg.Values["Word.Document.12" + ShellEx] = Word; // in-process only
        reg.Values[@"SystemFileAssociations\.docx" + ShellEx] = Font;

        Assert.IsNull(new ShellPreviewHandlers(reg).Find("docx"));
    }

    [TestMethod]
    public void InvalidOrMissingRegistrationsGiveNull()
    {
        var reg = new FakeRegistry();
        reg.Values[".bad" + ShellEx] = "not a guid";
        reg.Values[".zero" + ShellEx] = Guid.Empty.ToString("B");
        var handlers = new ShellPreviewHandlers(reg);

        Assert.IsNull(handlers.Find("bad"));
        Assert.IsNull(handlers.Find("zero"));
        Assert.IsNull(handlers.Find("none"));
        Assert.IsNull(handlers.Find(""));
    }

    [TestMethod]
    public void LookupsAreCachedUntilRefresh()
    {
        var reg = new FakeRegistry().Surrogate(Word);
        reg.Values[".docx" + ShellEx] = Word;
        var handlers = new ShellPreviewHandlers(reg);

        handlers.Find("docx");
        var reads = reg.Reads;
        Assert.AreEqual(Guid.Parse(Word), handlers.Find("DOCX"));
        Assert.AreEqual(reads, reg.Reads);

        reg.Values.Remove(".docx" + ShellEx);
        handlers.Refresh();
        Assert.IsNull(handlers.Find("docx"));
    }

    [TestMethod]
    public void AFailedHandlerIsSkippedForTheSession()
    {
        var reg = new FakeRegistry().Surrogate(Word);
        reg.Values[".docx" + ShellEx] = Word;
        reg.Values[".rtf" + ShellEx] = Word;
        var handlers = new ShellPreviewHandlers(reg);

        handlers.MarkFailed(Guid.Parse(Word));
        Assert.IsNull(handlers.Find("docx"));
        Assert.IsNull(handlers.Find("rtf"));
        handlers.Refresh();
        Assert.IsNull(handlers.Find("docx"), "a refresh keeps the failures");
    }

    [TestMethod]
    public void TheSystemRegistryIsEmptyOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.IsNull(SystemAssociationRegistry.Instance.ClassesRoot(".docx"));
        Assert.IsFalse(SystemAssociationRegistry.Instance.ClassesRootHasKey(".docx"));
        Assert.IsNull(SystemAssociationRegistry.Instance.UserChoice(".docx"));
    }
}
