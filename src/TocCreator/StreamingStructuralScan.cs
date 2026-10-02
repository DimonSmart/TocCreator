using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TocCreator;

/// <summary>
/// Configuration for a monotonic structural scan. Character budgets are authoritative;
/// pointer counts are only safety caps.
/// </summary>
public sealed record StructuralScanOptions(
    int DecisionZoneTextBudget = 12_000,
    int PreviousContextTextBudget = 6_000,
    int LookAheadTextBudget = 6_000,
    int VerifierContextTextBudget = 6_000,
    int PerPointerTextBudget = 4_000,
    int MaxSemanticAttempts = 2,
    int MaxDecisionPointers = 128,
    int MaxContextPointers = 64)
{
    public void Validate()
    {
        if (DecisionZoneTextBudget < 1 ||
            PreviousContextTextBudget < 0 ||
            LookAheadTextBudget < 0 ||
            VerifierContextTextBudget < 0 ||
            PerPointerTextBudget < 1 ||
            MaxSemanticAttempts < 1 ||
            MaxDecisionPointers < 1 ||
            MaxContextPointers < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DecisionZoneTextBudget), "Scan text budgets and safety limits are invalid.");
        }
    }
}

/// <summary>A window partitions pointer ownership: only DecisionPointerIds belong to this primary window.</summary>
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

        var end = cursor;
        var decisionBudget = 0;
        while (end < snapshot.Pointers.Count && end - cursor < options.MaxDecisionPointers)
        {
            var length = VisibleLength(snapshot, end, options.PerPointerTextBudget);
            if (end > cursor && decisionBudget + length > options.DecisionZoneTextBudget)
            {
                break;
            }

            decisionBudget += length;
            end++;
        }

        // Progress is mandatory even when one bounded item is larger than the ordinary window budget.
        if (cursor < snapshot.Pointers.Count && end == cursor)
        {
            end++;
        }

        var previousStart = cursor;
        var previousBudget = 0;
        while (previousStart > 0 && cursor - previousStart < options.MaxContextPointers)
        {
            var index = previousStart - 1;
            var length = VisibleLength(snapshot, index, options.PerPointerTextBudget);
            if (previousBudget + length > options.PreviousContextTextBudget)
            {
                break;
            }

            previousBudget += length;
            previousStart = index;
        }

        var lookAheadEnd = end;
        var lookAheadBudget = 0;
        while (lookAheadEnd < snapshot.Pointers.Count && lookAheadEnd - end < options.MaxContextPointers)
        {
            var length = VisibleLength(snapshot, lookAheadEnd, options.PerPointerTextBudget);
            if (lookAheadBudget + length > options.LookAheadTextBudget)
            {
                break;
            }

            lookAheadBudget += length;
            lookAheadEnd++;
        }

        return new StructuralScanWindow(
            cursor,
            end,
            snapshot.Pointers.Skip(previousStart).Take(cursor - previousStart).Select(pointer => pointer.Id).ToArray(),
            snapshot.Pointers.Skip(cursor).Take(end - cursor).Select(pointer => pointer.Id).ToArray(),
            snapshot.Pointers.Skip(end).Take(lookAheadEnd - end).Select(pointer => pointer.Id).ToArray());
    }

    private static int VisibleLength(SourceSnapshot snapshot, int index, int perPointerBudget) =>
        Math.Min(StreamingStructuralScanner.PointerText(snapshot, index).Length, perPointerBudget);
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
        var identity = string.Join("|", snapshot.Pointers.Select(pointer =>
            $"{pointer.Id}:{pointer.Span.ByteStart}:{pointer.Span.ByteLength}:{pointer.SourcePage}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.SourceBase64 + "|" + identity)));
    }
}

/// <summary>Pointer-bearing local context used by older non-heading semantic operations and application diagnostics.</summary>
public sealed record SemanticPointer(string PointerId, string Text, int GlobalPosition);

