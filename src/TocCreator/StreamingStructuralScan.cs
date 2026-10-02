using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TocCreator;

/// <summary>Configuration for a monotonic structural scan. Context pointers are never writable.</summary>
public sealed record StructuralScanOptions(int DecisionZoneSize, int ContextSize = 4, int LookAheadSize = 4, int MaxSemanticAttempts = 2)
{
    public void Validate()
    {
        if (DecisionZoneSize < 1 || ContextSize < 0 || LookAheadSize < 0 || MaxSemanticAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(DecisionZoneSize), "Scan sizes must be non-negative and the decision zone and attempts must be positive.");
        }
    }
}

/// <summary>A window partitions pointer ownership: only DecisionPointerIds may be changed by its result.</summary>
public sealed record StructuralScanWindow(
    int DecisionStart,
    int DecisionEndExclusive,
    IReadOnlyList<string> PreviousContextPointerIds,
    IReadOnlyList<string> DecisionPointerIds,
    IReadOnlyList<string> LookAheadPointerIds)
{
    public bool IsInDecisionZone(string pointerId) => DecisionPointerIds.Contains(pointerId, StringComparer.Ordinal);
}

public sealed class StructuralScanWindowPlanner
{
    public StructuralScanWindow Create(SourceSnapshot snapshot, int cursor, StructuralScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options.Validate();
        if (cursor < 0 || cursor > snapshot.Pointers.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor));
        }

        var end = Math.Min(snapshot.Pointers.Count, cursor + options.DecisionZoneSize);
        var previousStart = Math.Max(0, cursor - options.ContextSize);
        var lookAheadEnd = Math.Min(snapshot.Pointers.Count, end + options.LookAheadSize);
        return new StructuralScanWindow(
            cursor,
            end,
            snapshot.Pointers.Skip(previousStart).Take(cursor - previousStart).Select(pointer => pointer.Id).ToArray(),
            snapshot.Pointers.Skip(cursor).Take(end - cursor).Select(pointer => pointer.Id).ToArray(),
            snapshot.Pointers.Skip(end).Take(lookAheadEnd - end).Select(pointer => pointer.Id).ToArray());
    }
}

public sealed record HeadingStackItem(string PointerId, int Level);
public sealed record DeferredBoundaryCandidate(string PointerId, string Reason);
public sealed record ScanWarning(string Code, string Message);

public enum StructuralRecoveryCheckpoint
{
    Scanning,
    StructuralValidation,
    Completed
}

/// <summary>Persisted application-owned memory. Cursor and committed boundary are pointer ordinals, not model state.</summary>
public sealed record StructuralScanState
{
    public required string SnapshotFingerprint { get; init; }
    public required int Cursor { get; init; }
    public required int CommittedBoundary { get; init; }
    public required IReadOnlyList<HeadingStackItem> ActiveHeadingStack { get; init; }
    public required IReadOnlyList<HeadingStackItem> RecentAcceptedHeadings { get; init; }
    public required IReadOnlyList<TocEntry> NextExpectedTocEntries { get; init; }
    public IReadOnlyList<TocEntry> TocEntries { get; init; } = [];
    public required IReadOnlyList<StructuralAnnotation> AcceptedAnnotations { get; init; }
    public required IReadOnlyList<DeferredBoundaryCandidate> DeferredCandidates { get; init; }
    public required IReadOnlyList<ScanWarning> Warnings { get; init; }
    public StructuralRecoveryCheckpoint Checkpoint { get; init; } = StructuralRecoveryCheckpoint.Scanning;
    public int RepairCyclesCompleted { get; init; }

    public static StructuralScanState Start(SourceSnapshot snapshot, StructuralDiscoveryResult discovery) => new()
    {
        SnapshotFingerprint = Fingerprint(snapshot),
        Cursor = 0,
        CommittedBoundary = -1,
        ActiveHeadingStack = [],
        RecentAcceptedHeadings = [],
        NextExpectedTocEntries = discovery.Entries.ToArray(),
        TocEntries = discovery.Entries.ToArray(),
        AcceptedAnnotations = discovery.Annotations.Items.ToArray(),
        DeferredCandidates = [],
        Warnings = [],
        Checkpoint = StructuralRecoveryCheckpoint.Scanning,
        RepairCyclesCompleted = 0
    };

