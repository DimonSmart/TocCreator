using System.Text;
using Xunit;

namespace TocCreator.Tests;

public sealed class StreamingStructuralScanTests
{
    private readonly SourceImporter importer = new();
    private readonly StreamingStructuralScanner scanner = new();

    [Fact]
    public void Windows_have_one_writable_owner_and_read_only_overlap()
    {
        var snapshot = Import("one\ntwo\nthree\nfour\nfive\n");
        var planner = new StructuralScanWindowPlanner();
        var options = new StructuralScanOptions(2, ContextSize: 1, LookAheadSize: 1);

        var first = planner.Create(snapshot, 0, options);
        var second = planner.Create(snapshot, 2, options);
        var third = planner.Create(snapshot, 4, options);

        Assert.Equal(snapshot.Pointers.Select(pointer => pointer.Id), first.DecisionPointerIds.Concat(second.DecisionPointerIds).Concat(third.DecisionPointerIds));
        Assert.Equal(["p00000003"], first.LookAheadPointerIds);
        Assert.Equal(["p00000002"], second.PreviousContextPointerIds);
        Assert.DoesNotContain("p00000003", first.DecisionPointerIds);
    }

    [Fact]
    public async Task Defers_boundary_candidate_until_its_next_primary_window()
    {
        var snapshot = Import("Chapter\nBody\nSection\nMore\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var options = new StructuralScanOptions(3, LookAheadSize: 1);

        state = await scanner.ScanNextAsync(snapshot, state, options, new DelegateExecutor(_ => Result(
            Annotate("p00000001", StructuralRole.Heading, 1),
            new StructuralDecision("p00000003", StructuralDecisionKind.Defer, Reason: "Need following paragraph."))));

        Assert.Equal(2, state.Cursor);
        Assert.Equal(1, state.CommittedBoundary);
        Assert.Equal("p00000003", Assert.Single(state.DeferredCandidates).PointerId);
        Assert.Equal(StructuralRole.Heading, state.AcceptedAnnotations.Single().Role);

        state = await scanner.ScanNextAsync(snapshot, state, options, new DelegateExecutor(_ => Result(Annotate("p00000003", StructuralRole.Heading, 2))));

        Assert.Equal(4, state.Cursor);
        Assert.Empty(state.DeferredCandidates);
        Assert.Equal(2, state.AcceptedAnnotations.Single(annotation => annotation.PointerId == "p00000003").HeadingLevel);
    }

    [Fact]
    public async Task Restart_resumes_after_persisted_boundary_without_replaying_committed_window()
    {
        var snapshot = Import("one\ntwo\nthree\nfour\n");
        var options = new StructuralScanOptions(2);
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var firstCalls = new List<string>();
        state = await scanner.ScanNextAsync(snapshot, state, options, new DelegateExecutor(request => { firstCalls.Add(request.DecisionZone[0].PointerId); return Result(); }));
        Assert.Equal(["p00000001"], firstCalls);

        var path = Path.Combine(Path.GetTempPath(), $"scan-{Guid.NewGuid():N}.json");
        try
        {
            var store = new StructuralScanStateStore();
            await store.SaveAsync(path, state);
            var resumed = await store.LoadAsync(path);
            var resumedCalls = new List<string>();
            resumed = await scanner.ScanNextAsync(snapshot, resumed, options, new DelegateExecutor(request => { resumedCalls.Add(request.DecisionZone[0].PointerId); return Result(); }));

            Assert.Equal(["p00000003"], resumedCalls);
            Assert.Equal(4, resumed.Cursor);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Rejects_state_when_source_snapshot_or_segmentation_changes()
    {
        var original = Import("one\ntwo\n");
        var changed = Import("one\ntwo\nthree\n");
        var state = StructuralScanState.Start(original, new StructuralDiscoveryResult(null, new AnnotationSet([])));

        await Assert.ThrowsAsync<ArgumentException>(() => scanner.ScanNextAsync(changed, state, new StructuralScanOptions(1), new DelegateExecutor(_ => Result())));
    }

    [Fact]
    public async Task Mid_chapter_window_receives_prior_structure_and_does_not_promote_its_first_body_pointer()
    {
        var snapshot = Import("Chapter One\nBody one\nBody two\nBody three\n");
        var options = new StructuralScanOptions(2, ContextSize: 1, LookAheadSize: 1);
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        state = await scanner.ScanNextAsync(snapshot, state, options, new DelegateExecutor(_ => Result(Annotate("p00000001", StructuralRole.Heading, 1))));
        StructuralDecisionRequest? secondRequest = null;

        state = await scanner.ScanNextAsync(snapshot, state, options, new DelegateExecutor(request => { secondRequest = request; return Result(); }));

        Assert.NotNull(secondRequest);
        Assert.Equal("p00000002", Assert.Single(secondRequest!.PreviousContext).PointerId);
        Assert.Equal("p00000003", secondRequest.DecisionZone[0].PointerId);
        Assert.Equal("p00000001", Assert.Single(secondRequest.ActiveHeadingStack).PointerId);
        Assert.Equal(1, secondRequest.CurrentHeadingLevel);
        Assert.Equal(StructuralRole.Body, new AnnotationSet(state.AcceptedAnnotations).GetOrBody("p00000003").Role);
    }

    [Fact]
    public async Task Application_maintains_heading_stack_transitions()
    {
        var snapshot = Import("Chapter\nSection\nSubsection\nAnother section\n");
        var options = new StructuralScanOptions(1);
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        foreach (var decision in new[] { Annotate("p00000001", StructuralRole.Heading, 1), Annotate("p00000002", StructuralRole.Heading, 2), Annotate("p00000003", StructuralRole.Heading, 3), Annotate("p00000004", StructuralRole.Heading, 2) })
        {
            state = await scanner.ScanNextAsync(snapshot, state, options, new DelegateExecutor(_ => Result(decision)));
        }

        Assert.Equal([1, 2], state.ActiveHeadingStack.Select(item => item.Level));
        Assert.Equal(["p00000001", "p00000004"], state.ActiveHeadingStack.Select(item => item.PointerId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("p00000003")]
    public async Task Invalid_decisions_retry_only_the_window_then_leave_it_unresolved(string pointerId)
    {
        var snapshot = Import("one\ntwo\nthree\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var diagnostics = new CollectingDiagnostics();
        var calls = 0;

        state = await scanner.ScanNextAsync(snapshot, state, new StructuralScanOptions(2), new DelegateExecutor(_ => { calls++; return Result(Annotate(pointerId, StructuralRole.Heading, 1)); }), diagnostics);

        Assert.Equal(2, calls);
        Assert.Equal(2, diagnostics.Items.Count);
        Assert.All(diagnostics.Items, item => Assert.NotNull(item.ValidationFailure));
        Assert.Empty(state.AcceptedAnnotations);
        Assert.Contains(state.Warnings, warning => warning.Code == "semantic-unresolved");
    }

    [Fact]
    public async Task Invalid_result_is_retried_and_records_typed_semantic_diagnostics()
    {
        var snapshot = Import("Chapter\nBody\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var diagnostics = new CollectingDiagnostics();
        var results = new Queue<StructuralDecisionResult>([
            Result(Annotate("missing", StructuralRole.Heading, 1)),
            Result(Annotate("p00000001", StructuralRole.Heading, 1))]);

        state = await scanner.ScanNextAsync(snapshot, state, new StructuralScanOptions(2), new DelegateExecutor(_ => results.Dequeue()), diagnostics);

        Assert.Equal(StructuralRole.Heading, Assert.Single(state.AcceptedAnnotations).Role);
        Assert.Equal(2, diagnostics.Items.Count);
        Assert.Equal("window-heading-classification", diagnostics.Items[0].Operation);
        Assert.Equal("streaming-structural-scan", diagnostics.Items[0].WorkflowStep);
        Assert.Equal(["p00000001", "p00000002"], diagnostics.Items[0].SuppliedPointerIds);
        Assert.NotNull(diagnostics.Items[0].ValidationFailure);
        Assert.Equal(1, diagnostics.Items[1].RetryCount);
        Assert.NotNull(diagnostics.Items[1].TypedResult);
    }

    [Fact]
    public async Task Missing_decision_collection_is_retried_as_invalid_output()
    {
        var snapshot = Import("Chapter\nBody\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var calls = 0;

        state = await scanner.ScanNextAsync(snapshot, state, new StructuralScanOptions(2), new DelegateExecutor(_ =>
        {
            calls++;
            return new StructuralDecisionResult(null!);
        }));

        Assert.Equal(2, calls);
        Assert.Empty(state.AcceptedAnnotations);
        Assert.Contains(state.Warnings, warning => warning.Code == "semantic-unresolved");
    }

    [Fact]
    public async Task Context_and_look_ahead_pointers_are_never_writable()
    {
        var snapshot = Import("one\ntwo\nthree\nfour\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        var options = new StructuralScanOptions(1, ContextSize: 1, LookAheadSize: 1);
        state = await scanner.ScanNextAsync(snapshot, state, options, new DelegateExecutor(_ => Result()));
        var calls = 0;

        state = await scanner.ScanNextAsync(snapshot, state, options, new DelegateExecutor(request =>
        {
            calls++;
            return Result(Annotate(calls == 1 ? request.PreviousContext[0].PointerId : request.LookAhead[0].PointerId, StructuralRole.Heading, 1));
        }));

        Assert.Equal(2, calls);
        Assert.Empty(state.AcceptedAnnotations);
        Assert.Contains(state.Warnings, warning => warning.Code == "semantic-unresolved");
    }

    [Fact]
    public async Task Agent_framework_executor_uses_profile_and_typed_local_request()
    {
        var snapshot = Import("Chapter\nBody\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([])));
        WindowHeadingProviderRequest? received = null;
        var profile = new OpenAICompatibleModelProfile("local", new Uri("http://localhost:11434/v1/"), "gpt-oss:20b", new OpenAICompatibleCredentials("test"));
        var executor = new LocalWindowHeadingDecisionExecutor(profile, new DelegateProvider(request =>
        {
            received = request;
            return new WindowHeadingProviderResponse(Result(Annotate("p00000001", StructuralRole.Heading, 1)), "prompt", "response");
        }));
        var diagnostics = new CollectingDiagnostics();

        state = await scanner.ScanNextAsync(snapshot, state, new StructuralScanOptions(2), executor, diagnostics);

        Assert.NotNull(received);
        Assert.Equal("local", received!.Profile.Name);
        Assert.NotNull(received.JsonSchema);
        Assert.Equal(["p00000001", "p00000002"], received.Input.DecisionZone.Select(pointer => pointer.PointerId));
        Assert.Equal("local", Assert.Single(diagnostics.Items).ProfileName);
        Assert.Equal("gpt-oss:20b", diagnostics.Items[0].Model);
        Assert.Null(diagnostics.Items[0].RawPrompt);
        Assert.Equal(StructuralRole.Heading, Assert.Single(state.AcceptedAnnotations).Role);

        var debugDiagnostics = new CollectingDiagnostics();
        var debugExecutor = new LocalWindowHeadingDecisionExecutor(profile, new DelegateProvider(_ => new WindowHeadingProviderResponse(Result(Annotate("p00000001", StructuralRole.Heading, 1)), "prompt", "response")), retainRawMessages: true);
        await scanner.ScanNextAsync(snapshot, StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([]))), new StructuralScanOptions(2), debugExecutor, debugDiagnostics, new SemanticDiagnosticOptions(RetainRawModelMessages: true));
        Assert.Equal("prompt", debugDiagnostics.Items[0].RawPrompt);
        Assert.Equal("response", debugDiagnostics.Items[0].RawResponse);
    }

    private SourceSnapshot Import(string text) => importer.Import(Encoding.UTF8.GetBytes(text));
    private static StructuralDecisionResult Result(params StructuralDecision[] decisions) => new(decisions);
    private static StructuralDecision Annotate(string pointerId, StructuralRole role, int? level = null) => new(pointerId, StructuralDecisionKind.Annotate, new StructuralAnnotation { PointerId = pointerId, Role = role, HeadingLevel = level });

    private sealed class DelegateExecutor(Func<StructuralDecisionRequest, StructuralDecisionResult> decide) : IStructuralDecisionExecutor
    {
        public Task<StructuralDecisionResult> DecideAsync(StructuralDecisionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(decide(request));
    }

    private sealed class DelegateProvider(Func<WindowHeadingProviderRequest, WindowHeadingProviderResponse> decide) : IWindowHeadingDecisionProvider
    {
        public Task<WindowHeadingProviderResponse> DecideAsync(WindowHeadingProviderRequest request, CancellationToken cancellationToken = default) => Task.FromResult(decide(request));
    }

    private sealed class CollectingDiagnostics : ISemanticDiagnosticSink
    {
        public List<SemanticDecisionDiagnostic> Items { get; } = [];
        public void Record(SemanticDecisionDiagnostic diagnostic) => Items.Add(diagnostic);
    }
}

