using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TocCreator.Tests;

public sealed class StructuralRecoveryCommandTests
{
    [Fact]
    public async Task Resumes_an_interrupted_uncommitted_window_at_saved_boundary()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshotPath = Path.Combine(root, "snapshot.json");
            var statePath = Path.Combine(root, "state.json");
            var annotationsPath = Path.Combine(root, "annotations.json");
            var diagnosticsPath = Path.Combine(root, "diagnostics.json");
            var markdownPath = Path.Combine(root, "book.md");
            await new SnapshotStore().SaveAsync(
                snapshotPath,
                new SourceImporter().Import(Encoding.UTF8.GetBytes("one\ntwo\nthree\nfour\n")));

            var interruptedDetector = new RecordingDetector(request =>
            {
                if (request.Paragraphs[0].GlobalPosition == 2)
                {
                    throw new OperationCanceledException();
                }

                return new HeadingCandidateDetectionResult([]);
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                CreateCommand().ExecuteAsync(
                    Options(snapshotPath, statePath, annotationsPath, diagnosticsPath, markdownPath),
                    new HeadingScanSemanticExecutors(
                        interruptedDetector,
                        new DelegateExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult>(
                            _ => new HeadingCandidateVerificationResult(false)))));

            var resumedDetector = new RecordingDetector(_ => new HeadingCandidateDetectionResult([]));
            await CreateCommand().ExecuteAsync(
                Options(snapshotPath, statePath, annotationsPath, diagnosticsPath, markdownPath),
                new HeadingScanSemanticExecutors(
                    resumedDetector,
                    new DelegateExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult>(
                        _ => new HeadingCandidateVerificationResult(false))));

            Assert.Equal([2], resumedDetector.Requests.Select(request => request.Paragraphs[0].GlobalPosition));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Runs_discovery_two_stage_scan_export_and_completed_resume_without_replay()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshotPath = Path.Combine(root, "snapshot.json");
            var statePath = Path.Combine(root, "state.json");
            var annotationsPath = Path.Combine(root, "annotations.json");
            var diagnosticsPath = Path.Combine(root, "diagnostics.json");
            var markdownPath = Path.Combine(root, "book.md");
            var snapshot = new SourceImporter().Import(Encoding.UTF8.GetBytes(
                "Contents\nChapter One .... 1\nAppendix A .... 20\nChapter One\nBody\nSection\nAppendix A\n"));
            await new SnapshotStore().SaveAsync(snapshotPath, snapshot);

            var detector = new RecordingDetector(request => new HeadingCandidateDetectionResult(
                request.Paragraphs
                    .Where(paragraph => paragraph.Selectable && paragraph.Text.Trim() == "Section")
                    .Select(paragraph => paragraph.Number)
                    .ToArray()));
            var verifier = new RecordingVerifier(request =>
                request.CandidateText.Trim() == "Section"
                    ? new HeadingCandidateVerificationResult(true, 2)
                    : new HeadingCandidateVerificationResult(false));

            await CreateCommand().ExecuteAsync(
                Options(snapshotPath, statePath, annotationsPath, diagnosticsPath, markdownPath),
                new HeadingScanSemanticExecutors(detector, verifier));

            var annotations = await new AnnotationStore().LoadAsync(annotationsPath);
            Assert.Equal(StructuralRole.Toc, annotations.GetOrBody("p00000001").Role);
            Assert.Equal(1, annotations.GetOrBody("p00000004").HeadingLevel);
            Assert.Equal(2, annotations.GetOrBody("p00000006").HeadingLevel);
            Assert.Equal(
                "Contents\nChapter One .... 1\nAppendix A .... 20\n# Chapter One\nBody\n## Section\n# Appendix A\n",
                await File.ReadAllTextAsync(markdownPath));
            Assert.NotEmpty(await File.ReadAllTextAsync(diagnosticsPath));
            Assert.DoesNotContain(
                "rawPrompt",
                await File.ReadAllTextAsync(diagnosticsPath),
                StringComparison.OrdinalIgnoreCase);

            var noReplayDetector = new RecordingDetector(_ =>
                throw new InvalidOperationException("A completed scan must not call the detector again."));
            var noReplayVerifier = new RecordingVerifier(_ =>
                throw new InvalidOperationException("A completed scan must not call the verifier again."));
            await CreateCommand().ExecuteAsync(
                Options(snapshotPath, statePath, annotationsPath, diagnosticsPath, markdownPath),
                new HeadingScanSemanticExecutors(noReplayDetector, noReplayVerifier));

            Assert.Empty(noReplayDetector.Requests);
            Assert.Empty(noReplayVerifier.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StructuralRecoveryCommand CreateCommand() => new(
        new SnapshotStore(),
        new StructuralScanStateStore(),
        new AnnotationStore(),
        new StructuralDiscovery(),
        new StreamingStructuralScanner(logger: NullLogger<StreamingStructuralScanner>.Instance),
        new MarkdownExporter(NullLogger<MarkdownExporter>.Instance),
        NullLogger<StructuralRecoveryCommand>.Instance);

    private static StructuralRecoveryOptions Options(
        string snapshot,
        string state,
        string annotations,
        string diagnostics,
        string markdown) =>
        new(
            snapshot,
            state,
            annotations,
            diagnostics,
            markdown,
            Profile(),
            new StructuralScanOptions(
                DecisionZoneTextBudget: 10_000,
                PreviousContextTextBudget: 10_000,
                LookAheadTextBudget: 10_000,
                VerifierContextTextBudget: 10_000,
                PerPointerTextBudget: 1_000,
                MaxSemanticAttempts: 2,
                MaxDecisionPointers: 2,
                MaxContextPointers: 8));

    private static OpenAICompatibleModelProfile Profile() =>
        new(
            "test",
            new Uri("http://localhost:11434/v1/"),
            "test-model",
            new OpenAICompatibleCredentials("test-key"));

    private sealed class RecordingDetector(
        Func<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult> decide)
        : ISemanticOperationExecutor<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult>
    {
        public List<HeadingCandidateDetectionRequest> Requests { get; } = [];

        public Task<HeadingCandidateDetectionResult> DecideAsync(
            HeadingCandidateDetectionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(decide(request));
        }
    }

    private sealed class RecordingVerifier(
        Func<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult> decide)
        : ISemanticOperationExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult>
    {
        public List<HeadingCandidateVerificationRequest> Requests { get; } = [];

        public Task<HeadingCandidateVerificationResult> DecideAsync(
            HeadingCandidateVerificationRequest request,
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