    internal static string Fingerprint(SourceSnapshot snapshot)
    {
        var identity = string.Join("|", snapshot.Pointers.Select(pointer => $"{pointer.Id}:{pointer.Span.ByteStart}:{pointer.Span.ByteLength}:{pointer.SourcePage}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.SourceBase64 + "|" + identity)));
    }
}

public sealed record SemanticPointer(string PointerId, string Text, int GlobalPosition);

/// <summary>Small typed input appropriate for one Agent Framework agent executor; it intentionally has no provider dependency.</summary>
public sealed record StructuralDecisionRequest(
    IReadOnlyList<SemanticPointer> PreviousContext,
    IReadOnlyList<SemanticPointer> DecisionZone,
    IReadOnlyList<SemanticPointer> LookAhead,
    int GlobalDecisionStart,
    IReadOnlyList<HeadingStackItem> ActiveHeadingStack,
    IReadOnlyList<HeadingStackItem> RecentAcceptedHeadings,
    IReadOnlyList<TocEntry> NextExpectedTocEntries,
    IReadOnlyList<DeferredBoundaryCandidate> DeferredCandidates)
{
    public int CurrentHeadingLevel => ActiveHeadingStack.Count == 0 ? 0 : ActiveHeadingStack[^1].Level;
}

public enum StructuralDecisionKind { Annotate, Defer }

/// <summary>One local, auditable decision. Annotation is required only for Annotate.</summary>
public sealed record StructuralDecision(string PointerId, StructuralDecisionKind Kind, StructuralAnnotation? Annotation = null, string? Reason = null);
public sealed record StructuralDecisionResult(IReadOnlyList<StructuralDecision> Decisions);

public interface IStructuralDecisionExecutor
{
    Task<StructuralDecisionResult> DecideAsync(StructuralDecisionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Identity emitted with semantic diagnostics without coupling scan state to a provider.</summary>
public interface ISemanticDecisionIdentity
{
    string ProfileName { get; }
    string Model { get; }
}

/// <summary>Diagnostics are operational evidence, deliberately separate from structural annotations.</summary>
public sealed record SemanticDecisionDiagnostic(
    string Operation,
    string WorkflowStep,
    string? ProfileName,
    string? Model,
    IReadOnlyList<string> SuppliedPointerIds,
    IReadOnlyList<string> CandidateIds,
    StructuralDecisionResult? TypedResult,
    string? ValidationFailure,
    int RetryCount,
    TimeSpan Elapsed,
    string? RawPrompt = null,
    string? RawResponse = null);

public interface ISemanticDiagnosticSink
{
    void Record(SemanticDecisionDiagnostic diagnostic);
}

/// <summary>Raw model messages are excluded unless an operator explicitly enables this diagnostic option.</summary>
public sealed record SemanticDiagnosticOptions(bool RetainRawModelMessages = false);

public interface ISemanticRawTranscriptSource
{
    bool TryGetRawTranscript(out string? rawPrompt, out string? rawResponse);
}

public sealed class StructuralScanStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task SaveAsync(string path, StructuralScanState state, CancellationToken cancellationToken = default) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);

    public async Task<StructuralScanState> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        JsonSerializer.Deserialize<StructuralScanState>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
        ?? throw new InvalidDataException("Structural scan state file is empty or invalid.");
}

/// <summary>Application-owned workflow: it validates local results, evolves hierarchy, and commits only immutable completed ranges.</summary>
public sealed class StreamingStructuralScanner
{
    private readonly StructuralScanWindowPlanner windowPlanner;
    private readonly ILogger<StreamingStructuralScanner> logger;

    public StreamingStructuralScanner(StructuralScanWindowPlanner? windowPlanner = null, ILogger<StreamingStructuralScanner>? logger = null)
    {
        this.windowPlanner = windowPlanner ?? new StructuralScanWindowPlanner();
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamingStructuralScanner>.Instance;
    }

    public async Task<StructuralScanState> ScanNextAsync(SourceSnapshot snapshot, StructuralScanState state, StructuralScanOptions options, IStructuralDecisionExecutor executor, ISemanticDiagnosticSink? diagnostics = null, SemanticDiagnosticOptions? diagnosticOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(executor);
        options.Validate();
        ValidateState(snapshot, state);
        if (state.Cursor == snapshot.Pointers.Count)
        {
            return state;
        }

        var window = windowPlanner.Create(snapshot, state.Cursor, options);
        var request = CreateRequest(snapshot, state, window);
        StructuralDecisionResult? result = null;
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= options.MaxSemanticAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var candidate = await executor.DecideAsync(request, cancellationToken);
                ValidateResult(snapshot, window, candidate);
                result = candidate;
                RecordDiagnostic(diagnostics, diagnosticOptions, executor, request, candidate, null, attempt - 1, stopwatch.Elapsed);
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastFailure = exception;
                RecordDiagnostic(diagnostics, diagnosticOptions, executor, request, null, exception.Message, attempt - 1, stopwatch.Elapsed);
                logger.LogWarning(exception, "Structural decision attempt {Attempt} failed for pointers {Start} through {End}.", attempt, window.DecisionStart, window.DecisionEndExclusive - 1);
            }
        }

        if (result is null)
        {
            logger.LogWarning(lastFailure, "Structural decision fallback left pointers {Start} through {End} unresolved.", window.DecisionStart, window.DecisionEndExclusive - 1);
            return Commit(snapshot, state, window.DecisionEndExclusive - 1, [], [], new ScanWarning("semantic-unresolved", "Semantic decision retries were exhausted; no annotations were invented."));
        }

        return Apply(snapshot, state, window, result);
    }

