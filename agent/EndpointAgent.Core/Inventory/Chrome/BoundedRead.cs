namespace EndpointAgent.Core.Inventory.Chrome;

/// <summary>
/// Reads a stream into memory up to a stated bound and not one byte further,
/// whatever length the stream claims to have.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the length is not trusted.</b> The files the Chrome collector opens sit
/// in a user's own profile and are opened with shared write access, so their
/// length at the instant of reading is the user's to choose, and it can change
/// between a length check and the read that follows. A parser that reads "to
/// the end" follows that change into memory: <c>JsonDocument.Parse(Stream)</c>
/// buffers the entire stream before it looks at a byte, so a file grown to
/// gigabytes in that window (sparse, so instantly and at no cost on disk) would
/// become a gigabyte allocation in a LocalSystem process. Reading at most one
/// byte past the bound is enough to know it was crossed, and the allocation can
/// never exceed the bound plus one.
/// </para>
/// <para>
/// A seekable stream that admits to being over the bound is refused before a
/// byte of it is read. Otherwise the claimed length only sizes the first buffer,
/// so a file of the usual few kilobytes costs a few kilobytes and one that turns
/// out longer than it said grows the buffer toward the bound, never past it.
/// </para>
/// </remarks>
internal static class BoundedRead
{
    /// <summary>
    /// At most <paramref name="maxBytes"/> bytes of the stream, with a UTF-8 byte
    /// order mark stripped, or null when the stream holds more than that.
    /// </summary>
    /// <remarks>
    /// An I/O fault while reading propagates: the caller knows which file it
    /// opened and how to report it. Only the bound is decided here.
    /// </remarks>
    public static ReadOnlyMemory<byte>? ReadAtMost(Stream stream, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, int.MaxValue - 1L);

        long claimed = 0;
        if (stream.CanSeek)
        {
            claimed = Math.Max(0, stream.Length - stream.Position);
            if (claimed > maxBytes)
            {
                return null;
            }
        }

        // One byte more than the bound, so a stream that stops exactly at the
        // bound is told apart from one that goes past it.
        var buffer = new byte[(int)claimed + 1];
        var total = 0;

        while (true)
        {
            if (total == buffer.Length)
            {
                if (total > maxBytes)
                {
                    return null;
                }

                // Longer than it said. Grow toward the bound, never past it.
                Array.Resize(ref buffer, (int)Math.Min(buffer.Length * 2L, maxBytes + 1));
            }

            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        // JsonDocument's stream overload strips a byte order mark and its memory
        // overload does not. Chrome writes none, but a file that has been through
        // an editor may carry one, and that is no reason to lose it.
        var offset = total >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        return new ReadOnlyMemory<byte>(buffer, offset, total - offset);
    }
}
