using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TocCreator.Tests;

public sealed class StructuralRecoveryValidationTests
{
    [Fact]
    public void Reports_structural_findings_for_hierarchy_toc_and_repeated_headers()
    {
        var snapshot = Import("Contents\nOne .... 1\nOne\nOne\n1.1 Wrong level\n");
        var state = StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(
            new TocRange("p00000001", "p00000002", [new TocEntry("p00000002", "One", 1, 1)]),
            new AnnotationSet([]))) with
        {
            TocEntries = [new TocEntry("p00000002", "One", 1, 1)],
            AcceptedAnnotations =
            [
                new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Toc },
                new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Heading, HeadingLevel = 1 },
                new StructuralAnnotation { PointerId = "p00000003", Role = StructuralRole.Heading, HeadingLevel = 1 },
                new StructuralAnnotation { PointerId = "p00000004", Role = StructuralRole.Heading, HeadingLevel = 1 },
                new StructuralAnnotation { PointerId = "p00000005", Role = StructuralRole.Heading, HeadingLevel = 3 }
            ]
        };

        var codes = new StructuralRecoveryValidator().Validate(snapshot, state).Select(finding => finding.Code);

        Assert.Contains("toc-body-separation", codes);
        Assert.Contains("repeated-running-header", codes);
        Assert.Contains("heading-numbering", codes);
        Assert.Contains("heading-hierarchy", codes);
    }

    [Fact]
    public async Task Repairs_only_reported_writable_pointers_with_a_bounded_local_review()
    {
        var snapshot = Import("Chapter\nSection\n");
        var state = State(
            snapshot,
            new StructuralAnnotation { PointerId = "p00000001", Role = StructuralRole.Heading, HeadingLevel = 1 },
            new StructuralAnnotation { PointerId = "p00000002", Role = StructuralRole.Heading, HeadingLevel = 3 });
        var executor = new ReviewExecutor(request => new ValidatedAnomalyReviewResult(
            request.Anomalies
                .Select(anomaly => new AnomalyReview(anomaly.PointerId, AnomalyReviewDisposition.Dismiss))
                .ToArray()));
        var path = Path.Combine(Path.GetTempPath(), $"repair-{Guid.NewGuid():N}.json");
        try
        {
            var result = await new StructuralRecoveryRepairWorkflow(new StructuralRecoveryValidator())
                .ValidateAndRepairAsync(
                    snapshot,
                    state,
                    new StructuralRecoveryRepairOptions(MaxCycles: 1, ContextSize: 1),
                    executor,
                    new StructuralScanStateStore(),
                    path,
                    null);

            Assert.DoesNotContain(result.AcceptedAnnotations, annotation => annotation.PointerId == "p00000002");
            Assert.Contains(result.AcceptedAnnotations, annotation => annotation.PointerId == "p00000001");
            Assert.Equal(
                ["p00000002"],
                Assert.Single(executor.Requests).Context.WritablePointers.Select(pointer => pointer.PointerId));
            Assert.DoesNotContain("p00000001", executor.Requests[0].Context.WritablePointerIds);
            Assert.Equal(1, result.RepairCyclesCompleted);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Completion_checkpoint_resumes_without_replaying_scan()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshotPath = Path.Combine(root, "snapshot.json");
            var statePath = Path.Combine(root, "state.json");
            await new SnapshotStore().SaveAsync(snapshotPath, Import("one\ntwo\n"));
            var options = Options(snapshotPath, statePath, root);

            await Command().ExecuteAsync(options, EmptyHeadingExecutors());
            Assert.Equal(
                StructuralRecoveryCheckpoint.Completed,
                (await new StructuralScanStateStore().LoadAsync(statePath)).Checkpoint);

            await Command().ExecuteAsync(options, ThrowingHeadingExecutors());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Completes_with_warnings_when_no_repair_executor_is_available()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshotPath = Path.Combine(root, "snapshot.json");
            var statePath = Path.Combine(root, "state.json");
            await new SnapshotStore().SaveAsync(snapshotPath, Import("Chapter\nSection\n"));

            await Command().ExecuteAsync(
                Options(snapshotPath, statePath, root),
                new HeadingScanSemanticExecutors(
                    new DelegateExecutor<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult>(
                        request => new HeadingCandidateDetectionResult(
                            request.Paragraphs
                                .Where(paragraph => paragraph.Text == "Section")
                                .Select(paragraph => paragraph.Number)
                                .ToArray())),
                    new DelegateExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult>(
                        _ => new HeadingCandidateVerificationResult(true, 3))));

            var state = await new StructuralScanStateStore().LoadAsync(statePath);
            Assert.Equal(StructuralRecoveryCheckpoint.Completed, state.Checkpoint);
            Assert.Contains(state.Warnings, warning => warning.Code == "validation-heading-hierarchy");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SourceSnapshot Import(string text) =>
        new SourceImporter().Import(Encoding.UTF8.GetBytes(text));

    private static StructuralScanState State(
        SourceSnapshot snapshot,
        params StructuralAnnotation[] annotations) =>
        StructuralScanState.Start(snapshot, new StructuralDiscoveryResult(null, new AnnotationSet([]))) with
        {
            Cursor = snapshot.Pointers.Count,
            CommittedBoundary = snapshot.Pointers.Count - 1,
            AcceptedAnnotations = annotations
        };

    private static StructuralRecoveryOptions Options(string snapshot, string state, string root) =>
        new(
            snapshot,
            state,
            Path.Combine(root, "annotations.json"),
            Path.Combine(root, "diagnostics.json"),
            Path.Combine(root, "book.md"),
            new OpenAICompatibleModelProfile(
                "test",
                new Uri("http://localhost/"),
                "test",
                new OpenAICompatibleCredentials("test")),
            new StructuralScanOptions(
                DecisionZoneTextBudget: 10_000,
                PreviousContextTextBudget: 10_000,
                LookAheadTextBudget: 10_000,
                VerifierContextTextBudget: 10_000,
                PerPointerTextBudget: 1_000,
                MaxSemanticAttempts: 2,
                MaxDecisionPointers: 2,
                MaxContextPointers: 8));

    private static StructuralRecoveryCommand Command() => new(
        new SnapshotStore(),
        new StructuralScanStateStore(),
        new AnnotationStore(),
        new StructuralDiscovery(),
        new StreamingStructuralScanner(),
        new MarkdownExporter(NullLogger<MarkdownExporter>.Instance),
        NullLogger<StructuralRecoveryCommand>.Instance);

    private static HeadingScanSemanticExecutors EmptyHeadingExecutors() => new(
        new DelegateExecutor<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult>(
            _ => new HeadingCandidateDetectionResult([])),
        new DelegateExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult>(
            _ => new HeadingCandidateVerificationResult(false)));

    private static HeadingScanSemanticExecutors ThrowingHeadingExecutors() => new(
        new DelegateExecutor<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult>(
            _ => throw new InvalidOperationException("A completed checkpoint must not scan.")),
        new DelegateExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult>(
            _ => throw new InvalidOperationException("A completed checkpoint must not scan.")));

    private sealed class ReviewExecutor(
        Func<ValidatedAnomalyReviewRequest, ValidatedAnomalyReviewResult> decide)
        : ISemanticOperationExecutor<ValidatedAnomalyReviewRequest, ValidatedAnomalyReviewResult>
    {
        public List<ValidatedAnomalyReviewRequest> Requests { get; } = [];

        public Task<ValidatedAnomalyReviewResult> DecideAsync(
            ValidatedAnomalyReviewRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(decide(request));
        }
    }

    private sealed class DelegateExecutor<TRequest, TResult>(Func<TRequest, TResult> decide)
        : ISemanticOperationExecutor<TRequest, TResult>
    {
        public Task<TResult> DecideAsync(TRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(decide(request));
    }
}
