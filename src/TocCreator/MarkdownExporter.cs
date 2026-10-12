using System.Text;
using Microsoft.Extensions.Logging;

namespace TocCreator;

public sealed class MarkdownExporter(ILogger<MarkdownExporter> logger)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public byte[] Export(SourceSnapshot snapshot, AnnotationSet annotations)
    {
        var source = snapshot.GetSourceBytes();
        using var output = new MemoryStream();

        logger.LogInformation("Starting Markdown export for {PointerCount} pointers.", snapshot.Pointers.Count);
        foreach (var pointer in snapshot.Pointers)
        {
            var annotation = annotations.GetOrBody(pointer.Id);
            if (annotation.Role == StructuralRole.Noise)
            {
                continue;
            }

            if (annotation.Role == StructuralRole.Heading)
            {
                output.Write(Utf8.GetBytes(new string('#', annotation.HeadingLevel!.Value) + " "));
            }

            output.Write(source.AsSpan(checked((int)pointer.Span.ByteStart), pointer.Span.ByteLength));
        }

        var markdown = output.ToArray();
        logger.LogInformation("Completed Markdown export with {ByteCount} bytes.", markdown.Length);
        return markdown;
    }

    public byte[] ExportConfirmedHeadings(SourceSnapshot snapshot, AnnotationSet annotations)
    {
        var source = snapshot.GetSourceBytes();
        var output = new StringBuilder();

        foreach (var pointer in snapshot.Pointers)
        {
            var annotation = annotations.GetOrBody(pointer.Id);
            if (annotation.Role != StructuralRole.Heading)
            {
                continue;
            }

            var text = Utf8.GetString(source, checked((int)pointer.Span.ByteStart), pointer.Span.ByteLength)
                .Trim()
                .TrimStart('#')
                .TrimStart();
            output.Append('#', annotation.HeadingLevel!.Value)
                .Append(' ')
                .AppendLine(text);
        }

        return Utf8.GetBytes(output.ToString());
    }
}
