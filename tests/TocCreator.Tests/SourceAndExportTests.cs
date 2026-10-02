using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TocCreator.Tests;

public sealed class SourceAndExportTests
{
    private readonly SourceImporter importer = new();
    private readonly MarkdownExporter exporter = new(NullLogger<MarkdownExporter>.Instance);

    [Fact]
    public void Import_preserves_source_bytes_and_pointers_reconstruct_them()
    {
        var source = Encoding.UTF8.GetBytes("A\r\nПривет\n\nB");
        var snapshot = importer.Import(source);

        Assert.Equal(source, snapshot.GetSourceBytes());
        Assert.Equal(source, Reconstruct(snapshot));
    }

    [Fact]
    public async Task Snapshot_and_annotations_are_persisted_separately()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshotPath = Path.Combine(root, "snapshot.json");
            var annotationPath = Path.Combine(root, "annotations.json");
            var snapshot = importer.Import(Encoding.UTF8.GetBytes("Heading\n"));
            var annotations = new AnnotationSet([
                new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Heading, HeadingLevel = 1 }
            ]);

            await new SnapshotStore().SaveAsync(snapshotPath, snapshot);
            await new AnnotationStore().SaveAsync(annotationPath, annotations);

            var restoredSnapshot = await new SnapshotStore().LoadAsync(snapshotPath);
            var restoredAnnotations = await new AnnotationStore().LoadAsync(annotationPath);
            Assert.Equal("Heading\n", restoredSnapshot.GetSourceText());
            Assert.Equal("# Heading\n", Encoding.UTF8.GetString(exporter.Export(restoredSnapshot, restoredAnnotations)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_segments_lines_deterministically_with_stable_ids()
    {
        var source = Encoding.UTF8.GetBytes("one\ntwo\nthree");

        var first = importer.Import(source);
        var second = importer.Import(source);

        Assert.Equal(["p00000001", "p00000002", "p00000003"], first.Pointers.Select(pointer => pointer.Id));
        Assert.Equal(first.Pointers, second.Pointers);
    }

    [Fact]
    public void Export_renders_annotated_headings_at_their_requested_level()
    {
        var snapshot = importer.Import(Encoding.UTF8.GetBytes("Chapter\nSection\nSubsection\n"));
        var annotations = new AnnotationSet([
            new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Heading, HeadingLevel = 1 },
            new StructuralAnnotation { PointerId = "p00000002", Role = StructuralRole.Heading, HeadingLevel = 2 },
            new StructuralAnnotation { PointerId = "p00000003", Role = StructuralRole.Heading, HeadingLevel = 3 }
        ]);

        Assert.Equal("# Chapter\n## Section\n### Subsection\n", Encoding.UTF8.GetString(exporter.Export(snapshot, annotations)));
    }

    [Fact]
    public void Export_omits_noise_without_mutating_the_snapshot()
    {
        var source = Encoding.UTF8.GetBytes("keep\ndrop\nkeep too\n");
        var snapshot = importer.Import(source);
        var annotations = new AnnotationSet([
            new StructuralAnnotation { PointerId = "p00000002", Role = StructuralRole.Noise }
        ]);

        Assert.Equal("keep\nkeep too\n", Encoding.UTF8.GetString(exporter.Export(snapshot, annotations)));
        Assert.Equal(source, snapshot.GetSourceBytes());
    }

    [Fact]
    public void Export_preserves_toc_by_default_and_is_repeatable()
    {
        var snapshot = importer.Import(Encoding.UTF8.GetBytes("Contents\n1. Start\nBody\n"));
        var annotations = new AnnotationSet([
            new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Toc },
            new StructuralAnnotation { PointerId = "p00000002", Role = StructuralRole.Toc }
        ]);

        var first = exporter.Export(snapshot, annotations);
        var second = exporter.Export(snapshot, annotations);

        Assert.Equal("Contents\n1. Start\nBody\n", Encoding.UTF8.GetString(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Annotation_rejects_invalid_heading_levels_and_levels_on_non_headings()
    {
        Assert.Throws<ArgumentException>(() => new AnnotationSet([
            new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Heading, HeadingLevel = 4 }
        ]));
        Assert.Throws<ArgumentException>(() => new AnnotationSet([
            new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Body, HeadingLevel = 1 }
        ]));
    }

    private static byte[] Reconstruct(SourceSnapshot snapshot)
    {
        var source = snapshot.GetSourceBytes();
        using var result = new MemoryStream();
        foreach (var pointer in snapshot.Pointers)
        {
            result.Write(source.AsSpan((int)pointer.Span.ByteStart, pointer.Span.ByteLength));
        }

        return result.ToArray();
    }
}
