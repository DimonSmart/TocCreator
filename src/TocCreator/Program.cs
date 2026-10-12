using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace TocCreator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            }));
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
            var confirmedHeadingsPath = Path.Combine(
                Path.GetDirectoryName(recoveryOutputPath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(recoveryOutputPath)}.confirmed-headings.md");
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
                    new OpenAICompatibleHeadingCandidateDetectionProvider(httpClient),
                    retainRawMessages: true),
                new HeadingCandidateVerificationExecutor(
                    profile,
                    new OpenAICompatibleHeadingCandidateVerificationProvider(httpClient),
                    retainRawMessages: true));

            var semanticExecutors = new StructuralRecoverySemanticExecutors(
                new TocDetectionExecutor(profile, new OpenAICompatibleTocDetectionProvider(httpClient)),
                new TocParsingExecutor(profile, new OpenAICompatibleTocParsingProvider(httpClient)),
                new AmbiguousHeadingMatchExecutor(profile, new OpenAICompatibleAmbiguousHeadingMatchProvider(httpClient)),
                new AmbiguousNoiseClassificationExecutor(profile, new OpenAICompatibleAmbiguousNoiseClassificationProvider(httpClient)),
                new ValidatedAnomalyReviewExecutor(profile, new OpenAICompatibleValidatedAnomalyReviewProvider(httpClient)));

            using var cancellationSource = new CancellationTokenSource();
            var cancellationRequests = 0;
            void RequestCancellation()
            {
                if (Interlocked.Increment(ref cancellationRequests) != 1)
                {
                    return;
                }

                logger.LogInformation("Cancellation requested. Cancelling the active Ollama request; the last committed checkpoint remains available for resume.");
                httpClient.CancelPendingRequests();
                cancellationSource.Cancel();
            }

            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                RequestCancellation();
            };
            Console.CancelKeyPress += cancelHandler;
            using var sigintRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, signalContext =>
            {
                signalContext.Cancel = true;
                RequestCancellation();
            });
            try
            {
                await command.ExecuteAsync(
                    new StructuralRecoveryOptions(
                        recoverySnapshotPath,
                        statePath,
                        recoveryAnnotationsPath,
                        diagnosticsPath,
                        recoveryOutputPath,
                        profile,
                        new StructuralScanOptions(),
                        ConfirmedHeadingsMarkdownPath: confirmedHeadingsPath),
                    headingExecutors,
                    semanticExecutors,
                    cancellationSource.Token);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
            {
                logger.LogInformation("Structural scan cancelled. Run the same command to resume from the saved checkpoint.");
                return 130;
            }
            catch (SemanticWindowFailureException exception)
            {
                logger.LogError(
                    "Structural scan paused: {Message} Resume from the saved checkpoint after correcting the model response. Diagnostics: {DiagnosticsPath}",
                    exception.Message,
                    diagnosticsPath);
                return 2;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        Console.Error.WriteLine(
            "Usage: TocCreator import <input.txt> <snapshot.json> | export <snapshot.json> <output.md> [annotations.json] | recover <snapshot.json> <state.json> <annotations.json> <diagnostics.json> <output.md> <endpoint> <model> <api-key>");
        return 1;
    }
}
