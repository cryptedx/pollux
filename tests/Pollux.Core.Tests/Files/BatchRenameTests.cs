using Pollux.Core.Files;

namespace Pollux.Core.Tests.Files;

[TestClass]
public sealed class BatchRenameTests
{
    private static RenameSource F(string name) => new(name, false);

    private static RenamePlan Plan(BatchRenameOptions options, string[] names, params string[] existing)
    {
        var set = new HashSet<string>(existing.Concat(names), StringComparer.OrdinalIgnoreCase);
        return BatchRename.Plan([.. names.Select(F)], options, set.Contains);
    }

    private static string[] New(RenamePlan plan) => [.. plan.Items.Select(i => i.NewName)];

    [TestMethod]
    public void PlainFindReplaceIsLiteralAndCaseInsensitiveByDefault()
    {
        var plan = Plan(new(Find: "a.b", Replace: "X"), ["A.B.txt", "axb.txt"]);

        CollectionAssert.AreEqual(new[] { "X.txt", "axb.txt" }, New(plan));
        Assert.IsTrue(plan.CanApply);
    }

    [TestMethod]
    public void MatchCaseRestrictsPlainReplace()
    {
        var plan = Plan(new(Find: "img", Replace: "pic", MatchCase: true), ["IMG_1.jpg", "img_2.jpg"]);

        CollectionAssert.AreEqual(new[] { "IMG_1.jpg", "pic_2.jpg" }, New(plan));
    }

    [TestMethod]
    public void RegexSupportsGroups()
    {
        var plan = Plan(new(Find: @"(\d+)-(\w+)", Replace: "$2-$1", UseRegex: true), ["01-foo.txt", "02-bar.txt"]);

        CollectionAssert.AreEqual(new[] { "foo-01.txt", "bar-02.txt" }, New(plan));
    }

    [TestMethod]
    public void InvalidRegexIsAnOptionsErrorAndChangesNothing()
    {
        var plan = Plan(new(Find: "(", UseRegex: true), ["a.txt"]);

        Assert.IsNotNull(plan.OptionsError);
        Assert.IsTrue(plan.HasConflicts);
        Assert.IsFalse(plan.CanApply);
        CollectionAssert.AreEqual(new[] { "a.txt" }, New(plan));
    }

    [TestMethod]
    public void CaseChangesTouchTheStemOnly()
    {
        Assert.AreEqual("report.TXT", New(Plan(new(Case: RenameCase.Lower), ["REPORT.TXT"]))[0]);
        Assert.AreEqual("REPORT.txt", New(Plan(new(Case: RenameCase.Upper), ["report.txt"]))[0]);
        Assert.AreEqual("My Big-File_Name.txt", New(Plan(new(Case: RenameCase.Title), ["mY bIG-fILE_name.txt"]))[0]);
    }

    [TestMethod]
    public void DotfilesAndFoldersHaveNoExtension()
    {
        var plan = BatchRename.Plan([F(".gitignore"), new("v1.2", true)], new(Case: RenameCase.Upper), _ => false);

        CollectionAssert.AreEqual(new[] { ".GITIGNORE", "V1.2" }, New(plan));
    }

    [TestMethod]
    public void TemplateNumbersInSelectionOrderWithPaddingStartAndStep()
    {
        var plan = Plan(new(Template: "{name}_{n:000}{ext}", Start: 5, Step: 5), ["a.png", "b.png", "c.jpg"]);

        CollectionAssert.AreEqual(new[] { "a_005.png", "b_010.png", "c_015.jpg" }, New(plan));
    }

    [TestMethod]
    public void TemplateWithoutFormatAndEscapedBraces()
    {
        var plan = Plan(new(Template: "{{{n}}}{ext}"), ["a.txt", "b.txt"]);

        CollectionAssert.AreEqual(new[] { "{1}.txt", "{2}.txt" }, New(plan));
    }

    [TestMethod]
    public void TemplateSeesNameAfterFindReplaceAndCase()
    {
        var plan = Plan(new(Find: "IMG", Replace: "photo", Case: RenameCase.Upper, Template: "{name}-{n}{ext}"), ["IMG_1.jpg"]);

        Assert.AreEqual("PHOTO_1-1.jpg", New(plan)[0]);
    }

    [TestMethod]
    [DataRow("{foo}")]
    [DataRow("{name")]
    [DataRow("name}")]
    [DataRow("{n:}")]
    public void BrokenTemplatesAreOptionsErrors(string template)
    {
        var plan = Plan(new(Template: template), ["a.txt"]);

        Assert.IsNotNull(plan.OptionsError);
        Assert.IsFalse(plan.CanApply);
    }

    [TestMethod]
    public void NoOptionsMeansNothingToApply()
    {
        var plan = Plan(new(), ["a.txt", "b.txt"]);

        Assert.IsFalse(plan.HasChanges);
        Assert.IsFalse(plan.CanApply);
        Assert.IsFalse(plan.HasConflicts);
    }

    [TestMethod]
    public void DuplicateTargetsAreConflictsOnAllInvolvedItems()
    {
        var dup = Plan(new(Template: "same.txt"), ["a.txt", "b.txt"]);

        Assert.IsTrue(dup.Items.All(i => i.Error == "Duplicate target name"));
        Assert.IsFalse(dup.CanApply);
    }

