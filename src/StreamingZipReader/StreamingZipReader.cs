using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace Wololo.StreamingZipReader;

public sealed partial class StreamingZipReader : IAsyncDisposable
{
    private static readonly byte[] LocalFileHeader = [(byte)'P', (byte)'K', 3, 4];
    private static readonly byte[] CentralDirectoryHeader = [(byte)'P', (byte)'K', 1, 2];
    private static readonly byte[] Zip64ExtraTag = [1, 0];

    private readonly bool leaveOpen;
    private Stream? stream;
    private StreamingZipEntry? currentEntry;
    private SubStream? currentSubStream;
    private DeflateStream? currentDeflateStream;

    public StreamingZipReader(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanRead)
            throw new ArgumentException("The stream must be readable.", nameof(stream));

        this.stream = stream;
        this.leaveOpen = leaveOpen;
    }

    public ValueTask DisposeAsync() => leaveOpen || stream is null ? ValueTask.CompletedTask : stream.DisposeAsync();

    public async ValueTask<bool> MoveToNextEntryAsync(bool skipDirectories, CancellationToken cancellationToken)
    {
        if (stream is null)
            return false;

        var remainingLength = 0L;
        if (currentSubStream is not null)
        {
            currentSubStream.Detach();
            remainingLength = currentSubStream.Length - currentSubStream.Position;
            currentSubStream = null;
            currentDeflateStream = null;
        }
        else if (currentEntry is not null)
        {
            remainingLength = currentEntry.CompressedLength;
        }

        if (remainingLength != 0)
        {
            if (stream.CanSeek)
            {
                stream.Seek(remainingLength, SeekOrigin.Current);
            }
            else
            {
                var skipArray = ArrayPool<byte>.Shared.Rent(checked((int)remainingLength));
                await ReadBlockAsync(stream, skipArray.AsMemory(0, (int)remainingLength), cancellationToken).ConfigureAwait(false);
                ArrayPool<byte>.Shared.Return(skipArray);
            }
        }

        readEntry:
        var array = ArrayPool<byte>.Shared.Rent(30);
        var buffer = array.AsMemory(0, 30);

        var bytesRead = await ReadBlockAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        try
        {
            if (bytesRead == 0)
                return false;
            else if (bytesRead < buffer.Length)
                throw new InvalidDataException("The stream ended unexpectedly or is not a .zip archive having no bytes around file entries.");

            if (buffer.Span.StartsWith(CentralDirectoryHeader))
            {
                if (!leaveOpen) await stream.DisposeAsync().ConfigureAwait(false);
                stream = null;
                currentEntry = null;
                return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }

        var (crc32, compressedSize, uncompressedSize, fileNameLength, extraFieldLength, dataDescriptor) = ParseMinimumLocalFileHeader(buffer.Span);

        array = ArrayPool<byte>.Shared.Rent(fileNameLength + extraFieldLength);
        buffer = array.AsMemory(0, fileNameLength + extraFieldLength);

        bytesRead = await ReadBlockAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        try
        {
            if (bytesRead < buffer.Length)
                throw new InvalidDataException("The stream ended unexpectedly.");

            if (skipDirectories && compressedSize == 0 && buffer.Span[fileNameLength - 1] == '/')
                goto readEntry;

            var fileName = Encoding.ASCII.GetString(buffer.Span[..fileNameLength]);

            if (compressedSize == 0xffffffff)
                ParseZIP64ExtraField(buffer.Span[fileNameLength..], out compressedSize, out uncompressedSize);

            currentEntry = new(fileName, crc32, compressedSize, uncompressedSize, dataDescriptor);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }

        return true;
    }

    private static void ParseZIP64ExtraField(ReadOnlySpan<byte> span, out long compressedSize, out long uncompressedSize)
    {
        var tagIndex = span.IndexOf(Zip64ExtraTag);
        if (tagIndex == -1)
            throw new InvalidDataException("ZIP64 file without extra field not supported.");
        var reader = new SpanReader(span[tagIndex..]);
        reader.Skip(4);
        uncompressedSize = (long) reader.ReadUInt64LittleEndian();
        compressedSize = (long) reader.ReadUInt64LittleEndian();
    }

    private static (
        uint Crc32,
        long CompressedSize,
        long UncompressedSize,
        ushort FileNameLength,
        ushort ExtraFieldLength,
        bool dataDescriptor) ParseMinimumLocalFileHeader(ReadOnlySpan<byte> span)
    {
        var reader = new SpanReader(span);

        if (!reader.ReadFixedValue(LocalFileHeader))
            throw new InvalidDataException("The stream is not a .zip archive having no bytes around file entries.");

        // version needed to extract (2 bytes)
        var version = reader.ReadUInt16LittleEndian();
        if (version > 45)
            throw new NotSupportedException("Zip format versions greater than 4.5 have not been tested.");

        var dataDescriptor = false;
        // general purpose bit flag: (2 bytes)
        var flags = reader.ReadUInt16LittleEndian();
        // 0x08 - Bit 3
        // If this bit is set, the fields crc-32, compressed 
        // size and uncompressed size are set to zero in the 
        // local header.  The correct values are put in the 
        // data descriptor immediately following the compressed
        // data.  (Note: PKZIP version 2.04g for DOS only 
        // recognizes this bit for method 8 compression, newer 
        // versions of PKZIP recognize this bit for any 
        // compression method.)
        if (flags == 0x08)
            dataDescriptor = true;
        else if (flags != 0)
            throw new NotImplementedException("Unknown flags");

        var compressionMethod = reader.ReadUInt16LittleEndian();

        // Last modified time and date (use 0x5455 "extended timestamp" extra field extension if possible first)
        reader.Skip(4);

        var crc32 = reader.ReadUInt32LittleEndian();
        var compressedSize = (long) reader.ReadUInt32LittleEndian();
        var uncompressedSize = (long) reader.ReadUInt32LittleEndian();
        var fileNameLength = reader.ReadUInt16LittleEndian();
        var extraFieldLength = reader.ReadUInt16LittleEndian();

        if (compressedSize > 0 && compressionMethod != 8)
            throw new NotSupportedException("Unsupported compression method");

        return (crc32, compressedSize, uncompressedSize, fileNameLength, extraFieldLength, dataDescriptor);
    }

    private static async ValueTask<int> ReadBlockAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalBytesRead = 0;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0) break;

            totalBytesRead += bytesRead;

            if (bytesRead == buffer.Length) break;

            buffer = buffer[bytesRead..];
        }

        return totalBytesRead;
    }

    public StreamingZipEntry CurrentEntry => currentEntry
        ?? throw new InvalidOperationException($"{nameof(CurrentEntry)} can only be used after {nameof(MoveToNextEntryAsync)} returns true.");

    public Stream GetCurrentEntryStream()
    {
        if (currentEntry is null)
            throw new InvalidOperationException($"{nameof(GetCurrentEntryStream)} can only be used after {nameof(MoveToNextEntryAsync)} returns true.");

        if (currentDeflateStream is null)
        {
            var compressedLength = currentEntry.DataDescriptor ? long.MaxValue : currentEntry.CompressedLength;
            currentSubStream = new SubStream(stream!, compressedLength);
            currentDeflateStream = new DeflateStream(currentSubStream, CompressionMode.Decompress);
        }

        return currentDeflateStream;
    }
}