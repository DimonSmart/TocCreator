using System.Text;
using System.Text.Json;
using Xunit;

namespace TocCreator.Tests;

public sealed class StreamingStructuralScanTests
{
    private readonly SourceImporter importer = new();
    private readonly StreamingStructuralScanner scanner = new();

    [Fact]
    public void Windows_have_single_ownership_and_are_planned_by_text_budget()
    {
        var snapshot = Import("one\ntwo\nthree\nfour\nfive\n");
        var planner = new StructuralScanWindowPlanner();
        var options = new StructuralScanOptions(
            DecisionZoneTextBudget: 100,
            PreviousContextTextBudget: 100,
            LookAheadTextBudget: 100,
            PerPointerTextBudget: 100,
            MaxDecisionPointers: 2,
            MaxContextPointers: 1);

        var first = planner.Create(snapshot, 0, options);
        var second = planner.Create(snapshot, 2, options);
        var third = planner.Create(snapshot, 4, options);

        Assert.Equal(
            snapshot.Pointers.Select(pointer => pointer.Id),
            first.DecisionPointerIds.Concat(second.DecisionPointerIds).Concat(third.DecisionPointerIds));
        Assert.Equal(["p00000003"], first.LookAheadPointerIds);
        Assert.Equal(["p00000002"], second.PreviousContextPointerIds);
    }

    [Fact]
    public async Task Detector_receives_context_full_decision_zone_and_breadcrumb_text_without_pointer_ids()
    {
        var snapshot = Import("Chapter One\nBody one\nSection\nBody after\n");
        var discovery = new StructuralDiscoveryResult(
            null,
            new AnnotationSet([
                new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Heading, HeadingLevel = 1 }
            ]));
        var state = StructuralScanState.Start(snapshot, discovery);
        var options = Options(maxDecisionPointers: 1);

        state = await scanner.ScanNextAsync(snapshot, state, options, Executors(_ => new([]), _ => throw new InvalidOperationException()));
        HeadingCandidateDetectionRequest? received = null;
        state = await scanner.ScanNextAsync(snapshot, state, options, Executors(
            request =>
            {
                received = request;
                return new HeadingCandidateDetectionResult([]);
            },
            _ => throw new InvalidOperationException()));

        Assert.NotNull(received);
        Assert.Equal("Chapter One", Assert.Single(received!.Breadcrumbs).Text);
        Assert.Equal("Chapter One", Assert.Single(received.ParagraphsBefore).Text);
        Assert.Equal("Body one", Assert.Single(received.Paragraphs).Text);
        Assert.Contains(received.ParagraphsAfter, paragraph => paragraph.Text == "Section");

