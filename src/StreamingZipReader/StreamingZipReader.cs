using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace Wololo.StreamingZipReader;

public sealed partial class StreamingZipReader : IAsyncDisposable
{
    private static readonly byte[] LocalFileHeader = [(byte)'P', (byte)'K', 3, 4];
    private static readonly byte[] CentralDirectoryHeader = [(byte)'P', (byte)'K', 1, 2];

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
            remainingLength = currentEntry.Value.CompressedLength;
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

        var (crc32, compressedSize, uncompressedSize, fileNameLength, extraFieldLength) = ParseMinimumLocalFileHeader(buffer.Span);

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
            currentEntry = new(fileName, crc32, compressedSize, uncompressedSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }

        return true;
    }

    private static (
        uint Crc32,
        uint CompressedSize,
        uint UncompressedSize,
        ushort FileNameLength,
        ushort ExtraFieldLength) ParseMinimumLocalFileHeader(ReadOnlySpan<byte> span)
    {
        var reader = new SpanReader(span);

        if (!reader.ReadFixedValue(LocalFileHeader))
            throw new InvalidDataException("The stream is not a .zip archive having no bytes around file entries.");

        var version = reader.ReadUInt16LittleEndian();
        if (version > 45)
            throw new NotSupportedException("Zip format versions greater than 4.5 have not been tested.");

        var flags = reader.ReadUInt16LittleEndian();
        if (flags != 0)
            throw new NotImplementedException("Unknown flags");

        var compressionMethod = reader.ReadUInt16LittleEndian();

        // Last modified time and date (use 0x5455 "extended timestamp" extra field extension if possible first)
        reader.Skip(4);

        var crc32 = reader.ReadUInt32LittleEndian();
        var compressedSize = reader.ReadUInt32LittleEndian();
        var uncompressedSize = reader.ReadUInt32LittleEndian();
        var fileNameLength = reader.ReadUInt16LittleEndian();
        var extraFieldLength = reader.ReadUInt16LittleEndian();

        if (compressedSize > 0 && compressionMethod != 8)
            throw new NotSupportedException("Unsupported compression method");

        return (crc32, compressedSize, uncompressedSize, fileNameLength, extraFieldLength);
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
            currentSubStream = new SubStream(stream!, currentEntry.Value.CompressedLength);
            currentDeflateStream = new DeflateStream(currentSubStream, CompressionMode.Decompress);
        }

        return currentDeflateStream;
    }
}