    [TestMethod]
    public void DuplicateTargetsCompareCaseInsensitively()
    {
        var plan = Plan(new(Find: "^(.)", Replace: "z", UseRegex: true), ["a.txt", "b.txt"]);
        Assert.IsTrue(plan.HasConflicts);

        var mixed = Plan(new(Template: "{name}{ext}"), ["a.txt", "A.txt"]);
        Assert.IsFalse(mixed.HasChanges);
        Assert.IsTrue(mixed.HasConflicts, "two entries cannot share a name even if unchanged");
    }

    [TestMethod]
    public void ExistingFileOutsideTheSelectionBlocks()
    {
        var plan = Plan(new(Find: "a", Replace: "b"), ["a.txt"], "b.txt");

        Assert.AreEqual("A file or folder with this name already exists", plan.Items[0].Error);
        Assert.IsFalse(plan.CanApply);
    }

    [TestMethod]
    public void TargetThatAnotherSelectedItemVacatesIsNotAConflict()
    {
        var plan = Plan(new(Template: "{n}{ext}", Start: 1), ["2.txt", "1.txt"]);

        CollectionAssert.AreEqual(new[] { "1.txt", "2.txt" }, New(plan));
        Assert.IsTrue(plan.CanApply);
    }

    [TestMethod]
    public void CaseOnlyChangeIsNotReportedAsExisting()
    {
        var plan = Plan(new(Case: RenameCase.Upper), ["a.txt"]);

        Assert.IsTrue(plan.CanApply);
    }

    [TestMethod]
    [DataRow("a/b.txt")]
    [DataRow("a:b.txt")]
    [DataRow("a?.txt")]
    [DataRow("trailing.")]
    [DataRow("trailing ")]
    [DataRow("CON")]
    [DataRow("nul.txt")]
    public void InvalidNamesAreRejected(string name)
    {
        var plan = Plan(new(Template: name), ["a.txt"]);

        Assert.IsNotNull(plan.Items[0].Error, name);
        Assert.IsFalse(plan.CanApply);
    }

    [TestMethod]
    public void OverlongNameIsRejected()
    {
        var plan = Plan(new(Template: new string('a', 256)), ["a.txt"]);

        Assert.AreEqual("Name is too long", plan.Items[0].Error);
    }

    [TestMethod]
    public void StepsAreEmptyWhenNotApplicable()
    {
        var plan = Plan(new(Template: "same"), ["a", "b"]);

        Assert.AreEqual(0, plan.Steps(_ => false).Count);
    }

    [TestMethod]
    public void IndependentRenamesRunDirectly()
    {
        var plan = Plan(new(Find: "a", Replace: "b"), ["a1.txt", "a2.txt"]);

        CollectionAssert.AreEqual(
            new[] { new RenameStep("a1.txt", "b1.txt"), new RenameStep("a2.txt", "b2.txt") },
            plan.Steps(_ => false).ToArray());
    }

    [TestMethod]
    public void ChainRunsTheVacatingItemFirst()
    {
        // 1.txt -> 2.txt needs 2.txt -> 3.txt out of the way first.
        var plan = Plan(new(Template: "{n}{ext}", Start: 2), ["1.txt", "2.txt"]);

        CollectionAssert.AreEqual(
            new[] { new RenameStep("2.txt", "3.txt"), new RenameStep("1.txt", "2.txt") },
            plan.Steps(_ => false).ToArray());
    }

    [TestMethod]
    public void SwapGoesThroughATemporaryName()
    {
        var steps = SwapPlan().Steps(_ => false);

        Assert.AreEqual(3, steps.Count);
        Assert.IsTrue(steps[0].To.EndsWith(".tmp", StringComparison.Ordinal));
        AssertSimulatedResult(["1.txt", "2.txt"], steps, ["2.txt", "1.txt"]);
    }

    [TestMethod]
    public void ThreeCycleAndCaseOnlyAreSimulatedCorrectly()
    {
        var cycle = Plan(new(Template: "{n}{ext}", Start: 1), ["2.txt", "3.txt", "1.txt"]);
        var steps = cycle.Steps(_ => false);
        AssertSimulatedResult(["2.txt", "3.txt", "1.txt"], steps, ["1.txt", "2.txt", "3.txt"]);

        var caseOnly = Plan(new(Case: RenameCase.Upper), ["a.txt"]).Steps(_ => false);
        Assert.AreEqual(2, caseOnly.Count, "case-only renames need a temp name to be safe on case-insensitive volumes");
        Assert.AreEqual("A.txt", caseOnly[^1].To);
    }

    [TestMethod]
    public void TemporaryNameAvoidsExistingFiles()
    {
        var steps = SwapPlan().Steps(n => n == ".pollux-rename-1.tmp");

        Assert.AreEqual(".pollux-rename-2.tmp", steps[0].To);
    }

    /// <summary>1.txt and 2.txt exchange names.</summary>
    private static RenamePlan SwapPlan()
    {
        return Plan(new(Template: "{n}{ext}", Start: 2, Step: -1), ["1.txt", "2.txt"]);
    }

    private static void AssertSimulatedResult(string[] start, IReadOnlyList<RenameStep> steps, string[] expected)
    {
        var fs = new HashSet<string>(start, StringComparer.OrdinalIgnoreCase);
        foreach (var s in steps)
        {
            Assert.IsTrue(fs.Remove(s.From), $"{s.From} must exist");
            Assert.IsTrue(fs.Add(s.To), $"{s.To} must be free");
        }

        CollectionAssert.AreEquivalent(expected, fs.ToArray());
    }
}
