namespace Wololo.StreamingZipReader;

public readonly record struct StreamingZipEntry(string Name, uint Crc32, long CompressedLength, long Length);