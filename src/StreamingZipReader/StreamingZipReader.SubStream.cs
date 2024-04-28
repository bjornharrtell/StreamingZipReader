namespace Wololo.StreamingZipReader;

partial class StreamingZipReader
{
    private sealed class SubStream : Stream
    {
        private Stream? stream;
        private long position;

        public SubStream(Stream stream, long length)
        {
            this.stream = stream;
            Length = length;
        }

        public void Detach() => stream = null;

        private static Exception CreateDetachedStreamException()
        {
            return new InvalidOperationException($"A stream returned from {nameof(StreamingZipReader)}.{nameof(GetCurrentEntryStream)} may not be used after calling {nameof(StreamingZipReader)}.{nameof(MoveToNextEntryAsync)} again.");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var remainingLength = Length - Position;
            if (remainingLength == 0)
            {
                // This stream instance is at the end even though the inner stream may not be.
                return 0;
            }

            if (buffer.Length > remainingLength)
                buffer = buffer[..(int)remainingLength];

            if (stream is null) throw CreateDetachedStreamException();
            var byteCount = stream.Read(buffer);
            position += byteCount;
            return byteCount;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remainingLength = Length - Position;
            if (remainingLength == 0)
            {
                // This stream instance is at the end even though the inner stream may not be.
                return 0;
            }

            if (buffer.Length > remainingLength)
                buffer = buffer[..(int)remainingLength];

            if (stream is null) throw CreateDetachedStreamException();
            var byteCount = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            position += byteCount;
            return byteCount;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length { get; }

        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}