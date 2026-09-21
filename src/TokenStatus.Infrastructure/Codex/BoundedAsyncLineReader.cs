using System.Text;

namespace TokenStatus.Infrastructure.Codex;

/// <summary>
/// Reads newline-delimited text without allowing an unterminated line to grow
/// beyond the configured limit. The underlying reader is consumed in reusable
/// chunks, and characters after a newline remain in the chunk for the next call.
/// </summary>
public sealed class BoundedAsyncLineReader
{
    private readonly TextReader _reader;
    private readonly int _maximumLineCharacters;
    private readonly char[] _buffer;
    private readonly StringBuilder _line;
    private int _offset;
    private int _count;

    public BoundedAsyncLineReader(
        TextReader reader,
        int maximumLineCharacters,
        int bufferSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        _reader = reader;
        _maximumLineCharacters = maximumLineCharacters;
        _buffer = new char[bufferSize];
        _line = new StringBuilder(
            Math.Min(maximumLineCharacters, 4096),
            maximumLineCharacters == int.MaxValue ? int.MaxValue : maximumLineCharacters + 1);
    }

    public async ValueTask<string?> ReadBoundedLineAsync(CancellationToken cancellationToken = default)
    {
        _line.Clear();

        while (true)
        {
            if (_offset >= _count)
            {
                _offset = 0;
                _count = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_count == 0)
                {
                    if (_line.Length == 0)
                    {
                        return null;
                    }

                    RemoveOptionalCarriageReturn();
                    return _line.ToString();
                }
            }

            while (_offset < _count)
            {
                var character = _buffer[_offset++];
                if (character == '\n')
                {
                    RemoveOptionalCarriageReturn();
                    return _line.ToString();
                }

                if (_line.Length > _maximumLineCharacters ||
                    (_line.Length == _maximumLineCharacters && character != '\r'))
                {
                    throw new InvalidDataException(
                        $"The input line exceeds the {_maximumLineCharacters} character limit.");
                }

                _line.Append(character);
            }
        }
    }

    private void RemoveOptionalCarriageReturn()
    {
        if (_line.Length > 0 && _line[^1] == '\r')
        {
            _line.Length--;
        }
    }
}
