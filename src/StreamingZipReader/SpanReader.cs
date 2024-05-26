using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Wololo.StreamingZipReader;

[DebuggerDisplay("{" + nameof(GetDebuggerDisplay) + "(),nq}")]
internal ref struct SpanReader(ReadOnlySpan<byte> span)
{
    private ReadOnlySpan<byte> span = span;

    public int RemainingByteCount => span.Length;

    public void Skip(int byteCount) => span = span[byteCount..];

    public bool ReadFixedValue(ReadOnlySpan<byte> value)
    {
        if (!span.StartsWith(value)) return false;
        span = span[value.Length..];
        return true;
    }

    public ushort ReadUInt16LittleEndian()
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(span);
        span = span[sizeof(ushort)..];
        return value;
    }

    public uint ReadUInt32LittleEndian()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(span);
        span = span[sizeof(uint)..];
        return value;
    }

    public ulong ReadUInt64LittleEndian()
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(span);
        span = span[sizeof(ulong)..];
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int byteCount)
    {
        var value = span[..byteCount];
        span = span[byteCount..];
        return value;
    }

    private string GetDebuggerDisplay()
    {
        const int maxDisplayedByteCount = 32;

        var builder = new StringBuilder();
        builder.Append(span.Length);
        builder.Append(" bytes left");

        if (span.Length != 0)
        {
            builder.Append(": ");

            foreach (var value in span.Length > maxDisplayedByteCount ? span[..maxDisplayedByteCount] : span)
                builder.Append(value.ToString("X2"));

            if (span.Length > maxDisplayedByteCount)
                builder.Append('…');
        }

        return builder.ToString();
    }
}