        var json = JsonSerializer.Serialize(received);
        Assert.DoesNotContain("p000000", json, StringComparison.Ordinal);
        Assert.Equal(2, state.Cursor);
    }

    [Fact]
    public async Task Candidates_are_normalized_to_source_order_and_accepted_heading_updates_later_breadcrumbs()
    {
        var snapshot = Import("Chapter A\nSection B\nTail\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var verifierRequests = new List<HeadingCandidateVerificationRequest>();

        state = await scanner.ScanNextAsync(
            snapshot,
            state,
            Options(maxDecisionPointers: 3),
            Executors(
                _ => new HeadingCandidateDetectionResult([2, 1]),
                request =>
                {
                    verifierRequests.Add(request);
                    return verifierRequests.Count == 1
                        ? new HeadingCandidateVerificationResult(true, 1)
                        : new HeadingCandidateVerificationResult(true, 2);
                }));

        Assert.Equal(["Chapter A", "Section B"], verifierRequests.Select(request => request.CandidateText));
        Assert.Empty(verifierRequests[0].Breadcrumbs);
        Assert.Equal("Chapter A", Assert.Single(verifierRequests[1].Breadcrumbs).Text);
        Assert.Equal(
            [1, 2],
            state.AcceptedAnnotations.Where(annotation => annotation.Role == StructuralRole.Heading).Select(annotation => annotation.HeadingLevel));
    }

    [Fact]
    public async Task Deterministic_heading_between_candidates_updates_later_verifier_breadcrumbs()
    {
        var snapshot = Import("Candidate A\nKnown Chapter\nCandidate B\n");
        var discovery = new StructuralDiscoveryResult(
            null,
            new AnnotationSet([
                new StructuralAnnotation { PointerId = "p00000002", Role = StructuralRole.Heading, HeadingLevel = 1 }
            ]));
        var state = StructuralScanState.Start(snapshot, discovery);
        var calls = 0;

        state = await scanner.ScanNextAsync(
            snapshot,
            state,
            Options(maxDecisionPointers: 3),
            Executors(
                _ => new HeadingCandidateDetectionResult([1, 3]),
                request =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        Assert.Empty(request.Breadcrumbs);
                        return new HeadingCandidateVerificationResult(true, 2);
                    }

                    Assert.Equal("Known Chapter", Assert.Single(request.Breadcrumbs).Text);
                    return new HeadingCandidateVerificationResult(false);
                }));

        Assert.Equal(2, calls);
        Assert.Equal(3, state.Cursor);
        Assert.Equal(1, state.AcceptedAnnotations.Single(annotation => annotation.PointerId == "p00000002").HeadingLevel);
    }

    [Fact]
    public async Task Non_selectable_confirmed_pointer_causes_detector_retry_and_is_not_overwritten()
    {
        var snapshot = Import("Known\nBody\n");
        var discovery = new StructuralDiscoveryResult(
            null,
            new AnnotationSet([
                new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Heading, HeadingLevel = 1 }
            ]));
        var state = StructuralScanState.Start(snapshot, discovery);
        var calls = 0;

        state = await scanner.ScanNextAsync(
            snapshot,
            state,
            Options(maxDecisionPointers: 2),
            Executors(
                _ =>
                {
                    calls++;
                    return calls == 1
                        ? new HeadingCandidateDetectionResult([1])
                        : new HeadingCandidateDetectionResult([]);
                },
                _ => throw new InvalidOperationException("Verifier must not run.")));

        Assert.Equal(2, calls);
        Assert.Equal(2, state.Cursor);
        Assert.Single(state.AcceptedAnnotations);
        Assert.Equal(1, state.AcceptedAnnotations[0].HeadingLevel);
    }

    [Fact]
    public async Task Detector_retries_exhausted_does_not_advance_uncommitted_window()
    {
        var snapshot = Import("one\ntwo\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var diagnostics = new CollectingDiagnostics();
        var calls = 0;

        await Assert.ThrowsAsync<SemanticWindowFailureException>(() =>
            scanner.ScanNextAsync(
                snapshot,
                state,
                Options(maxDecisionPointers: 2),
                Executors(
                    _ =>
                    {
                        calls++;
                        return new HeadingCandidateDetectionResult([1, 1]);
                    },
                    _ => throw new InvalidOperationException()),
                diagnostics));

        Assert.Equal(2, calls);
        Assert.Equal(0, state.Cursor);
        Assert.Equal(-1, state.CommittedBoundary);
        Assert.Equal(2, diagnostics.Items.Count);
        Assert.All(diagnostics.Items, item => Assert.Equal("window-heading-candidate-detection", item.Operation));
        Assert.All(diagnostics.Items, item => Assert.NotNull(item.ValidationFailure));
    }

    [Fact]
    public async Task Verifier_retries_exhausted_leaves_candidate_unresolved_and_continues()
    {
        var snapshot = Import("Candidate A\nCandidate B\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var verifierCalls = 0;

        state = await scanner.ScanNextAsync(
            snapshot,
            state,
            Options(maxDecisionPointers: 2),
            Executors(
                _ => new HeadingCandidateDetectionResult([1, 2]),
                _ =>
                {
                    verifierCalls++;
                    return new HeadingCandidateVerificationResult(true, 5);
                }));

        Assert.Equal(4, verifierCalls);
        Assert.Equal(2, state.Cursor);
        Assert.Empty(state.AcceptedAnnotations);
        Assert.Equal(2, state.Warnings.Count(warning => warning.Code == "unresolved-verification"));
    }

    [Fact]
    public async Task Single_huge_pointer_is_bounded_and_scan_still_progresses()
    {
        var snapshot = Import(new string('x', 200) + "\nnext\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        HeadingCandidateDetectionRequest? received = null;
        var options = new StructuralScanOptions(
            DecisionZoneTextBudget: 10,
            PreviousContextTextBudget: 10,
            LookAheadTextBudget: 10,
            VerifierContextTextBudget: 20,
            PerPointerTextBudget: 12,
            MaxSemanticAttempts: 2,
            MaxDecisionPointers: 4,
            MaxContextPointers: 2);

        state = await scanner.ScanNextAsync(
            snapshot,
            state,
            options,
            Executors(
                request =>
                {
                    received = request;
                    return new HeadingCandidateDetectionResult([]);
                },
                _ => throw new InvalidOperationException()));

        Assert.NotNull(received);
        var paragraph = Assert.Single(received!.Paragraphs);
        Assert.True(paragraph.Truncated);
        Assert.True(paragraph.Text.Length <= 12);
        Assert.Equal(1, state.Cursor);
    }

    [Fact]
    public async Task Verifier_context_can_cross_primary_window_boundaries()
    {
        var snapshot = Import("before\nCandidate\nafter\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var options = Options(maxDecisionPointers: 1);

        state = await scanner.ScanNextAsync(
            snapshot,
            state,
            options,
            Executors(_ => new HeadingCandidateDetectionResult([]), _ => throw new InvalidOperationException()));

        HeadingCandidateVerificationRequest? received = null;
        state = await scanner.ScanNextAsync(
            snapshot,
            state,
            options,
            Executors(
                _ => new HeadingCandidateDetectionResult([1]),
                request =>
                {
                    received = request;
                    return new HeadingCandidateVerificationResult(false);
                }));

        Assert.NotNull(received);
        Assert.Contains(received!.ParagraphsBefore, paragraph => paragraph.Text == "before");
        Assert.Contains(received.ParagraphsAfter, paragraph => paragraph.Text == "after");
        Assert.Equal(2, state.Cursor);
    }

    [Fact]
    public async Task Empty_candidate_result_commits_window_without_verifier_calls()
    {
        var snapshot = Import("one\ntwo\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var verifierCalls = 0;

        state = await scanner.ScanNextAsync(
            snapshot,
            state,
            Options(maxDecisionPointers: 2),
            Executors(
                _ => new HeadingCandidateDetectionResult([]),
                _ =>
                {
                    verifierCalls++;
                    return new HeadingCandidateVerificationResult(false);
                }));

        Assert.Equal(0, verifierCalls);
        Assert.Equal(2, state.Cursor);
        Assert.Equal(1, state.CommittedBoundary);
    }

    private SourceSnapshot Import(string text) => importer.Import(Encoding.UTF8.GetBytes(text));

    private static StructuralScanOptions Options(int maxDecisionPointers) => new(
        DecisionZoneTextBudget: 10_000,
        PreviousContextTextBudget: 10_000,
        LookAheadTextBudget: 10_000,
        VerifierContextTextBudget: 10_000,
        PerPointerTextBudget: 1_000,
        MaxSemanticAttempts: 2,
        MaxDecisionPointers: maxDecisionPointers,
        MaxContextPointers: 8);

    private static HeadingScanSemanticExecutors Executors(
        Func<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult> detect,
        Func<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult> verify) =>
        new(
            new DelegateExecutor<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult>(detect),
            new DelegateExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult>(verify));

    private sealed class DelegateExecutor<TRequest, TResult>(Func<TRequest, TResult> decide)
        : ISemanticOperationExecutor<TRequest, TResult>
    {
        public Task<TResult> DecideAsync(TRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(decide(request));
    }

    private sealed class CollectingDiagnostics : ISemanticDiagnosticSink
    {
        public List<SemanticDecisionDiagnostic> Items { get; } = [];
        public void Record(SemanticDecisionDiagnostic diagnostic) => Items.Add(diagnostic);
    }
}
