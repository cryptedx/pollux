using Pollux.Copilot.Panel;

namespace Pollux.Copilot.Tests.Panel;

[TestClass]
public sealed class PanelChannelTests
{
    [TestMethod]
    public void MessagesWaitForReadyAndKeepTheirOrder()
    {
        var posted = new List<string>();
        var channel = new PanelChannel(posted.Add);

        channel.Send("a");
        channel.Send("b");
        Assert.AreEqual(0, posted.Count);
        Assert.IsFalse(channel.IsReady);

        var e = channel.Receive("""{"type":"ready"}""");

        Assert.IsInstanceOfType<PanelEvent.Ready>(e);
        CollectionAssert.AreEqual(new[] { "a", "b" }, posted);
        channel.Send("c");
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, posted);
    }

    [TestMethod]
    public void ResetHoldsMessagesUntilTheNextReady()
    {
        var posted = new List<string>();
        var channel = new PanelChannel(posted.Add);
        channel.Receive("""{"type":"ready"}""");

        channel.Reset();
        channel.Send("x");

        Assert.AreEqual(0, posted.Count);
        channel.Receive("""{"type":"ready"}""");
        CollectionAssert.AreEqual(new[] { "x" }, posted);
    }

    [TestMethod]
    public void UnknownMessagesDoNotMakeTheChannelReady()
    {
        var posted = new List<string>();
        var channel = new PanelChannel(posted.Add);
        channel.Send("a");

        Assert.IsNull(channel.Receive("garbage"));

        Assert.IsFalse(channel.IsReady);
        Assert.AreEqual(0, posted.Count);
    }
}
