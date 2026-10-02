using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TocCreator;

/// <summary>Files and provider settings required to recover a snapshot's structure.</summary>
public sealed record StructuralRecoveryOptions(
    string SnapshotPath,
    string StatePath,
    string AnnotationsPath,
    string DiagnosticsPath,
    string MarkdownPath,
    OpenAICompatibleModelProfile Profile,
    StructuralScanOptions ScanOptions,
    StructuralRecoveryRepairOptions? RepairOptions = null);

/// <summary>
/// Runs deterministic TOC discovery, then resumes or completes the application-owned
/// structural scan. The semantic provider is used only by the scanner.
/// </summary>
public sealed class StructuralRecoveryCommand(
    SnapshotStore snapshotStore,
    StructuralScanStateStore stateStore,
    AnnotationStore annotationStore,
    StructuralDiscovery discovery,
    StreamingStructuralScanner scanner,
    MarkdownExporter markdownExporter,
    ILogger<StructuralRecoveryCommand> logger)
{
    public async Task ExecuteAsync(StructuralRecoveryOptions options, IStructuralDecisionExecutor executor, CancellationToken cancellationToken = default)
        => await ExecuteCoreAsync(options, executor, null, cancellationToken);

    public async Task ExecuteAsync(StructuralRecoveryOptions options, IStructuralDecisionExecutor windowHeadingExecutor, StructuralRecoverySemanticExecutors semanticExecutors, CancellationToken cancellationToken = default)
        => await ExecuteCoreAsync(options, windowHeadingExecutor, semanticExecutors, cancellationToken);

    private async Task ExecuteCoreAsync(StructuralRecoveryOptions options, IStructuralDecisionExecutor executor, StructuralRecoverySemanticExecutors? semanticExecutors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executor);
        options.ScanOptions.Validate();

        logger.LogInformation("Starting structural recovery for snapshot {SnapshotPath}.", options.SnapshotPath);
        var snapshot = await snapshotStore.LoadAsync(options.SnapshotPath, cancellationToken);
        var diagnostics = new JsonFileSemanticDiagnosticSink(options.DiagnosticsPath);
        StructuralScanState state;
        if (File.Exists(options.StatePath))
        {
            state = await stateStore.LoadAsync(options.StatePath, cancellationToken);
            logger.LogInformation("Resuming structural scan at cursor {Cursor} and committed boundary {CommittedBoundary}.", state.Cursor, state.CommittedBoundary);
        }
        else
        {
            var tocDiscovery = semanticExecutors is null
                ? discovery.Discover(snapshot)
                : await discovery.DiscoverAsync(snapshot, semanticExecutors, diagnostics, cancellationToken);
            state = StructuralScanState.Start(snapshot, tocDiscovery);
            await stateStore.SaveAsync(options.StatePath, state, cancellationToken);
            logger.LogInformation("Created structural scan state from deterministic TOC discovery with {TocEntryCount} entries.", tocDiscovery.Entries.Count);
        }

        if (state.Checkpoint == StructuralRecoveryCheckpoint.Scanning)
        {
            state = await scanner.ScanToCompletionAsync(snapshot, state, options.ScanOptions, executor, stateStore, options.StatePath, diagnostics, cancellationToken: cancellationToken);
            state = state with { Checkpoint = StructuralRecoveryCheckpoint.StructuralValidation };
            await stateStore.SaveAsync(options.StatePath, state, cancellationToken);
        }
        if (state.Checkpoint == StructuralRecoveryCheckpoint.StructuralValidation)
        {
            var repair = new StructuralRecoveryRepairWorkflow(new StructuralRecoveryValidator());
            state = await repair.ValidateAndRepairAsync(snapshot, state, options.RepairOptions ?? new StructuralRecoveryRepairOptions(), semanticExecutors?.AnomalyReview, stateStore, options.StatePath, diagnostics, cancellationToken);
            state = state with { Checkpoint = StructuralRecoveryCheckpoint.Completed };
            await stateStore.SaveAsync(options.StatePath, state, cancellationToken);
        }

        var annotations = new AnnotationSet(state.AcceptedAnnotations);
        await annotationStore.SaveAsync(options.AnnotationsPath, annotations, cancellationToken);
        await File.WriteAllBytesAsync(options.MarkdownPath, markdownExporter.Export(snapshot, annotations), cancellationToken);
        logger.LogInformation("Completed structural recovery with {AnnotationCount} annotations and {DiagnosticCount} semantic diagnostics.", annotations.Items.Count, diagnostics.Count);
    }
}

/// <summary>Persists structured semantic-call evidence. Raw provider messages are never retained by this sink.</summary>
public sealed class JsonFileSemanticDiagnosticSink : ISemanticDiagnosticSink, ISemanticOperationDiagnosticSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string path;
    private readonly List<JsonElement> diagnostics;

    public JsonFileSemanticDiagnosticSink(string path)
    {
        this.path = path;
        diagnostics = File.Exists(path)
            ? JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(path), JsonOptions) ?? []
            : [];
    }

    public int Count => diagnostics.Count;

    public void Record(SemanticDecisionDiagnostic diagnostic)
    {
        // Retaining model transcripts requires an explicit debug-only path; recovery files stay safe by default.
        diagnostics.Add(JsonSerializer.SerializeToElement(diagnostic with { RawPrompt = null, RawResponse = null }, JsonOptions));
        File.WriteAllText(path, JsonSerializer.Serialize(diagnostics, JsonOptions));
    }

    public void Record(SemanticOperationDiagnostic diagnostic)
    {
        diagnostics.Add(JsonSerializer.SerializeToElement(diagnostic, JsonOptions));
        File.WriteAllText(path, JsonSerializer.Serialize(diagnostics, JsonOptions));
    }
}
