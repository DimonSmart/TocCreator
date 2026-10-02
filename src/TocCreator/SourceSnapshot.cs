using System.Text;

namespace TocCreator;

public sealed record SourceSpan(long ByteStart, int ByteLength)
{
    public long ByteEnd => ByteStart + ByteLength;
}

public sealed record TextPointer(string Id, SourceSpan Span, int? SourcePage = null);

public sealed record SourceSnapshot(string SourceBase64, IReadOnlyList<TextPointer> Pointers)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public byte[] GetSourceBytes() => Convert.FromBase64String(SourceBase64);

    public string GetSourceText() => Utf8.GetString(GetSourceBytes());
}

public sealed class SourceImporter
{
    public SourceSnapshot Import(ReadOnlySpan<byte> sourceBytes)
    {
        // Extracted text is required to be valid UTF-8 so byte offsets stay addressable.
        _ = new UTF8Encoding(false, true).GetString(sourceBytes);

        var pointers = new List<TextPointer>();
        long start = 0;
        var ordinal = 1;

        for (var index = 0; index < sourceBytes.Length; index++)
        {
            if (sourceBytes[index] != (byte)'\n')
            {
                continue;
            }

            pointers.Add(CreatePointer(ordinal++, start, checked(index + 1 - (int)start)));
            start = index + 1L;
        }

        if (start < sourceBytes.Length)
        {
            pointers.Add(CreatePointer(ordinal, start, checked(sourceBytes.Length - (int)start)));
        }

        return new SourceSnapshot(Convert.ToBase64String(sourceBytes), pointers);
    }

    private static TextPointer CreatePointer(int ordinal, long start, int length) =>
        new($"p{ordinal:D8}", new SourceSpan(start, length));
}
