using Pollux.Core.Launch;

namespace Pollux.Core.Tests.Launch;

[TestClass]
public sealed class ShellKindTests
{
    [TestMethod]
    [DataRow("pwsh", ShellKind.Pwsh)]
    [DataRow(" PowerShell ", ShellKind.WindowsPowerShell)]
    [DataRow("CMD", ShellKind.Cmd)]
    public void ShellNamesFromSettingsAreParsed(string name, ShellKind kind) => Assert.AreEqual(kind, ShellKinds.Parse(name));

    [TestMethod]
    [DataRow("wt")]
    [DataRow("bash")]
    [DataRow("")]
    [DataRow(null)]
    public void OtherNamesAreNoShell(string? name) => Assert.IsNull(ShellKinds.Parse(name));
}
