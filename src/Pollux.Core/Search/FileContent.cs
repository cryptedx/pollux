using System.Text;

namespace Pollux.Core.Search;

/// <summary>Plain-text content search: case-insensitive substring, streamed, binaries skipped.</summary>
public static class FileContent
{
    /// <summary>How much of the start of a file is checked for NUL bytes, like git's binary detection.</summary>
    public const int SniffBytes = 8000;

    /// <summary>True when the file at <paramref name="path"/> is text and contains <paramref name="text"/>.</summary>
    public static bool Contains(string path, string text, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        return Contains(stream, text, cancellationToken);
    }

    /// <summary>
    /// True when <paramref name="stream"/> is text and contains <paramref name="text"/> (case-insensitive).
    /// Text is UTF-8 unless a byte order mark says UTF-16; without one, a NUL byte near the start means binary.
    /// </summary>
    public static bool Contains(Stream stream, string text, CancellationToken cancellationToken, int bufferChars = 64 * 1024)
    {
        if (text.Length == 0) return false;
        cancellationToken.ThrowIfCancellationRequested();

        var head = new byte[SniffBytes];
        var headLength = ReadFully(stream, head);
        if (headLength == 0) return false;
        var encoding = EncodingFor(head.AsSpan(0, headLength));
        if (encoding is null) return false;

        using var reader = new StreamReader(new PrefixedStream(head, headLength, stream), encoding, detectEncodingFromByteOrderMarks: true);
        // The last text.Length - 1 chars of one chunk stay in front of the next, so a match across chunks is found.
        var keep = text.Length - 1;
        var buffer = new char[Math.Max(bufferChars, keep + 1) + keep];
        var filled = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, filled, buffer.Length - filled);
            if (read == 0) return false;
            filled += read;
            if (buffer.AsSpan(0, filled).Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            var tail = Math.Min(keep, filled);
            buffer.AsSpan(filled - tail, tail).CopyTo(buffer);
            filled = tail;
        }
    }

    /// <summary>The encoding a byte order mark names, UTF-8 for BOM-less text, or null for binary.</summary>
    private static Encoding? EncodingFor(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode;
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode;
        return head.Contains((byte)0) ? null : Encoding.UTF8;
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }

        return total;
    }

    /// <summary>Replays the sniffed bytes, then continues with the rest of the stream (which need not be seekable).</summary>
    private sealed class PrefixedStream(byte[] prefix, int prefixLength, Stream rest) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position < prefixLength)
            {
                var n = Math.Min(count, prefixLength - _position);
                Array.Copy(prefix, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            return rest.Read(buffer, offset, count);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
