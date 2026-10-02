using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TocCreator.Tests;

public sealed class StructuralRecoveryCommandTests
{
    [Fact]
    public async Task Resumes_an_interrupted_scan_at_the_saved_committed_boundary()
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
            await new SnapshotStore().SaveAsync(snapshotPath, new SourceImporter().Import(Encoding.UTF8.GetBytes("one\ntwo\nthree\nfour\n")));
            var interruptedProvider = new RecordingProvider(request =>
            {
                if (request.DecisionZone[0].PointerId == "p00000003") throw new OperationCanceledException();
                return new StructuralDecisionResult([]);
            });

            await Assert.ThrowsAsync<OperationCanceledException>(() => CreateCommand().ExecuteAsync(
                Options(snapshotPath, statePath, annotationsPath, diagnosticsPath, markdownPath),
                new LocalWindowHeadingDecisionExecutor(Profile(), interruptedProvider)));

            var resumedProvider = new RecordingProvider(_ => new StructuralDecisionResult([]));
            await CreateCommand().ExecuteAsync(Options(snapshotPath, statePath, annotationsPath, diagnosticsPath, markdownPath), new LocalWindowHeadingDecisionExecutor(Profile(), resumedProvider));

            Assert.Equal(["p00000003"], resumedProvider.Requests.Select(request => request.DecisionZone[0].PointerId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Runs_toc_discovery_scans_exports_and_resumes_without_replaying_committed_windows()
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
            var snapshot = new SourceImporter().Import(Encoding.UTF8.GetBytes("Contents\nChapter One .... 1\nAppendix A .... 20\nChapter One\nBody\nSection\nAppendix A\n"));
            await new SnapshotStore().SaveAsync(snapshotPath, snapshot);
            var command = CreateCommand();
            var firstProvider = new RecordingProvider(request => new StructuralDecisionResult(request.DecisionZone
                .Where(pointer => pointer.Text.Trim() == "Section")
                .Select(pointer => Heading(pointer.PointerId, 2)).ToArray()));

            await command.ExecuteAsync(Options(snapshotPath, statePath, annotationsPath, diagnosticsPath, markdownPath), new LocalWindowHeadingDecisionExecutor(Profile(), firstProvider));

            var annotations = await new AnnotationStore().LoadAsync(annotationsPath);
            Assert.Equal(StructuralRole.Toc, annotations.GetOrBody("p00000001").Role);
            Assert.Equal(1, annotations.GetOrBody("p00000004").HeadingLevel);
            Assert.Equal(2, annotations.GetOrBody("p00000006").HeadingLevel);
            Assert.Equal("Contents\nChapter One .... 1\nAppendix A .... 20\n# Chapter One\nBody\n## Section\n# Appendix A\n", await File.ReadAllTextAsync(markdownPath));
            Assert.NotEmpty(await File.ReadAllTextAsync(diagnosticsPath));
            Assert.DoesNotContain("rawPrompt", await File.ReadAllTextAsync(diagnosticsPath), StringComparison.OrdinalIgnoreCase);

            var resumedProvider = new RecordingProvider(_ => throw new InvalidOperationException("A completed scan must not call the provider again."));
            await command.ExecuteAsync(Options(snapshotPath, statePath, annotationsPath, diagnosticsPath, markdownPath), new LocalWindowHeadingDecisionExecutor(Profile(), resumedProvider));
            Assert.Empty(resumedProvider.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StructuralRecoveryCommand CreateCommand() => new(
        new SnapshotStore(), new StructuralScanStateStore(), new AnnotationStore(), new StructuralDiscovery(),
        new StreamingStructuralScanner(logger: NullLogger<StreamingStructuralScanner>.Instance),
        new MarkdownExporter(NullLogger<MarkdownExporter>.Instance), NullLogger<StructuralRecoveryCommand>.Instance);

    private static StructuralRecoveryOptions Options(string snapshot, string state, string annotations, string diagnostics, string markdown) =>
        new(snapshot, state, annotations, diagnostics, markdown, Profile(), new StructuralScanOptions(2));

    private static OpenAICompatibleModelProfile Profile() => new("test", new Uri("http://localhost:11434/v1/"), "test-model", new OpenAICompatibleCredentials("test-key"));
    private static StructuralDecision Heading(string pointerId, int level) => new(pointerId, StructuralDecisionKind.Annotate, new StructuralAnnotation { PointerId = pointerId, Role = StructuralRole.Heading, HeadingLevel = level });

    private sealed class RecordingProvider(Func<StructuralDecisionRequest, StructuralDecisionResult> decide) : IWindowHeadingDecisionProvider
    {
        public List<StructuralDecisionRequest> Requests { get; } = [];
        public Task<WindowHeadingProviderResponse> DecideAsync(WindowHeadingProviderRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Input);
            return Task.FromResult(new WindowHeadingProviderResponse(decide(request.Input), "provider raw prompt", "provider raw response"));
        }
    }
}
