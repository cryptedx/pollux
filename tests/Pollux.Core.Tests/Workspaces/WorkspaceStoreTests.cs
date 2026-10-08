using Pollux.Core.Session;
using Pollux.Core.Workspaces;

namespace Pollux.Core.Tests.Workspaces;

[TestClass]
public sealed class WorkspaceStoreTests
{
    [TestMethod]
    public void SaveListRenameDelete()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new WorkspaceStore(dir.FullName);
            var w = new Workspace { Name = "Pollux: dev", LastUsed = DateTime.Now, Window = new WindowSession { DualPane = false } };
            w.Window.Panes[0].Tabs[0].Location = @"C:\dev\pollux";
            w.Bookmarks.Add(@"C:\dev\pollux\docs", "docs");
            store.Save(w);
            store.Save(new Workspace { Name = "Older", LastUsed = DateTime.Now.AddDays(-1) });

            var list = store.List();
            Assert.AreEqual("Pollux: dev", list[0].Name);
            Assert.AreEqual(@"C:\dev\pollux", list[0].Window.Panes[0].Tabs[0].Location);
            Assert.AreEqual("docs", list[0].Bookmarks.Flat.Single().Name);
            Assert.AreEqual(1, Directory.GetFiles(dir.FullName, "Pollux_ dev-*.json").Length);

            // Names that sanitize to the same file must not overwrite each other.
            store.Save(new Workspace { Name = "a:b" });
            store.Save(new Workspace { Name = "a?b" });
            Assert.IsNotNull(store.Get("a:b"));
            Assert.IsNotNull(store.Get("a?b"));
            store.Delete("a:b");
            store.Delete("a?b");

            store.Rename("Older", "Archive");
            Assert.IsNotNull(store.Get("archive"));
            store.Delete("Archive");
            Assert.AreEqual(1, store.List().Count);
        }
        finally { dir.Delete(true); }
    }
}
