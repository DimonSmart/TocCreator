using System.Text;
using System.Text.RegularExpressions;

namespace TocCreator;

public sealed record StructuralRecoveryRepairOptions(int MaxCycles = 2, int ContextSize = 2, int MaxSemanticAttempts = 2)
{
    public void Validate()
    {
        if (MaxCycles < 0 || ContextSize < 0 || MaxSemanticAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxCycles));
    }
}

public sealed record StructuralValidationFinding(string PointerId, string Code, string Description);

/// <summary>Deterministic checks over completed annotations. Findings name only source pointers that can be locally reviewed.</summary>
public sealed class StructuralRecoveryValidator
{
    private static readonly Regex HeadingNumber = new(@"^\s*(?<number>\d+(?:\.\d+){0,2})\b", RegexOptions.CultureInvariant);

    public IReadOnlyList<StructuralValidationFinding> Validate(SourceSnapshot snapshot, StructuralScanState state)
    {
        var findings = new List<StructuralValidationFinding>();
        var pointers = snapshot.Pointers.Select((pointer, index) => (pointer, index)).ToDictionary(item => item.pointer.Id, item => item.index, StringComparer.Ordinal);
        var annotations = state.AcceptedAnnotations;
        foreach (var group in annotations.GroupBy(annotation => annotation.PointerId, StringComparer.Ordinal).Where(group => group.Count() > 1))
            findings.Add(new(group.Key, "duplicate-annotation", "More than one annotation targets this pointer."));
        foreach (var annotation in annotations)
        {
            if (!pointers.ContainsKey(annotation.PointerId))
            {
                findings.Add(new(annotation.PointerId, "unknown-annotation-pointer", "An annotation refers to a pointer outside the source snapshot."));
                continue;
            }
            try { annotation.Validate(); }
            catch (ArgumentException) { findings.Add(new(annotation.PointerId, "annotation-consistency", "The annotation role and heading level are inconsistent.")); }
        }

        var byId = annotations.GroupBy(annotation => annotation.PointerId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var orderedHeadings = byId.Values.Where(annotation => annotation.Role == StructuralRole.Heading)
            .Where(annotation => pointers.ContainsKey(annotation.PointerId)).OrderBy(annotation => pointers[annotation.PointerId]).ToArray();
        var priorLevel = 0;
        foreach (var heading in orderedHeadings)
        {
            var level = heading.HeadingLevel!.Value;
            if (priorLevel == 0 && level != 1 || priorLevel > 0 && level > priorLevel + 1)
                findings.Add(new(heading.PointerId, "heading-hierarchy", "Heading level skips a required ancestor."));
            priorLevel = level;
        }

        foreach (var heading in orderedHeadings)
        {
            var number = HeadingNumber.Match(Text(snapshot, pointers[heading.PointerId]));
            if (number.Success && number.Groups["number"].Value.Count(character => character == '.') + 1 != heading.HeadingLevel)
                findings.Add(new(heading.PointerId, "heading-numbering", "The hierarchical number and annotation level disagree."));
        }

        var tocIds = TocPointerIds(snapshot, state);
        foreach (var heading in orderedHeadings.Where(heading => tocIds.Contains(heading.PointerId)))
            findings.Add(new(heading.PointerId, "toc-body-separation", "A table-of-contents pointer is annotated as a body heading."));

        var matchedToc = new Dictionary<int, string>();
        var tocIndex = -1;
        foreach (var heading in orderedHeadings)
        {
            var matches = state.TocEntries.Select((entry, index) => (entry, index))
                .Where(value => Normalize(value.entry.Title) == Normalize(Text(snapshot, pointers[heading.PointerId]))).ToArray();
            if (matches.Length > 1) findings.Add(new(heading.PointerId, "toc-duplicate-mapping", "A body heading maps to more than one TOC entry."));
            if (matches.Length == 1)
            {
                if (matchedToc.TryGetValue(matches[0].index, out _)) findings.Add(new(heading.PointerId, "toc-duplicate-mapping", "More than one body heading maps to this TOC entry."));
                if (matches[0].index < tocIndex) findings.Add(new(heading.PointerId, "toc-order", "Body headings map to TOC entries out of order."));
                matchedToc[matches[0].index] = heading.PointerId;
                tocIndex = Math.Max(tocIndex, matches[0].index);
            }
        }

        foreach (var repeated in orderedHeadings.GroupBy(heading => Normalize(Text(snapshot, pointers[heading.PointerId])), StringComparer.Ordinal)
                     .Where(group => group.Key.Length > 0 && group.Count() > 1))
            foreach (var heading in repeated.Skip(1))
                findings.Add(new(heading.PointerId, "repeated-running-header", "A repeated heading may be a running header rather than structure."));

        return findings.DistinctBy(finding => (finding.PointerId, finding.Code)).ToArray();
    }

    private static HashSet<string> TocPointerIds(SourceSnapshot snapshot, StructuralScanState state)
    {
        var annotations = state.AcceptedAnnotations.Where(annotation => annotation.Role == StructuralRole.Toc).Select(annotation => annotation.PointerId);
        return annotations.ToHashSet(StringComparer.Ordinal);
    }
    internal static string Text(SourceSnapshot snapshot, int index)
    {
        var pointer = snapshot.Pointers[index];
        return Encoding.UTF8.GetString(snapshot.GetSourceBytes(), checked((int)pointer.Span.ByteStart), pointer.Span.ByteLength).Trim();
    }
    internal static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>Reviews only validator-reported pointers and can only remove their local annotations.</summary>
public sealed class StructuralRecoveryRepairWorkflow(StructuralRecoveryValidator validator)
{
    public async Task<StructuralScanState> ValidateAndRepairAsync(
        SourceSnapshot snapshot, StructuralScanState state, StructuralRecoveryRepairOptions options,
        ISemanticOperationExecutor<ValidatedAnomalyReviewRequest, ValidatedAnomalyReviewResult>? executor,
        StructuralScanStateStore store, string statePath, ISemanticOperationDiagnosticSink? diagnostics,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        for (var cycle = state.RepairCyclesCompleted; cycle < options.MaxCycles; cycle++)
        {
            var findings = validator.Validate(snapshot, state);
            if (findings.Count == 0) return state;
            var known = snapshot.Pointers.Select(pointer => pointer.Id).ToHashSet(StringComparer.Ordinal);
            var reviewable = findings.Where(finding => known.Contains(finding.PointerId)).ToArray();
            var nonReviewable = findings.Except(reviewable).ToArray();
            if (nonReviewable.Length != 0) state = WithWarnings(state, nonReviewable, "This finding has no eligible local source range.");
            if (executor is null) return WithWarnings(state, reviewable, "No anomaly-review executor is configured.");

            var repaired = false;
            foreach (var range in reviewable.GroupBy(finding => finding.PointerId, StringComparer.Ordinal))
            {
                var writable = range.Select(finding => ToPointer(snapshot, finding.PointerId)).ToArray();
                var request = new ValidatedAnomalyReviewRequest(new SemanticOperationContext(Context(snapshot, writable, options.ContextSize), writable),
                    range.Select(finding => new ValidatedAnomaly(finding.PointerId, finding.Code, finding.Description)).ToArray());
                try
                {
                    var result = await new ValidatedSemanticOperation<ValidatedAnomalyReviewRequest, ValidatedAnomalyReviewResult>(
                        SemanticOperationKind.ValidatedAnomalyReview, "structural-recovery-repair", value => value.Context, SemanticOperationResultValidator.AnomalyReview)
                        .ExecuteAsync(request, executor, options.MaxSemanticAttempts, diagnostics, cancellationToken);
                    var dismissed = result.Reviews.Where(review => review.Disposition == AnomalyReviewDisposition.Dismiss)
                        .Select(review => review.PointerId).ToHashSet(StringComparer.Ordinal);
                    if (dismissed.Count != 0)
                    {
                        // The validator and typed result both constrain this to the reported writable range.
                        state = state with { AcceptedAnnotations = state.AcceptedAnnotations.Where(annotation => !dismissed.Contains(annotation.PointerId)).ToArray() };
                        repaired = true;
                    }
                }
                catch (InvalidDataException)
                {
                    state = state with { Warnings = state.Warnings.Append(new ScanWarning("repair-unresolved", $"Repair review failed for {range.Key}." )).ToArray() };
                }
            }
            state = state with { RepairCyclesCompleted = cycle + 1 };
            await store.SaveAsync(statePath, state, cancellationToken);
            if (!repaired) return WithWarnings(state, validator.Validate(snapshot, state), "No eligible local repair was applied.");
        }
        return WithWarnings(state, validator.Validate(snapshot, state), "The configured repair-cycle limit was reached.");
    }

    private static StructuralScanState WithWarnings(StructuralScanState state, IReadOnlyList<StructuralValidationFinding> findings, string reason) => state with
    {
        Warnings = state.Warnings.Concat(findings.Select(finding => new ScanWarning($"validation-{finding.Code}", $"{finding.Description} {reason}")))
            .DistinctBy(warning => (warning.Code, warning.Message)).ToArray()
    };
    private static SemanticPointer ToPointer(SourceSnapshot snapshot, string id)
    {
        var index = snapshot.Pointers.ToList().FindIndex(pointer => pointer.Id == id);
        if (index < 0) throw new ArgumentException($"Unknown validation pointer '{id}'.");
        return new SemanticPointer(id, StructuralRecoveryValidator.Text(snapshot, index), index);
    }
    private static IReadOnlyList<SemanticPointer> Context(SourceSnapshot snapshot, IReadOnlyList<SemanticPointer> writable, int size)
    {
        var indexes = writable.Select(pointer => pointer.GlobalPosition).ToHashSet();
        return writable.SelectMany(pointer => Enumerable.Range(Math.Max(0, pointer.GlobalPosition - size), Math.Min(snapshot.Pointers.Count - 1, pointer.GlobalPosition + size) - Math.Max(0, pointer.GlobalPosition - size) + 1))
            .Where(index => !indexes.Contains(index)).Distinct().OrderBy(index => index).Select(index => new SemanticPointer(snapshot.Pointers[index].Id, StructuralRecoveryValidator.Text(snapshot, index), index)).ToArray();
    }
}
