using TokenStatus.Infrastructure.Codex;

namespace TokenStatus.Infrastructure.Tests;

public sealed class BoundedAsyncLineReaderTests
{
    [Fact]
    public async Task ReadsLfAndCrLfLinesAndRetainsFollowingMessages()
    {
        var reader = new BoundedAsyncLineReader(new ChunkedTextReader("one\ntwo\r\n", 3), 32, 4);

        Assert.Equal("one", await reader.ReadBoundedLineAsync());
        Assert.Equal("two", await reader.ReadBoundedLineAsync());
        Assert.Null(await reader.ReadBoundedLineAsync());
    }

    [Fact]
    public async Task ReadsAnUnterminatedFinalLine()
    {
        var reader = new BoundedAsyncLineReader(new StringReader("final"), 32, 8);

        Assert.Equal("final", await reader.ReadBoundedLineAsync());
        Assert.Null(await reader.ReadBoundedLineAsync());
    }

    [Fact]
    public async Task RejectsAnOversizedLineBeforeWaitingForEof()
    {
        var source = new ChunkedTextReader("123456789", 4);
        var reader = new BoundedAsyncLineReader(source, 8, 4);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadBoundedLineAsync().AsTask());

        Assert.False(source.ReadPastEndOfInput);
    }

    [Fact]
    public async Task CancellationInterruptsAReadInProgress()
    {
        var source = new BlockingTextReader();
        var reader = new BoundedAsyncLineReader(source, 32);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadBoundedLineAsync(cancellation.Token).AsTask());
    }

    private sealed class ChunkedTextReader(string text, int chunkSize) : TextReader
    {
        private readonly string _text = text;
        private int _position;

        public bool ReadPastEndOfInput { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            if (_position >= _text.Length)
            {
                ReadPastEndOfInput = true;
                return 0;
            }

            var length = Math.Min(Math.Min(count, chunkSize), _text.Length - _position);
            _text.CopyTo(_position, buffer, index, length);
            _position += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = new char[Math.Min(buffer.Length, chunkSize)];
            var read = Read(temporary, 0, temporary.Length);
            temporary.AsSpan(0, read).CopyTo(buffer.Span);
            return ValueTask.FromResult(read);
        }
    }

    private sealed class BlockingTextReader : TextReader
    {
        public override int Read(char[] buffer, int index, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
