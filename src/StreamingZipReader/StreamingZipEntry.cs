namespace Wololo.StreamingZipReader;

public sealed class StreamingZipEntry
{
    public StreamingZipEntry(string name, uint crc32, uint compressedLength, uint length)
    {
        Name = name;
        Crc32 = crc32;
        CompressedLength = compressedLength;
        Length = length;
    }

    public string Name { get; }
    public uint Crc32 { get; }
    public uint CompressedLength { get; }
    public uint Length { get; }
}