namespace Wololo.StreamingZipReader;

public sealed record StreamingZipEntry(string Name, uint Crc32, long CompressedLength, long Length, bool DataDescriptor);