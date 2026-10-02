using System.Text.Json;

namespace TocCreator;

public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task SaveAsync(string path, SourceSnapshot snapshot, CancellationToken cancellationToken = default) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);

    public async Task<SourceSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        JsonSerializer.Deserialize<SourceSnapshot>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
        ?? throw new InvalidDataException("Snapshot file is empty or invalid.");
}

public sealed class AnnotationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task SaveAsync(string path, AnnotationSet annotations, CancellationToken cancellationToken = default) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(annotations.Items, JsonOptions), cancellationToken);

    public async Task<AnnotationSet> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var annotations = JsonSerializer.Deserialize<List<StructuralAnnotation>>(
            await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("Annotation file is empty or invalid.");
        return new AnnotationSet(annotations);
    }
}
