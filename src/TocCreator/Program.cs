using Microsoft.Extensions.Logging;

namespace TocCreator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
        var logger = loggerFactory.CreateLogger("TocCreator");
        var store = new SnapshotStore();
        var annotationStore = new AnnotationStore();

        if (args is ["import", var inputPath, var importSnapshotPath])
        {
            logger.LogInformation("Starting source import from {InputPath}.", inputPath);
            var snapshot = new SourceImporter().Import(await File.ReadAllBytesAsync(inputPath));
            await store.SaveAsync(importSnapshotPath, snapshot);
            logger.LogInformation("Completed source import with {PointerCount} pointers.", snapshot.Pointers.Count);
            return 0;
        }

        if (args is ["export", var exportSnapshotPath, var outputPath])
        {
            var snapshot = await store.LoadAsync(exportSnapshotPath);
            var markdown = new MarkdownExporter(loggerFactory.CreateLogger<MarkdownExporter>())
                .Export(snapshot, new AnnotationSet([]));
            await File.WriteAllBytesAsync(outputPath, markdown);
            return 0;
        }

        if (args is ["export", var annotatedSnapshotPath, var annotatedOutputPath, var annotationsPath])
        {
            var snapshot = await store.LoadAsync(annotatedSnapshotPath);
            var annotations = await annotationStore.LoadAsync(annotationsPath);
            var markdown = new MarkdownExporter(loggerFactory.CreateLogger<MarkdownExporter>())
                .Export(snapshot, annotations);
            await File.WriteAllBytesAsync(annotatedOutputPath, markdown);
            return 0;
        }

        if (args is ["recover", var recoverySnapshotPath, var statePath, var recoveryAnnotationsPath, var diagnosticsPath, var recoveryOutputPath, var endpoint, var model, var apiKey])
        {
            var profile = new OpenAICompatibleModelProfile(
                "cli",
                new Uri(endpoint, UriKind.Absolute),
                model,
                new OpenAICompatibleCredentials(apiKey));

            using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var command = new StructuralRecoveryCommand(
                store,
                new StructuralScanStateStore(),
                annotationStore,
                new StructuralDiscovery(),
                new StreamingStructuralScanner(logger: loggerFactory.CreateLogger<StreamingStructuralScanner>()),
                new MarkdownExporter(loggerFactory.CreateLogger<MarkdownExporter>()),
                loggerFactory.CreateLogger<StructuralRecoveryCommand>());

            var headingExecutors = new HeadingScanSemanticExecutors(
                new HeadingCandidateDetectionExecutor(
                    profile,
                    new OpenAICompatibleHeadingCandidateDetectionProvider(httpClient)),
                new HeadingCandidateVerificationExecutor(
                    profile,
                    new OpenAICompatibleHeadingCandidateVerificationProvider(httpClient)));

            var semanticExecutors = new StructuralRecoverySemanticExecutors(
                new TocDetectionExecutor(profile, new OpenAICompatibleTocDetectionProvider(httpClient)),
                new TocParsingExecutor(profile, new OpenAICompatibleTocParsingProvider(httpClient)),
                new AmbiguousHeadingMatchExecutor(profile, new OpenAICompatibleAmbiguousHeadingMatchProvider(httpClient)),
                new AmbiguousNoiseClassificationExecutor(profile, new OpenAICompatibleAmbiguousNoiseClassificationProvider(httpClient)),
                new ValidatedAnomalyReviewExecutor(profile, new OpenAICompatibleValidatedAnomalyReviewProvider(httpClient)));

            await command.ExecuteAsync(
                new StructuralRecoveryOptions(
                    recoverySnapshotPath,
                    statePath,
                    recoveryAnnotationsPath,
                    diagnosticsPath,
                    recoveryOutputPath,
                    profile,
                    new StructuralScanOptions()),
                headingExecutors,
                semanticExecutors);
            return 0;
        }

        Console.Error.WriteLine(
            "Usage: TocCreator import <input.txt> <snapshot.json> | export <snapshot.json> <output.md> [annotations.json] | recover <snapshot.json> <state.json> <annotations.json> <diagnostics.json> <output.md> <endpoint> <model> <api-key>");
        return 1;
    }
}
