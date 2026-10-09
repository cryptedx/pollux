using Pollux.Core.Copilot.Guards;

namespace Pollux.Core.Tests.Copilot;

/// <summary>
/// Shell commands whose effect the Guard Rules cannot work out from the text are asked, never allowed (fail closed);
/// each data row is a way around the rules found in review.
/// </summary>
[TestClass]
public sealed class GuardRulesFailClosedTests
{
    private const string WorkingFolder = @"C:\repos\app";

    private static readonly Dictionary<string, string> Env = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = @"C:\Users\marko",
        ["APPDATA"] = @"C:\Users\marko\AppData\Roaming",
        ["LOCALAPPDATA"] = @"C:\Users\marko\AppData\Local",
        ["TEMP"] = @"C:\Users\marko\AppData\Local\Temp",
    };

    private static GuardContext Context() => new(WorkingFolder, false, name => Env.TryGetValue(name, out var v) ? v : null);

    private static GuardVerdict Evaluate(ToolRequest request) => new GuardRules().Evaluate(request, Context());

    private static GuardVerdict Shell(string command) => Evaluate(ToolRequest.Shell(command));

    private static void AssertCannotAnalyse(string command)
    {
        var verdict = Shell(command);
        Assert.AreNotEqual(GuardDecision.Allow, verdict.Decision, command);
        if (verdict.Decision == GuardDecision.Ask)
        {
            StringAssert.Contains(verdict.Reason, GuardRules.CannotAnalyse, command);
        }
    }

    [TestMethod]
    [DataRow(@"cd C:\Users\marko; Get-Content .ssh\id_rsa")]
    [DataRow(@"Set-Location ~ ; type .aws\credentials")]
    [DataRow("sl ~; cat .aws/credentials")]
    [DataRow(@"pushd C:\Users\marko && type .ssh\id_rsa")]
    [DataRow(@"chdir C:\Users\marko & type .ssh\id_rsa")]
    [DataRow(@"cd /d C:\Users\marko && del /q notes.txt")]
    public void ChangingDirectoryBeforeOtherCommandsIsAsked(string command) => AssertCannotAnalyse(command);

    [TestMethod]
    [DataRow("cd src")]
    [DataRow("git status")]
    public void ADirectoryChangeAloneOrNoneIsAllowed(string command) =>
        Assert.AreEqual(GuardDecision.Allow, Shell(command).Decision, command);

    [TestMethod]
    [DataRow(@"$p = 'C:\Users\marko\.s' + 'sh\id_rsa'; Get-Content $p")]
    [DataRow("Get-Content $(Join-Path $HOME .ssh/id_rsa)")]
    [DataRow(@"type %SECRET_DIR%\id_rsa")]
    [DataRow(@"set D=C:\Users\marko\.ssh && type %D%\id_rsa")]
    [DataRow(@"Get-Content C:\Users\marko\.s`sh\id_rsa")]
    [DataRow(@"$rm = 'Remove-Item'; & $rm -Recurse -Force C:\repos")]
    [DataRow("cat \"$SECRET/id_rsa\"")]
    public void VariablesAndSubexpressionsAsPathsAreAsked(string command) => AssertCannotAnalyse(command);

    [TestMethod]
    [DataRow(@"Get-Content C:\Users\marko\.ss?\id_rsa")]
    [DataRow(@"type C:\Users\*\.ssh\id_rsa")]
    [DataRow("cat ~/.s*/id_*")]
    [DataRow(@"Get-Content C:\Users\marko\.ss[h]\config")]
    [DataRow("cat .en?")]
    [DataRow(@"type secrets\*.ke*")]
    public void WildcardsMatchingAProtectedSecretAreBlocked(string command) =>
        Assert.AreEqual(GuardDecision.Deny, Shell(command).Decision, command);

    [TestMethod]
    [DataRow(@"Copy-Item C:\Users\marko\* -Recurse C:\repos\app\loot")]
    [DataRow(@"Compress-Archive C:\Users\* C:\repos\app\all.zip")]
    [DataRow(@"Select-String -Path C:\Users\marko\* -Pattern key")]
    public void WildcardsAboveAProtectedSecretAreNotAllowed(string command) =>
        Assert.AreNotEqual(GuardDecision.Allow, Shell(command).Decision, command);

    [TestMethod]
    [DataRow(@"Get-Content src\*.cs")]
    [DataRow("cat *.txt")]
    public void WildcardsElsewhereAreAllowed(string command) =>
        Assert.AreEqual(GuardDecision.Allow, Shell(command).Decision, command);

    [TestMethod]
    public void NestingDeeperThanTheParserUnwrapsIsAsked() =>
        AssertCannotAnalyse("cmd /c cmd /c cmd /c cmd /c cmd /c cmd /c format D:");

    [TestMethod]
    public void AnUndecodableEncodedCommandIsAsked() =>
        AssertCannotAnalyse("powershell -EncodedCommand !!!not-base64!!!");

    [TestMethod]
    [DataRow("python -c \"import os; os.remove('x')\"")]
    [DataRow("python3 -c \"print(1)\"")]
    [DataRow("node -e \"require('fs').rmSync('C:/Users', {recursive:true})\"")]
    [DataRow("node --eval \"1\"")]
    [DataRow("perl -e 'unlink glob q(~/.ssh/*)'")]
    [DataRow("ruby -e \"File.delete('x')\"")]
    [DataRow("php -r \"unlink('x');\"")]
    [DataRow(@"sed -i s/a/b/ C:\Windows\win.ini")]
    [DataRow(@"perl -pi -e s/a/b/ C:\x.txt")]
    [DataRow(@"git -C C:\other commit -am x")]
    public void InlineInterpreterCodeInPlaceEditsAndGitInAnotherFolderAreAsked(string command) => AssertCannotAnalyse(command);

    [TestMethod]
    [DataRow("cp notes.txt /Users")]
    [DataRow("mv build /tmp")]
    [DataRow(@"copy notes.txt /tmp")]
    public void PosixRootsAreNotMistakenForCmdSwitches(string command)
    {
        var verdict = Shell(command);
        Assert.AreEqual(GuardDecision.Ask, verdict.Decision, command);
        StringAssert.Contains(verdict.Reason, "outside the Working Folder");
    }

    [TestMethod]
    [DataRow("robocopy src dst /MIR")]
    [DataRow("xcopy src dst /E /I /Y")]
    public void CmdSwitchesAreStillSwitches(string command) =>
        Assert.AreEqual(GuardDecision.Allow, Shell(command).Decision, command);

    [TestMethod]
    [DataRow(@"rg password C:\Users\marko")]
    [DataRow("grep -r BEGIN ~")]
    [DataRow(@"findstr /s /i password C:\Users\marko\*")]
    [DataRow(@"Get-ChildItem -Recurse C:\Users\marko | Get-Content")]
    [DataRow(@"dir /s C:\Users")]
    [DataRow(@"Get-ChildItem C:\Users\marko -Recurse -Filter *.txt | Select-String key")]
    public void RecursiveSearchesOverAProtectedSecretAreBlocked(string command) =>
        Assert.AreEqual(GuardDecision.Deny, Shell(command).Decision, command);

    [TestMethod]
    public void RecursiveSearchOutsideTheWorkingFolderIsAsked() =>
        Assert.AreEqual(GuardDecision.Ask, Shell(@"rg TODO D:\other").Decision);

    [TestMethod]
    [DataRow("rg TODO src")]
    [DataRow("grep -rn TODO .")]
    public void RecursiveSearchInsideTheWorkingFolderIsAllowed(string command) =>
        Assert.AreEqual(GuardDecision.Allow, Shell(command).Decision, command);

    [TestMethod]
    public void ARecursiveReadToolOverAProtectedSecretIsBlocked() =>
        Assert.AreEqual(GuardDecision.Deny, Evaluate(ToolRequest.Read(@"C:\Users\marko") with { Recursive = true }).Decision);

    [TestMethod]
    public void ARecursiveReadToolOutsideTheWorkingFolderIsAsked() =>
        Assert.AreEqual(GuardDecision.Ask, Evaluate(ToolRequest.Read(@"D:\other") with { Recursive = true }).Decision);

    [TestMethod]
    public void ANonRecursiveReadOfTheHomeFolderIsAllowed() =>
        Assert.AreEqual(GuardDecision.Allow, Evaluate(ToolRequest.Read(@"C:\Users\marko")).Decision);

    [TestMethod]
    [DataRow("curl file:///C:/Users/marko/.ssh/id_rsa")]
    [DataRow("Invoke-WebRequest -Uri file://localhost/C:/Users/marko/.aws/credentials")]
    public void FileUrisInCommandsAreCheckedAsPaths(string command) =>
        Assert.AreEqual(GuardDecision.Deny, Shell(command).Decision, command);

    [TestMethod]
    public void FileUrisAsUrlsAreCheckedAsPaths() =>
        Assert.AreEqual(GuardDecision.Deny, Evaluate(ToolRequest.Url("file:///C:/Users/marko/.aws/credentials")).Decision);

    [TestMethod]
    [DataRow(@"C:\Users\marko\AppData\Local\Pollux\copilot\chats.json")]
    [DataRow(@"%LOCALAPPDATA%\Pollux\copilot\actions\chat.json")]
    public void PolluxsDataFolderIsProtected(string path)
    {
        Assert.AreEqual(GuardDecision.Deny, Evaluate(ToolRequest.Read(path)).Decision, path);
        Assert.AreEqual(GuardDecision.Deny, Evaluate(ToolRequest.Write(path)).Decision, path);
        Assert.AreEqual(GuardDecision.Deny, Shell($"Set-Content \"{path}\" x").Decision, path);
    }
}
