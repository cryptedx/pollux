using Pollux.Core.ShellNew;

namespace Pollux.Core.Tests.ShellNew;

[TestClass]
public sealed class ShellNewTests
{
    private static ShellNewRaw Raw(string ext, string? progId = null, string? itemName = null, bool nullFile = false, string? fileName = null, byte[]? data = null, bool command = false, string? friendly = null, string? description = null)
        => new(ext, progId, itemName, nullFile, fileName, data, command, friendly, description);

    private static string? Indirect(string s) => s == "@x,-1" ? "Resolved" : null;

    private static IReadOnlyList<ShellNewEntry> Build(params ShellNewRaw[] raws)
        => ShellNewCatalog.Build(raws, Indirect, f => f == "missing.dat" ? null : "/t/" + f);

    [TestMethod]
    public void KindsFollowTheRegisteredValues()
    {
        var entries = Build(
            Raw(".a", nullFile: true, description: "A"),
            Raw(".b", fileName: "b.bin", description: "B"),
            Raw(".c", data: [1, 2], description: "C"));
        CollectionAssert.AreEqual(new[] { ShellNewKind.Empty, ShellNewKind.Template, ShellNewKind.Data }, entries.Select(e => e.Kind).ToArray());
        Assert.AreEqual("/t/b.bin", entries[1].TemplatePath);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, entries[2].Data);
    }

    [TestMethod]
    public void CommandHandlersMissingTemplatesAndEmptyKeysAreSkipped()
    {
        var entries = Build(
            Raw(".cmd", nullFile: true, command: true, description: "Cmd"),
            Raw(".gone", fileName: "missing.dat", description: "Gone"),
            Raw(".none", description: "None"),
            Raw(".", nullFile: true));
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void FirstUsableKeyPerExtensionWins()
    {
        var entries = Build(
            Raw(".doc", progId: "A", fileName: "missing.dat", description: "Broken"),
            Raw(".DOC", progId: "B", nullFile: true, description: "Fine"));
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("Fine", entries[0].DisplayName);
    }

    [TestMethod]
    public void DisplayNameFallsBackAndResolvesIndirectStrings()
    {
        Assert.AreEqual("Item", ShellNewCatalog.DisplayName(Raw(".x", itemName: "Item", friendly: "F", description: "D"), Indirect));
        Assert.AreEqual("Resolved", ShellNewCatalog.DisplayName(Raw(".x", itemName: "@x,-1", friendly: "F"), Indirect));
        Assert.AreEqual("F", ShellNewCatalog.DisplayName(Raw(".x", itemName: "@unknown,-9", friendly: "F"), Indirect));
        Assert.AreEqual("D", ShellNewCatalog.DisplayName(Raw(".x", description: "D"), Indirect));
        Assert.AreEqual("RTF File", ShellNewCatalog.DisplayName(Raw(".rtf"), Indirect));
    }

    [TestMethod]
    public void EntriesAreSortedByDisplayName()
    {
        var entries = Build(Raw(".z", nullFile: true, description: "beta"), Raw(".y", nullFile: true, description: "Alpha"));
        CollectionAssert.AreEqual(new[] { "Alpha", "beta" }, entries.Select(e => e.DisplayName).ToArray());
    }

    [TestMethod]
    public void CatalogCachesUntilInvalidated()
    {
        var source = new CountingSource();
        var catalog = new ShellNewCatalog(source, Indirect, f => f);
        Assert.AreEqual(1, catalog.Entries().Count);
        _ = catalog.Entries();
        Assert.AreEqual(1, source.Reads);
        catalog.Invalidate();
        _ = catalog.Entries();
        Assert.AreEqual(2, source.Reads);
    }

    [TestMethod]
    public void UniqueNameNumbersFromTwo()
    {
        var entry = new ShellNewEntry(".docx", "Microsoft Word Document", ShellNewKind.Empty);
        Assert.AreEqual("New Microsoft Word Document.docx", ShellNewCreator.UniqueName(entry, _ => false));
        var taken = new HashSet<string> { "New Microsoft Word Document.docx", "New Microsoft Word Document (2).docx" };
        Assert.AreEqual("New Microsoft Word Document (3).docx", ShellNewCreator.UniqueName(entry, taken.Contains));
    }

    [TestMethod]
    public void UniqueNameKeepsNamesThatAreAlreadyNewNames()
    {
        var entry = new ShellNewEntry(".docx", "Neues Office Open XML-Dokument", ShellNewKind.Empty);
        Assert.AreEqual("Neues Office Open XML-Dokument.docx", ShellNewCreator.UniqueName(entry, _ => false));
    }

    [TestMethod]
    public void SanitizeRemovesInvalidCharactersAndTrailingDots()
    {
        Assert.AreEqual("A B", ShellNewCreator.Sanitize("A/B"));
        Assert.AreEqual("Doc", ShellNewCreator.Sanitize("Doc. "));
        Assert.AreEqual("File", ShellNewCreator.Sanitize("\0\0"));
    }

    [TestMethod]
    public void TemplateResolutionSearchesFoldersInOrder()
    {
        var existing = new HashSet<string> { Path.Combine("b", "t.xlsx"), Path.Combine("c", "t.xlsx") };
        Assert.AreEqual(Path.Combine("b", "t.xlsx"), ShellNewTemplates.Resolve("t.xlsx", ["a", "", "b", "c"], existing.Contains));
        Assert.IsNull(ShellNewTemplates.Resolve("other.xlsx", ["a", "b"], existing.Contains));
        var absolute = Path.GetFullPath("abs.bin");
        Assert.AreEqual(absolute, ShellNewTemplates.Resolve(absolute, ["a"], p => p == absolute));
    }

    [TestMethod]
    public void CreateWritesEmptyDataAndTemplateAndNeverOverwrites()
    {
        var dir = Directory.CreateTempSubdirectory("shellnew").FullName;
        try
        {
            var template = Path.Combine(dir, "template.bin");
            File.WriteAllBytes(template, [9, 8, 7]);

            var empty = ShellNewCreator.Create(dir, new ShellNewEntry(".e", "Empty", ShellNewKind.Empty));
            var data = ShellNewCreator.Create(dir, new ShellNewEntry(".d", "Data", ShellNewKind.Data, Data: [1, 2, 3]));
            var copy = ShellNewCreator.Create(dir, new ShellNewEntry(".t", "Tpl", ShellNewKind.Template, template));
            var copy2 = ShellNewCreator.Create(dir, new ShellNewEntry(".t", "Tpl", ShellNewKind.Template, template));

            Assert.AreEqual(0, new FileInfo(empty).Length);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(data));
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, File.ReadAllBytes(copy));
            Assert.AreEqual("New Tpl.t", Path.GetFileName(copy));
            Assert.AreEqual("New Tpl (2).t", Path.GetFileName(copy2));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void FailedTemplateCopyLeavesNoFileBehind()
    {
        var dir = Directory.CreateTempSubdirectory("shellnew").FullName;
        try
        {
            var entry = new ShellNewEntry(".t", "Tpl", ShellNewKind.Template, Path.Combine(dir, "nope.bin"));
            Assert.ThrowsExactly<FileNotFoundException>(() => ShellNewCreator.Create(dir, entry));
            Assert.AreEqual(0, Directory.GetFiles(dir).Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class CountingSource : IShellNewSource
    {
        public int Reads { get; private set; }

        public IReadOnlyList<ShellNewRaw> Read()
        {
            Reads++;
            return [Raw(".q", nullFile: true, description: "Q")];
        }
    }

    [TestMethod]
    public void NetworkTemplatesAndResourceStringsAreNeverTouched()
    {
        var asked = new List<string>();
        var entries = ShellNewCatalog.Build(
            [Raw(".a", fileName: @"\\server\t.dat", description: "A"), Raw(".b", fileName: "//server/t.dat", description: "B"), Raw(".c", fileName: "c.dat", description: "C")],
            s => { asked.Add(s); return "Resolved"; },
            f => { asked.Add(f); return "/t/" + f; });
        CollectionAssert.AreEqual(new[] { ".c" }, entries.Select(e => e.Extension).ToArray());
        Assert.AreEqual("F", ShellNewCatalog.DisplayName(Raw(".x", itemName: @"@\\server\x.dll,-1", friendly: "F"), s => { asked.Add(s); return "Resolved"; }));
        CollectionAssert.AreEqual(new[] { "c.dat" }, asked);
    }
}