    public async Task<StructuralScanState> ScanToCompletionAsync(SourceSnapshot snapshot, StructuralScanState state, StructuralScanOptions options, IStructuralDecisionExecutor executor, StructuralScanStateStore? store = null, string? statePath = null, ISemanticDiagnosticSink? diagnostics = null, SemanticDiagnosticOptions? diagnosticOptions = null, CancellationToken cancellationToken = default)
    {
        while (state.Cursor < snapshot.Pointers.Count)
        {
            var next = await ScanNextAsync(snapshot, state, options, executor, diagnostics, diagnosticOptions, cancellationToken);
            if (next.Cursor == state.Cursor)
            {
                throw new InvalidOperationException("Structural scan made no progress.");
            }
            state = next;
            if (store is not null && statePath is not null)
            {
                await store.SaveAsync(statePath, state, cancellationToken);
            }
        }
        return state;
    }

    private StructuralScanState Apply(SourceSnapshot snapshot, StructuralScanState state, StructuralScanWindow window, StructuralDecisionResult result)
    {
        ValidateResult(snapshot, window, result);
        var annotations = result.Decisions.Where(decision => decision.Kind == StructuralDecisionKind.Annotate).Select(decision => decision.Annotation!).ToList();
        var deferrals = result.Decisions.Where(decision => decision.Kind == StructuralDecisionKind.Defer).Select(decision => new DeferredBoundaryCandidate(decision.PointerId, decision.Reason ?? "Needs forward context.")).ToList();

        var firstDeferred = deferrals.Count == 0 ? window.DecisionEndExclusive : deferrals.Min(candidate => PointerIndex(snapshot, candidate.PointerId));
        var commitBoundary = firstDeferred - 1;
        // Decisions after the deferred boundary are not yet durable and must be reconsidered in their later primary window.
        annotations.RemoveAll(annotation => PointerIndex(snapshot, annotation.PointerId) > commitBoundary);
        return Commit(snapshot, state, commitBoundary, annotations, deferrals, null);
    }

