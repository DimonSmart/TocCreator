namespace TocCreator;

public enum StructuralRole
{
    Body,
    Heading,
    Toc,
    Noise
}

public sealed record StructuralAnnotation
{
    public required string PointerId { get; init; }
    public required StructuralRole Role { get; init; }
    public int? HeadingLevel { get; init; }

    public void Validate()
    {
        if (Role == StructuralRole.Heading && HeadingLevel is >= 1 and <= 3)
        {
            return;
        }

        if (Role != StructuralRole.Heading && HeadingLevel is null)
        {
            return;
        }

        throw new ArgumentException("Only headings have a level, and heading levels must be from 1 through 3.");
    }
}

public sealed class AnnotationSet
{
    private readonly IReadOnlyDictionary<string, StructuralAnnotation> byPointer;

    public AnnotationSet(IEnumerable<StructuralAnnotation> annotations)
    {
        var materialized = annotations.ToList();
        foreach (var annotation in materialized)
        {
            annotation.Validate();
        }

        byPointer = materialized.ToDictionary(annotation => annotation.PointerId, StringComparer.Ordinal);
    }

    public IReadOnlyList<StructuralAnnotation> Items => byPointer.Values.ToList();

    public StructuralAnnotation GetOrBody(string pointerId) =>
        byPointer.TryGetValue(pointerId, out var annotation)
            ? annotation
            : new StructuralAnnotation { PointerId = pointerId, Role = StructuralRole.Body };
}