public sealed class SemanticWindowFailureException : InvalidDataException
{
    public SemanticWindowFailureException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Diagnostics are operational evidence and never become structural annotations.</summary>
public sealed record SemanticDecisionDiagnostic(
    string Operation,
    string WorkflowStep,
    string? ProfileName,
    string? Model,
    string PrimaryWindow,
    string? ApplicationCandidatePointerId,
    IReadOnlyList<int> SelectableCandidateNumbers,
    object? TypedResult,
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

/// <summary>
/// Application-owned two-stage workflow: the model proposes and verifies local hypotheses;
/// the application owns pointer identity, hierarchy, validation and window commit.
/// </summary>
public sealed class StreamingStructuralScanner
{
    private readonly StructuralScanWindowPlanner windowPlanner;
    private readonly ILogger<StreamingStructuralScanner> logger;

    public StreamingStructuralScanner(
        StructuralScanWindowPlanner? windowPlanner = null,
        ILogger<StreamingStructuralScanner>? logger = null)
    {
        this.windowPlanner = windowPlanner ?? new StructuralScanWindowPlanner();
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamingStructuralScanner>.Instance;
    }

    public async Task<StructuralScanState> ScanNextAsync(
        SourceSnapshot snapshot,
        StructuralScanState state,
        StructuralScanOptions options,
        HeadingScanSemanticExecutors executors,
        ISemanticDiagnosticSink? diagnostics = null,
        SemanticDiagnosticOptions? diagnosticOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(executors);
        options.Validate();
        ValidateState(snapshot, state);
        if (state.Cursor == snapshot.Pointers.Count)
        {
            return state;
        }

        var window = windowPlanner.Create(snapshot, state.Cursor, options);
        var knownAnnotations = state.AcceptedAnnotations.ToDictionary(annotation => annotation.PointerId, StringComparer.Ordinal);
        var detectorRequest = CreateDetectorRequest(snapshot, state, window, knownAnnotations, options);
        var detectorResult = await DetectCandidatesAsync(
            detectorRequest,
            executors.CandidateDetection,
            snapshot,
            window,
            options,
            diagnostics,
            diagnosticOptions,
            cancellationToken);

        var candidateNumbers = detectorResult.Candidates.OrderBy(number => number).ToArray();
        var candidateSet = candidateNumbers.ToHashSet();
        var temporaryStack = state.ActiveHeadingStack.ToList();
        var additions = new List<StructuralAnnotation>();
        var warnings = new List<ScanWarning>();

        for (var localIndex = 0; localIndex < window.DecisionPointerIds.Count; localIndex++)
        {
            var pointerId = window.DecisionPointerIds[localIndex];
            if (knownAnnotations.TryGetValue(pointerId, out var known) && known.Role == StructuralRole.Heading)
            {
                UpdateStack(temporaryStack, new HeadingStackItem(pointerId, known.HeadingLevel!.Value));
            }

            var localNumber = localIndex + 1;
            if (!candidateSet.Contains(localNumber))
            {
                continue;
            }

            var globalIndex = window.DecisionStart + localIndex;
            var verifierRequest = CreateVerifierRequest(snapshot, globalIndex, temporaryStack, options);
            var verifierResult = await VerifyCandidateAsync(
                verifierRequest,
                executors.CandidateVerification,
                pointerId,
                detectorRequest.Paragraphs.Where(paragraph => paragraph.Selectable).Select(paragraph => paragraph.Number).ToArray(),
                window,
                options,
                diagnostics,
                diagnosticOptions,
                cancellationToken);

            if (verifierResult is null)
            {
                warnings.Add(new ScanWarning(
                    "unresolved-verification",
                    $"Heading verification retries were exhausted for pointer '{pointerId}'."));
                continue;
            }

            if (!verifierResult.Accept)
            {
                continue;
            }

            var annotation = new StructuralAnnotation
            {
                PointerId = pointerId,
                Role = StructuralRole.Heading,
                HeadingLevel = verifierResult.Level!.Value
            };
            additions.Add(annotation);
            UpdateStack(temporaryStack, new HeadingStackItem(pointerId, annotation.HeadingLevel.Value));
        }

        return Commit(snapshot, state, window.DecisionEndExclusive - 1, additions, warnings);
    }

    public async Task<StructuralScanState> ScanToCompletionAsync(
        SourceSnapshot snapshot,
        StructuralScanState state,
        StructuralScanOptions options,
        HeadingScanSemanticExecutors executors,
        StructuralScanStateStore? store = null,
        string? statePath = null,
        ISemanticDiagnosticSink? diagnostics = null,
        SemanticDiagnosticOptions? diagnosticOptions = null,
        CancellationToken cancellationToken = default)
    {
        while (state.Cursor < snapshot.Pointers.Count)
        {
            var next = await ScanNextAsync(
                snapshot,
                state,
                options,
                executors,
                diagnostics,
                diagnosticOptions,
                cancellationToken);

            if (next.Cursor <= state.Cursor)
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

    private async Task<HeadingCandidateDetectionResult> DetectCandidatesAsync(
        HeadingCandidateDetectionRequest request,
        ISemanticOperationExecutor<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult> executor,
        SourceSnapshot snapshot,
        StructuralScanWindow window,
        StructuralScanOptions options,
        ISemanticDiagnosticSink? diagnostics,
        SemanticDiagnosticOptions? diagnosticOptions,
        CancellationToken cancellationToken)
    {
        Exception? finalFailure = null;
        var selectable = request.Paragraphs.Where(paragraph => paragraph.Selectable).Select(paragraph => paragraph.Number).ToArray();

        for (var attempt = 0; attempt < options.MaxSemanticAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            HeadingCandidateDetectionResult? result = null;
            try
            {
                result = await executor.DecideAsync(request, cancellationToken);
                HeadingSemanticResultValidator.CandidateDetection(request, result);
                RecordDiagnostic(
                    diagnostics,
                    diagnosticOptions,
                    executor,
                    "window-heading-candidate-detection",
                    window,
                    null,
                    selectable,
                    result,
                    null,
                    attempt,
                    stopwatch.Elapsed);
                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                finalFailure = exception;
                RecordDiagnostic(
                    diagnostics,
                    diagnosticOptions,
                    executor,
                    "window-heading-candidate-detection",
                    window,
                    null,
                    selectable,
                    result,
                    exception.Message,
                    attempt,
                    stopwatch.Elapsed);
                logger.LogWarning(
                    exception,
                    "Heading candidate detection attempt {Attempt} failed for window {Start} through {End}.",
                    attempt + 1,
                    window.DecisionStart,
                    window.DecisionEndExclusive - 1);
            }
        }

        throw new SemanticWindowFailureException(
            $"Heading candidate detection failed for uncommitted window {window.DecisionStart}..{window.DecisionEndExclusive - 1}; committed boundary was not advanced.",
            finalFailure);
    }

    private async Task<HeadingCandidateVerificationResult?> VerifyCandidateAsync(
        HeadingCandidateVerificationRequest request,
        ISemanticOperationExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult> executor,
        string candidatePointerId,
        IReadOnlyList<int> selectableNumbers,
        StructuralScanWindow window,
        StructuralScanOptions options,
        ISemanticDiagnosticSink? diagnostics,
        SemanticDiagnosticOptions? diagnosticOptions,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < options.MaxSemanticAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            HeadingCandidateVerificationResult? result = null;
            try
            {
                result = await executor.DecideAsync(request, cancellationToken);
                HeadingSemanticResultValidator.CandidateVerification(request, result);
                RecordDiagnostic(
                    diagnostics,
                    diagnosticOptions,
                    executor,
                    "heading-candidate-verification",
                    window,
                    candidatePointerId,
                    selectableNumbers,
                    result,
                    null,
                    attempt,
                    stopwatch.Elapsed);
                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordDiagnostic(
                    diagnostics,
                    diagnosticOptions,
                    executor,
                    "heading-candidate-verification",
                    window,
                    candidatePointerId,
                    selectableNumbers,
                    result,
                    exception.Message,
                    attempt,
                    stopwatch.Elapsed);
                logger.LogWarning(
                    exception,
                    "Heading verification attempt {Attempt} failed for candidate {PointerId}.",
                    attempt + 1,
                    candidatePointerId);
            }
        }

        return null;
    }

    private static HeadingCandidateDetectionRequest CreateDetectorRequest(
        SourceSnapshot snapshot,
        StructuralScanState state,
        StructuralScanWindow window,
        IReadOnlyDictionary<string, StructuralAnnotation> knownAnnotations,
        StructuralScanOptions options)
    {
        var before = window.PreviousContextPointerIds
            .Select(id => ContextParagraph(snapshot, PointerIndex(snapshot, id), options.PerPointerTextBudget))
            .ToArray();
        var after = window.LookAheadPointerIds
            .Select(id => ContextParagraph(snapshot, PointerIndex(snapshot, id), options.PerPointerTextBudget))
            .ToArray();

        var paragraphs = window.DecisionPointerIds.Select((id, index) =>
        {
            var globalIndex = window.DecisionStart + index;
            var representation = BoundedPointerText(snapshot, globalIndex, options.PerPointerTextBudget);
            knownAnnotations.TryGetValue(id, out var known);
            return new HeadingDecisionParagraph(
                index + 1,
                representation.Text,
                known is null,
                known?.Role.ToString().ToLowerInvariant(),
                known?.HeadingLevel,
                globalIndex,
                representation.Truncated,
                snapshot.Pointers[globalIndex].SourcePage);
        }).ToArray();

        return new HeadingCandidateDetectionRequest(
            Breadcrumbs(snapshot, state.ActiveHeadingStack, options.PerPointerTextBudget),
            before,
            paragraphs,
            after,
            snapshot.Pointers.Count);
    }

    private static HeadingCandidateVerificationRequest CreateVerifierRequest(
        SourceSnapshot snapshot,
        int candidateIndex,
        IReadOnlyList<HeadingStackItem> temporaryStack,
        StructuralScanOptions options)
    {
        var candidate = BoundedPointerText(snapshot, candidateIndex, options.PerPointerTextBudget);
        var beforeBudget = options.VerifierContextTextBudget / 2;
        var afterBudget = options.VerifierContextTextBudget - beforeBudget;

        return new HeadingCandidateVerificationRequest(
            Breadcrumbs(snapshot, temporaryStack, options.PerPointerTextBudget),
            candidate.Text,
            CollectContextBefore(snapshot, candidateIndex, beforeBudget, options),
            CollectContextAfter(snapshot, candidateIndex, afterBudget, options),
            candidateIndex,
            snapshot.Pointers.Count,
            candidate.Truncated,
            snapshot.Pointers[candidateIndex].SourcePage);
    }

    private static IReadOnlyList<HeadingContextParagraph> CollectContextBefore(
        SourceSnapshot snapshot,
        int candidateIndex,
        int budget,
        StructuralScanOptions options)
    {
        var items = new List<HeadingContextParagraph>();
        var used = 0;
        for (var index = candidateIndex - 1; index >= 0 && items.Count < options.MaxContextPointers; index--)
        {
            var item = ContextParagraph(snapshot, index, options.PerPointerTextBudget);
            if (used + item.Text.Length > budget)
            {
                break;
            }

            used += item.Text.Length;
            items.Add(item);
        }

        items.Reverse();
        return items;
    }

    private static IReadOnlyList<HeadingContextParagraph> CollectContextAfter(
        SourceSnapshot snapshot,
        int candidateIndex,
        int budget,
        StructuralScanOptions options)
    {
        var items = new List<HeadingContextParagraph>();
        var used = 0;
        for (var index = candidateIndex + 1; index < snapshot.Pointers.Count && items.Count < options.MaxContextPointers; index++)
        {
            var item = ContextParagraph(snapshot, index, options.PerPointerTextBudget);
            if (used + item.Text.Length > budget)
            {
                break;
            }

            used += item.Text.Length;
            items.Add(item);
        }

        return items;
    }

    private static HeadingContextParagraph ContextParagraph(SourceSnapshot snapshot, int index, int perPointerBudget)
    {
        var representation = BoundedPointerText(snapshot, index, perPointerBudget);
        return new HeadingContextParagraph(
            representation.Text,
            index,
            representation.Truncated,
            snapshot.Pointers[index].SourcePage);
    }

    private static IReadOnlyList<SemanticBreadcrumb> Breadcrumbs(
        SourceSnapshot snapshot,
        IReadOnlyList<HeadingStackItem> stack,
        int perPointerBudget) =>
        stack.Select(item =>
        {
            var representation = BoundedPointerText(snapshot, PointerIndex(snapshot, item.PointerId), perPointerBudget);
            return new SemanticBreadcrumb(item.Level, representation.Text);
        }).ToArray();

    private static (string Text, bool Truncated) BoundedPointerText(
        SourceSnapshot snapshot,
        int index,
        int maxCharacters)
    {
        var text = PointerText(snapshot, index);
        if (text.Length <= maxCharacters)
        {
            return (text, false);
        }

        if (maxCharacters <= 3)
        {
            return (text[..maxCharacters], true);
        }

        var marker = " … ";
        if (maxCharacters <= marker.Length)
        {
            return (text[..maxCharacters], true);
        }

        var available = maxCharacters - marker.Length;
        var prefixLength = (available + 1) / 2;
        var suffixLength = available - prefixLength;
        return (text[..prefixLength] + marker + text[^suffixLength..], true);
    }

    internal static string PointerText(SourceSnapshot snapshot, int index)
    {
        var pointer = snapshot.Pointers[index];
        return Encoding.UTF8.GetString(
                snapshot.GetSourceBytes(),
                checked((int)pointer.Span.ByteStart),
                pointer.Span.ByteLength)
            .TrimEnd('\r', '\n');
    }

    private StructuralScanState Commit(
        SourceSnapshot snapshot,
        StructuralScanState state,
        int boundary,
        IReadOnlyList<StructuralAnnotation> additions,
        IReadOnlyList<ScanWarning> newWarnings)
    {
        if (boundary < state.CommittedBoundary)
        {
            throw new InvalidOperationException("A committed boundary cannot move backwards.");
        }

        var byPointer = state.AcceptedAnnotations.ToDictionary(annotation => annotation.PointerId, StringComparer.Ordinal);
        foreach (var annotation in additions)
        {
            if (byPointer.ContainsKey(annotation.PointerId))
            {
                throw new InvalidOperationException($"Generic heading scan cannot overwrite confirmed annotation '{annotation.PointerId}'.");
            }

            byPointer.Add(annotation.PointerId, annotation);
        }

        var headings = byPointer.Values
            .Where(annotation => annotation.Role == StructuralRole.Heading && PointerIndex(snapshot, annotation.PointerId) <= boundary)
            .OrderBy(annotation => PointerIndex(snapshot, annotation.PointerId))
            .ToArray();
        var stack = new List<HeadingStackItem>();
        foreach (var heading in headings)
        {
            UpdateStack(stack, new HeadingStackItem(heading.PointerId, heading.HeadingLevel!.Value));
        }

        var recent = headings.TakeLast(12)
            .Select(heading => new HeadingStackItem(heading.PointerId, heading.HeadingLevel!.Value))
            .ToArray();
        var expected = AdvanceExpectedToc(
            state.NextExpectedTocEntries,
            byPointer.Values,
            snapshot,
            state.CommittedBoundary + 1,
            boundary);
        var deferred = state.DeferredCandidates
            .Where(candidate => PointerIndex(snapshot, candidate.PointerId) > boundary)
            .DistinctBy(candidate => candidate.PointerId, StringComparer.Ordinal)
            .ToArray();
        var nextCursor = Math.Min(snapshot.Pointers.Count, boundary + 1);

        return state with
        {
            Cursor = nextCursor,
            CommittedBoundary = boundary,
            ActiveHeadingStack = stack,
            RecentAcceptedHeadings = recent,
            NextExpectedTocEntries = expected,
            AcceptedAnnotations = byPointer.Values.OrderBy(annotation => PointerIndex(snapshot, annotation.PointerId)).ToArray(),
            DeferredCandidates = deferred,
            Warnings = state.Warnings.Concat(newWarnings).ToArray()
        };
    }

    private static void ValidateState(SourceSnapshot snapshot, StructuralScanState state)
    {
        if (state.SnapshotFingerprint != StructuralScanState.Fingerprint(snapshot))
        {
            throw new ArgumentException("Scan state belongs to a different source snapshot or segmentation configuration.", nameof(state));
        }

        if (state.CommittedBoundary != state.Cursor - 1 || state.Cursor < 0 || state.Cursor > snapshot.Pointers.Count)
        {
            throw new ArgumentException("State cursor and committed boundary are inconsistent.", nameof(state));
        }

        var known = snapshot.Pointers.Select(pointer => pointer.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var annotation in state.AcceptedAnnotations)
        {
            if (!known.Contains(annotation.PointerId))
            {
                throw new ArgumentException($"State contains unknown pointer '{annotation.PointerId}'.", nameof(state));
            }

            annotation.Validate();
        }
    }

    private static void RecordDiagnostic<TRequest, TResult>(
        ISemanticDiagnosticSink? sink,
        SemanticDiagnosticOptions? options,
        ISemanticOperationExecutor<TRequest, TResult> executor,
        string operation,
        StructuralScanWindow window,
        string? candidatePointerId,
        IReadOnlyList<int> selectableNumbers,
        object? result,
        string? failure,
        int retryCount,
        TimeSpan elapsed)
    {
        if (sink is null)
        {
            return;
        }

        var identity = executor as ISemanticDecisionIdentity;
        string? rawPrompt = null;
        string? rawResponse = null;
        if (options?.RetainRawModelMessages == true && executor is ISemanticRawTranscriptSource transcriptSource)
        {
            transcriptSource.TryGetRawTranscript(out rawPrompt, out rawResponse);
        }

        sink.Record(new SemanticDecisionDiagnostic(
            operation,
            "streaming-structural-scan",
            identity?.ProfileName,
            identity?.Model,
            $"{window.DecisionStart}..{window.DecisionEndExclusive - 1}",
            candidatePointerId,
            selectableNumbers,
            result,
            failure,
            retryCount,
            elapsed,
            rawPrompt,
            rawResponse));
    }

    private static void UpdateStack(List<HeadingStackItem> stack, HeadingStackItem heading)
    {
        stack.RemoveAll(item => item.Level >= heading.Level);
        stack.Add(heading);
    }

    private static IReadOnlyList<TocEntry> AdvanceExpectedToc(
        IReadOnlyList<TocEntry> expected,
        IEnumerable<StructuralAnnotation> annotations,
        SourceSnapshot snapshot,
        int rangeStart,
        int rangeEnd)
    {
        var cursor = 0;
        foreach (var heading in annotations
                     .Where(annotation => annotation.Role == StructuralRole.Heading)
                     .Where(annotation =>
                         PointerIndex(snapshot, annotation.PointerId) >= rangeStart &&
                         PointerIndex(snapshot, annotation.PointerId) <= rangeEnd)
                     .OrderBy(annotation => PointerIndex(snapshot, annotation.PointerId)))
        {
            if (cursor >= expected.Count)
            {
                break;
            }

            var headingText = PointerText(snapshot, PointerIndex(snapshot, heading.PointerId));
            if (Normalize(headingText) == Normalize(expected[cursor].Title))
            {
                cursor++;
            }
        }

        return expected.Skip(cursor).ToArray();
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int PointerIndex(SourceSnapshot snapshot, string pointerId)
    {
        for (var index = 0; index < snapshot.Pointers.Count; index++)
        {
            if (snapshot.Pointers[index].Id == pointerId)
            {
                return index;
            }
        }

        throw new ArgumentException($"Unknown pointer '{pointerId}'.");
    }
}