    private static void ValidateResult(SourceSnapshot snapshot, StructuralScanWindow window, StructuralDecisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Decisions is null) throw new ArgumentException("A structural decision result must contain decisions.", nameof(result));
        var known = snapshot.Pointers.Select(pointer => pointer.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in result.Decisions)
        {
            if (!known.Contains(decision.PointerId)) throw new ArgumentException($"Decision refers to unknown pointer '{decision.PointerId}'.", nameof(result));
            if (!window.IsInDecisionZone(decision.PointerId)) throw new ArgumentException($"Decision for '{decision.PointerId}' is outside this window's decision zone.", nameof(result));
            if (!seen.Add(decision.PointerId)) throw new ArgumentException($"More than one decision was returned for '{decision.PointerId}'.", nameof(result));
            if (decision.Kind == StructuralDecisionKind.Annotate)
            {
                if (decision.Annotation is null || decision.Annotation.PointerId != decision.PointerId) throw new ArgumentException("An annotation decision must contain an annotation for the same pointer.", nameof(result));
                decision.Annotation.Validate();
            }
            else if (decision.Kind == StructuralDecisionKind.Defer)
            {
                if (decision.Annotation is not null) throw new ArgumentException("A deferred decision cannot carry an annotation.", nameof(result));
            }
            else throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    private static void RecordDiagnostic(ISemanticDiagnosticSink? sink, SemanticDiagnosticOptions? options, IStructuralDecisionExecutor executor, StructuralDecisionRequest request, StructuralDecisionResult? result, string? failure, int retryCount, TimeSpan elapsed)
    {
        if (sink is null) return;
        var identity = executor as ISemanticDecisionIdentity;
        string? rawPrompt = null;
        string? rawResponse = null;
        if (options?.RetainRawModelMessages == true && executor is ISemanticRawTranscriptSource transcriptSource)
        {
            transcriptSource.TryGetRawTranscript(out rawPrompt, out rawResponse);
        }
        sink.Record(new SemanticDecisionDiagnostic(
            "window-heading-classification",
            "streaming-structural-scan",
            identity?.ProfileName,
            identity?.Model,
            request.PreviousContext.Concat(request.DecisionZone).Concat(request.LookAhead).Select(pointer => pointer.PointerId).ToArray(),
            request.DecisionZone.Select(pointer => pointer.PointerId).ToArray(),
            result,
            failure,
            retryCount,
            elapsed,
            rawPrompt,
            rawResponse));
    }

    private StructuralScanState Commit(SourceSnapshot snapshot, StructuralScanState state, int boundary, IReadOnlyList<StructuralAnnotation> additions, IReadOnlyList<DeferredBoundaryCandidate> newDeferrals, ScanWarning? warning)
    {
        if (boundary < state.CommittedBoundary) throw new InvalidOperationException("A committed boundary cannot move backwards.");
        var byPointer = state.AcceptedAnnotations.ToDictionary(annotation => annotation.PointerId, StringComparer.Ordinal);
        foreach (var annotation in additions) byPointer[annotation.PointerId] = annotation;
        var headings = byPointer.Values
            .Where(annotation => annotation.Role == StructuralRole.Heading && PointerIndex(snapshot, annotation.PointerId) <= boundary)
            .OrderBy(annotation => PointerIndex(snapshot, annotation.PointerId)).ToArray();
        var stack = new List<HeadingStackItem>();
        foreach (var heading in headings) UpdateStack(stack, new HeadingStackItem(heading.PointerId, heading.HeadingLevel!.Value));
        var recent = headings.TakeLast(12).Select(heading => new HeadingStackItem(heading.PointerId, heading.HeadingLevel!.Value)).ToArray();
        var expected = AdvanceExpectedToc(state.NextExpectedTocEntries, byPointer.Values, snapshot, state.CommittedBoundary + 1, boundary);
        var deferred = state.DeferredCandidates.Concat(newDeferrals).Where(candidate => PointerIndex(snapshot, candidate.PointerId) > boundary).DistinctBy(candidate => candidate.PointerId, StringComparer.Ordinal).ToArray();
        var warnings = warning is null ? state.Warnings : state.Warnings.Append(warning).ToArray();
        var nextCursor = Math.Min(snapshot.Pointers.Count, boundary + 1);
        return state with { Cursor = nextCursor, CommittedBoundary = boundary, ActiveHeadingStack = stack, RecentAcceptedHeadings = recent, NextExpectedTocEntries = expected, AcceptedAnnotations = byPointer.Values.OrderBy(annotation => PointerIndex(snapshot, annotation.PointerId)).ToArray(), DeferredCandidates = deferred, Warnings = warnings };
    }

    private static StructuralDecisionRequest CreateRequest(SourceSnapshot snapshot, StructuralScanState state, StructuralScanWindow window)
    {
        var source = snapshot.GetSourceBytes();
        SemanticPointer Make(string id) { var index = PointerIndex(snapshot, id); var pointer = snapshot.Pointers[index]; return new SemanticPointer(id, Encoding.UTF8.GetString(source, (int)pointer.Span.ByteStart, pointer.Span.ByteLength).TrimEnd('\r', '\n'), index); }
        return new StructuralDecisionRequest(window.PreviousContextPointerIds.Select(Make).ToArray(), window.DecisionPointerIds.Select(Make).ToArray(), window.LookAheadPointerIds.Select(Make).ToArray(), window.DecisionStart, state.ActiveHeadingStack, state.RecentAcceptedHeadings, state.NextExpectedTocEntries.Take(8).ToArray(), state.DeferredCandidates);
    }

    private static void ValidateState(SourceSnapshot snapshot, StructuralScanState state)
    {
        if (state.SnapshotFingerprint != StructuralScanState.Fingerprint(snapshot)) throw new ArgumentException("Scan state belongs to a different source snapshot or segmentation configuration.", nameof(state));
        if (state.CommittedBoundary != state.Cursor - 1 || state.Cursor < 0 || state.Cursor > snapshot.Pointers.Count) throw new ArgumentException("State cursor and committed boundary are inconsistent.", nameof(state));
        var known = snapshot.Pointers.Select(pointer => pointer.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var annotation in state.AcceptedAnnotations)
        {
            if (!known.Contains(annotation.PointerId)) throw new ArgumentException($"State contains unknown pointer '{annotation.PointerId}'.", nameof(state));
            annotation.Validate();
        }
    }

    private static void UpdateStack(List<HeadingStackItem> stack, HeadingStackItem heading)
    {
        stack.RemoveAll(item => item.Level >= heading.Level);
        stack.Add(heading);
    }

    private static IReadOnlyList<TocEntry> AdvanceExpectedToc(IReadOnlyList<TocEntry> expected, IEnumerable<StructuralAnnotation> annotations, SourceSnapshot snapshot, int rangeStart, int rangeEnd)
    {
        var cursor = 0;
        foreach (var heading in annotations.Where(annotation => annotation.Role == StructuralRole.Heading)
                     .Where(annotation => PointerIndex(snapshot, annotation.PointerId) >= rangeStart && PointerIndex(snapshot, annotation.PointerId) <= rangeEnd)
                     .OrderBy(annotation => PointerIndex(snapshot, annotation.PointerId)))
        {
            if (cursor >= expected.Count) break;
            var headingText = PointerText(snapshot, heading.PointerId);
            if (Normalize(headingText) == Normalize(expected[cursor].Title)) cursor++;
        }
        return expected.Skip(cursor).ToArray();
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string PointerText(SourceSnapshot snapshot, string id) { var pointer = snapshot.Pointers[PointerIndex(snapshot, id)]; return Encoding.UTF8.GetString(snapshot.GetSourceBytes(), (int)pointer.Span.ByteStart, pointer.Span.ByteLength).Trim(); }
    private static int PointerIndex(SourceSnapshot snapshot, string pointerId) { for (var index = 0; index < snapshot.Pointers.Count; index++) if (snapshot.Pointers[index].Id == pointerId) return index; throw new ArgumentException($"Unknown pointer '{pointerId}'."); }
}
