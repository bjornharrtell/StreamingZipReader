using System.IO.Compression;

namespace Wololo.StreamingZipReader.UnitTests;

public class StreamingZipReaderTests
{
    [Fact]
    public async Task BasicTest()
    {
        var directoryInfo = Directory.CreateTempSubdirectory();
        var stream = new MemoryStream();
        ZipFile.CreateFromDirectory(directoryInfo.FullName, stream);
        stream.Position = 0;
        var zipArchive = new ZipArchive(stream, ZipArchiveMode.Create, true);
        var entry = zipArchive.CreateEntry("test");
        var writeStream = entry.Open();
        byte[] bytes = new byte[1024];
        Random.Shared.NextBytes(bytes);
        writeStream.Write(bytes);
        writeStream.Dispose();
        zipArchive.Dispose();
        stream.Position = 0;
        var reader = new StreamingZipReader(stream);
        await reader.MoveToNextEntryAsync(true, CancellationToken.None);
        Assert.Equal((uint) 1024, reader.CurrentEntry.Length);
    }
}