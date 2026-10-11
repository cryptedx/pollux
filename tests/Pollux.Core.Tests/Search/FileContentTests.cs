using System.Text;
using Pollux.Core.Search;

namespace Pollux.Core.Tests.Search;

[TestClass]
public sealed class FileContentTests
{
    private static bool Contains(byte[] bytes, string text, int bufferChars = 4096) =>
        FileContent.Contains(new MemoryStream(bytes), text, CancellationToken.None, bufferChars);

    [TestMethod]
    public void FindsTextCaseInsensitively()
    {
        Assert.IsTrue(Contains(Encoding.UTF8.GetBytes("first line\nThe Needle is here\n"), "needle"));
        Assert.IsFalse(Contains(Encoding.UTF8.GetBytes("nothing to see"), "needle"));
    }

    [TestMethod]
    public void FindsTextAcrossBufferBoundaries()
    {
        var text = new string('x', 100) + "needle" + new string('y', 100);

        for (var size = 8; size < 20; size++)
        {
            Assert.IsTrue(Contains(Encoding.UTF8.GetBytes(text), "NEEDLE", size), $"buffer {size}");
        }
    }

    [TestMethod]
    public void SkipsBinaryFiles()
    {
        var bytes = Encoding.UTF8.GetBytes("needle").Concat(new byte[] { 0, 1, 2, 3 }).ToArray();

        Assert.IsFalse(Contains(bytes, "needle"));
    }

    [TestMethod]
    public void ReadsUtf8WithAndWithoutBomAndUtf16()
    {
        Assert.IsTrue(Contains(Encoding.UTF8.GetBytes("Größe: München"), "münchen"));
        Assert.IsTrue(Contains(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Größe")).ToArray(), "grö"));
        Assert.IsTrue(Contains(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("wide needle")).ToArray(), "needle"));
        Assert.IsTrue(Contains(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("big needle")).ToArray(), "needle"));
    }

    [TestMethod]
    public void EmptyTextOrFileNeverMatches()
    {
        Assert.IsFalse(Contains([], "x"));
        Assert.IsFalse(Contains(Encoding.UTF8.GetBytes("abc"), ""));
    }

    [TestMethod]
    public void StopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            FileContent.Contains(new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 100_000))), "needle", cts.Token));
    }
}
