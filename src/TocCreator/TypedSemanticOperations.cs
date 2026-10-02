using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI.Workflows;

namespace TocCreator;

/// <summary>Names one narrow semantic judgment. Application code owns all state changes after a valid result is returned.</summary>
public enum SemanticOperationKind
{
    TocDetection,
    TocParsing,
    AmbiguousHeadingMatch,
    AmbiguousNoiseClassification,
    WindowHeadingCandidateDetection,
    HeadingCandidateVerification,
    ValidatedAnomalyReview
}

/// <summary>Explicit local ownership supplied to a semantic operation. Read-only pointers can never be selected for a change.</summary>
public sealed record SemanticOperationContext(
    IReadOnlyList<SemanticPointer> ReadOnlyPointers,
    IReadOnlyList<SemanticPointer> WritablePointers)
{
    public IReadOnlyList<string> SuppliedPointerIds => ReadOnlyPointers.Concat(WritablePointers).Select(pointer => pointer.PointerId).ToArray();
    public IReadOnlyList<string> WritablePointerIds => WritablePointers.Select(pointer => pointer.PointerId).ToArray();

    public void Validate()
    {
        var all = SuppliedPointerIds;
        if (all.Any(string.IsNullOrWhiteSpace) || all.Distinct(StringComparer.Ordinal).Count() != all.Count)
            throw new ArgumentException("Local semantic context must contain unique, non-empty pointer identifiers.");
    }
}

public sealed record TocDetectionRequest(SemanticOperationContext Context);
public sealed record TocDetectionResult(bool IsTableOfContents, string? StartPointerId = null, string? EndPointerId = null);

public sealed record TocParsingRequest(SemanticOperationContext Context);
public sealed record ParsedTocEntry(string SourcePointerId, string Title, int Level, int? PrintedPageNumber);
public sealed record TocParsingResult(IReadOnlyList<ParsedTocEntry> Entries);

public sealed record AmbiguousHeadingMatchRequest(SemanticOperationContext Context, TocEntry ExpectedEntry);
public sealed record AmbiguousHeadingMatchResult(string? SelectedPointerId, int? HeadingLevel);

public sealed record AmbiguousNoiseClassificationRequest(SemanticOperationContext Context);
public sealed record NoiseClassification(string PointerId, bool IsNoise);
public sealed record AmbiguousNoiseClassificationResult(IReadOnlyList<NoiseClassification> Decisions);

public sealed record ValidatedAnomaly(string PointerId, string Code, string Description);
public sealed record ValidatedAnomalyReviewRequest(SemanticOperationContext Context, IReadOnlyList<ValidatedAnomaly> Anomalies);
public enum AnomalyReviewDisposition { Keep, Dismiss }
public sealed record AnomalyReview(string PointerId, AnomalyReviewDisposition Disposition, string? Reason = null);
public sealed record ValidatedAnomalyReviewResult(IReadOnlyList<AnomalyReview> Reviews);

/// <summary>Typed provider boundary for one semantic operation. It deliberately has no source snapshot or structural state.</summary>
public sealed record SemanticProviderRequest<TRequest>(OpenAICompatibleModelProfile Profile, TRequest Input, JsonDocument? JsonSchema);
public sealed record SemanticProviderResponse<TResult>(TResult Result, string? RawPrompt = null, string? RawResponse = null);

public interface ITocDetectionProvider { Task<SemanticProviderResponse<TocDetectionResult>> DecideAsync(SemanticProviderRequest<TocDetectionRequest> request, CancellationToken cancellationToken = default); }
public interface ITocParsingProvider { Task<SemanticProviderResponse<TocParsingResult>> DecideAsync(SemanticProviderRequest<TocParsingRequest> request, CancellationToken cancellationToken = default); }
public interface IAmbiguousHeadingMatchProvider { Task<SemanticProviderResponse<AmbiguousHeadingMatchResult>> DecideAsync(SemanticProviderRequest<AmbiguousHeadingMatchRequest> request, CancellationToken cancellationToken = default); }
public interface IAmbiguousNoiseClassificationProvider { Task<SemanticProviderResponse<AmbiguousNoiseClassificationResult>> DecideAsync(SemanticProviderRequest<AmbiguousNoiseClassificationRequest> request, CancellationToken cancellationToken = default); }
public interface IValidatedAnomalyReviewProvider { Task<SemanticProviderResponse<ValidatedAnomalyReviewResult>> DecideAsync(SemanticProviderRequest<ValidatedAnomalyReviewRequest> request, CancellationToken cancellationToken = default); }

/// <summary>Common OpenAI-compatible transport; subclasses expose operation-specific typed contracts.</summary>
public abstract class OpenAICompatibleSemanticProvider<TRequest, TResult>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient httpClient;
    private readonly SemanticOperationKind operation;

    protected OpenAICompatibleSemanticProvider(HttpClient httpClient, SemanticOperationKind operation)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.operation = operation;
    }

    protected virtual string SystemPrompt =>
        $"Perform only the {operation} judgment on the supplied local context. Return the requested JSON result.";

    protected async Task<SemanticProviderResponse<TResult>> SendAsync(SemanticProviderRequest<TRequest> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Profile.Validate();
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(request.Profile.Endpoint, "chat/completions"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Profile.Credentials.ApiKey);
        message.Content = JsonContent.Create(CreateRequestBody(request), options: JsonOptions);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI-compatible request failed with status code {(int)response.StatusCode}.", null, response.StatusCode);

        var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            var choices = document.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) throw new InvalidDataException("OpenAI-compatible response contains no choices.");
            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("OpenAI-compatible response has no structured content.");
            return new SemanticProviderResponse<TResult>(JsonSerializer.Deserialize<TResult>(content, JsonOptions) ?? throw new InvalidDataException("OpenAI-compatible response contains no typed result."), null, rawResponse);
        }
        catch (JsonException exception) { throw new InvalidDataException("OpenAI-compatible response is not valid structured JSON.", exception); }
        catch (KeyNotFoundException exception) { throw new InvalidDataException("OpenAI-compatible response does not match the chat-completions contract.", exception); }
        catch (InvalidOperationException exception) { throw new InvalidDataException("OpenAI-compatible response does not match the chat-completions contract.", exception); }
    }

    private object CreateRequestBody(SemanticProviderRequest<TRequest> request)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Profile.Model,
            ["messages"] = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(request.Input, JsonOptions) }
            }
        };
        if (request.Profile.SupportsTemperature && request.Profile.Temperature is not null) body["temperature"] = request.Profile.Temperature;
        if (request.Profile.IncludeJsonSchema && request.Profile.SupportsStructuredOutput && request.JsonSchema is not null)
            body["response_format"] = new { type = "json_schema", json_schema = new { name = operation.ToString(), strict = true, schema = request.JsonSchema.RootElement } };
        return body;
    }
}

public sealed class OpenAICompatibleTocDetectionProvider(HttpClient httpClient) : OpenAICompatibleSemanticProvider<TocDetectionRequest, TocDetectionResult>(httpClient, SemanticOperationKind.TocDetection), ITocDetectionProvider
{ public Task<SemanticProviderResponse<TocDetectionResult>> DecideAsync(SemanticProviderRequest<TocDetectionRequest> request, CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken); }
public sealed class OpenAICompatibleTocParsingProvider(HttpClient httpClient) : OpenAICompatibleSemanticProvider<TocParsingRequest, TocParsingResult>(httpClient, SemanticOperationKind.TocParsing), ITocParsingProvider
{ public Task<SemanticProviderResponse<TocParsingResult>> DecideAsync(SemanticProviderRequest<TocParsingRequest> request, CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken); }
public sealed class OpenAICompatibleAmbiguousHeadingMatchProvider(HttpClient httpClient) : OpenAICompatibleSemanticProvider<AmbiguousHeadingMatchRequest, AmbiguousHeadingMatchResult>(httpClient, SemanticOperationKind.AmbiguousHeadingMatch), IAmbiguousHeadingMatchProvider
{ public Task<SemanticProviderResponse<AmbiguousHeadingMatchResult>> DecideAsync(SemanticProviderRequest<AmbiguousHeadingMatchRequest> request, CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken); }
public sealed class OpenAICompatibleAmbiguousNoiseClassificationProvider(HttpClient httpClient) : OpenAICompatibleSemanticProvider<AmbiguousNoiseClassificationRequest, AmbiguousNoiseClassificationResult>(httpClient, SemanticOperationKind.AmbiguousNoiseClassification), IAmbiguousNoiseClassificationProvider
{ public Task<SemanticProviderResponse<AmbiguousNoiseClassificationResult>> DecideAsync(SemanticProviderRequest<AmbiguousNoiseClassificationRequest> request, CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken); }
public sealed class OpenAICompatibleValidatedAnomalyReviewProvider(HttpClient httpClient) : OpenAICompatibleSemanticProvider<ValidatedAnomalyReviewRequest, ValidatedAnomalyReviewResult>(httpClient, SemanticOperationKind.ValidatedAnomalyReview), IValidatedAnomalyReviewProvider
{ public Task<SemanticProviderResponse<ValidatedAnomalyReviewResult>> DecideAsync(SemanticProviderRequest<ValidatedAnomalyReviewRequest> request, CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken); }

public interface ISemanticOperationExecutor<TRequest, TResult>
{
    Task<TResult> DecideAsync(TRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Operation-specific executors available to structural recovery. They hold no snapshot or scan state.</summary>
public sealed record StructuralRecoverySemanticExecutors(
    ISemanticOperationExecutor<TocDetectionRequest, TocDetectionResult> TocDetection,
    ISemanticOperationExecutor<TocParsingRequest, TocParsingResult> TocParsing,
    ISemanticOperationExecutor<AmbiguousHeadingMatchRequest, AmbiguousHeadingMatchResult> HeadingMatch,
    ISemanticOperationExecutor<AmbiguousNoiseClassificationRequest, AmbiguousNoiseClassificationResult> NoiseClassification,
    ISemanticOperationExecutor<ValidatedAnomalyReviewRequest, ValidatedAnomalyReviewResult>? AnomalyReview = null);

/// <summary>Agent Framework executor base: invokes one provider contract and retains no book state.</summary>
public abstract class SemanticOperationExecutor<TRequest, TResult> : Executor<TRequest, TResult>, ISemanticOperationExecutor<TRequest, TResult>, ISemanticDecisionIdentity, ISemanticRawTranscriptSource
{
    private readonly OpenAICompatibleModelProfile profile;
    private readonly bool retainRawMessages;
    private SemanticProviderResponse<TResult>? lastResponse;

    protected SemanticOperationExecutor(string name, OpenAICompatibleModelProfile profile, bool retainRawMessages = false) : base(name, options: null, declareCrossRunShareable: false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        this.profile = profile;
        this.retainRawMessages = retainRawMessages;
    }

    public string ProfileName => profile.Name;
    public string Model => profile.Model;
    protected OpenAICompatibleModelProfile Profile => profile;
    protected abstract JsonDocument Schema { get; }
    protected abstract Task<SemanticProviderResponse<TResult>> InvokeAsync(SemanticProviderRequest<TRequest> request, CancellationToken cancellationToken);
    public async Task<TResult> DecideAsync(TRequest request, CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync(new SemanticProviderRequest<TRequest>(profile, request, profile.IncludeJsonSchema ? Schema : null), cancellationToken);
        if (retainRawMessages) lastResponse = response;
        return response.Result;
    }
    public override async ValueTask<TResult> HandleAsync(TRequest input, IWorkflowContext context, CancellationToken cancellationToken = default) => await DecideAsync(input, cancellationToken);
    public bool TryGetRawTranscript(out string? rawPrompt, out string? rawResponse) { rawPrompt = lastResponse?.RawPrompt; rawResponse = lastResponse?.RawResponse; return rawPrompt is not null || rawResponse is not null; }
}

public sealed class TocDetectionExecutor(OpenAICompatibleModelProfile profile, ITocDetectionProvider provider, bool retainRawMessages = false) : SemanticOperationExecutor<TocDetectionRequest, TocDetectionResult>("toc-detection", profile, retainRawMessages)
{ protected override JsonDocument Schema => SemanticOperationSchemas.TocDetection; protected override Task<SemanticProviderResponse<TocDetectionResult>> InvokeAsync(SemanticProviderRequest<TocDetectionRequest> request, CancellationToken cancellationToken) => provider.DecideAsync(request, cancellationToken); }
public sealed class TocParsingExecutor(OpenAICompatibleModelProfile profile, ITocParsingProvider provider, bool retainRawMessages = false) : SemanticOperationExecutor<TocParsingRequest, TocParsingResult>("toc-parsing", profile, retainRawMessages)
{ protected override JsonDocument Schema => SemanticOperationSchemas.TocParsing; protected override Task<SemanticProviderResponse<TocParsingResult>> InvokeAsync(SemanticProviderRequest<TocParsingRequest> request, CancellationToken cancellationToken) => provider.DecideAsync(request, cancellationToken); }
public sealed class AmbiguousHeadingMatchExecutor(OpenAICompatibleModelProfile profile, IAmbiguousHeadingMatchProvider provider, bool retainRawMessages = false) : SemanticOperationExecutor<AmbiguousHeadingMatchRequest, AmbiguousHeadingMatchResult>("ambiguous-heading-match", profile, retainRawMessages)
{ protected override JsonDocument Schema => SemanticOperationSchemas.HeadingMatch; protected override Task<SemanticProviderResponse<AmbiguousHeadingMatchResult>> InvokeAsync(SemanticProviderRequest<AmbiguousHeadingMatchRequest> request, CancellationToken cancellationToken) => provider.DecideAsync(request, cancellationToken); }
public sealed class AmbiguousNoiseClassificationExecutor(OpenAICompatibleModelProfile profile, IAmbiguousNoiseClassificationProvider provider, bool retainRawMessages = false) : SemanticOperationExecutor<AmbiguousNoiseClassificationRequest, AmbiguousNoiseClassificationResult>("ambiguous-noise-classification", profile, retainRawMessages)
{ protected override JsonDocument Schema => SemanticOperationSchemas.Noise; protected override Task<SemanticProviderResponse<AmbiguousNoiseClassificationResult>> InvokeAsync(SemanticProviderRequest<AmbiguousNoiseClassificationRequest> request, CancellationToken cancellationToken) => provider.DecideAsync(request, cancellationToken); }
public sealed class ValidatedAnomalyReviewExecutor(OpenAICompatibleModelProfile profile, IValidatedAnomalyReviewProvider provider, bool retainRawMessages = false) : SemanticOperationExecutor<ValidatedAnomalyReviewRequest, ValidatedAnomalyReviewResult>("validated-anomaly-review", profile, retainRawMessages)
{ protected override JsonDocument Schema => SemanticOperationSchemas.AnomalyReview; protected override Task<SemanticProviderResponse<ValidatedAnomalyReviewResult>> InvokeAsync(SemanticProviderRequest<ValidatedAnomalyReviewRequest> request, CancellationToken cancellationToken) => provider.DecideAsync(request, cancellationToken); }

/// <summary>Diagnostics for an isolated operation retry. They do not become structural state or output.</summary>
public sealed record SemanticOperationDiagnostic(SemanticOperationKind Operation, string WorkflowStep, string? ProfileName, string? Model, IReadOnlyList<string> SuppliedPointerIds, IReadOnlyList<string> CandidateIds, object? TypedResult, string? ValidationFailure, int RetryCount, TimeSpan Elapsed);
public interface ISemanticOperationDiagnosticSink { void Record(SemanticOperationDiagnostic diagnostic); }

/// <summary>Retries only a single operation after its own validation failure; no annotation application is performed here.</summary>
public sealed class ValidatedSemanticOperation<TRequest, TResult>
{
    private readonly SemanticOperationKind kind;
    private readonly string workflowStep;
    private readonly Func<TRequest, SemanticOperationContext> context;
    private readonly Action<TRequest, TResult> validate;
    public ValidatedSemanticOperation(SemanticOperationKind kind, string workflowStep, Func<TRequest, SemanticOperationContext> context, Action<TRequest, TResult> validate) { this.kind = kind; this.workflowStep = workflowStep; this.context = context; this.validate = validate; }
    public async Task<TResult> ExecuteAsync(TRequest request, ISemanticOperationExecutor<TRequest, TResult> executor, int maxAttempts = 2, ISemanticOperationDiagnosticSink? diagnostics = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(executor);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        context(request).Validate();
        Exception? finalFailure = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew(); TResult? result = default;
            try { result = await executor.DecideAsync(request, cancellationToken); validate(request, result); Record(diagnostics, executor, context(request), result, null, attempt, stopwatch.Elapsed); return result; }
            catch (Exception exception) when (exception is not OperationCanceledException) { finalFailure = exception; Record(diagnostics, executor, context(request), result, exception.Message, attempt, stopwatch.Elapsed); }
        }
        throw new InvalidDataException($"{kind} did not return a valid typed result after {maxAttempts} attempts.", finalFailure);
    }
    private void Record(ISemanticOperationDiagnosticSink? sink, ISemanticOperationExecutor<TRequest, TResult> executor, SemanticOperationContext local, TResult? result, string? failure, int retry, TimeSpan elapsed)
    {
        if (sink is null) return; var identity = executor as ISemanticDecisionIdentity;
        sink.Record(new SemanticOperationDiagnostic(kind, workflowStep, identity?.ProfileName, identity?.Model, local.SuppliedPointerIds, local.WritablePointerIds, result, failure, retry, elapsed));
    }
}

/// <summary>Application-side eligibility validation for operation results, before a caller can apply any structural change.</summary>
public static class SemanticOperationResultValidator
{
    public static void TocDetection(TocDetectionRequest request, TocDetectionResult result)
    {
        request.Context.Validate();
        var ids = PointerIds(request.Context.ReadOnlyPointers);
        if (!result.IsTableOfContents) { if (result.StartPointerId is not null || result.EndPointerId is not null) throw new ArgumentException("A negative TOC detection cannot name pointers."); return; }
        Require(ids, result.StartPointerId, "TOC start"); Require(ids, result.EndPointerId, "TOC end");
        if (IndexOf(request.Context.ReadOnlyPointers, result.StartPointerId!) > IndexOf(request.Context.ReadOnlyPointers, result.EndPointerId!)) throw new ArgumentException("TOC range is reversed.");
    }
    public static void TocParsing(TocParsingRequest request, TocParsingResult result)
    {
        request.Context.Validate();
        var ids = PointerIds(request.Context.ReadOnlyPointers); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in result.Entries) { Require(ids, entry.SourcePointerId, "TOC entry"); if (!seen.Add(entry.SourcePointerId)) throw new ArgumentException("TOC parsing contains duplicate source pointers."); if (string.IsNullOrWhiteSpace(entry.Title) || entry.Level is < 1 or > 3 || entry.PrintedPageNumber is < 1) throw new ArgumentException("TOC entry has invalid fields."); }
    }
    public static void HeadingMatch(AmbiguousHeadingMatchRequest request, AmbiguousHeadingMatchResult result)
    {
        request.Context.Validate();
        if (result.SelectedPointerId is null) { if (result.HeadingLevel is not null) throw new ArgumentException("An unmatched heading cannot have a level."); return; }
        Require(PointerIds(request.Context.WritablePointers), result.SelectedPointerId, "heading match"); if (result.HeadingLevel is < 1 or > 3) throw new ArgumentException("A heading level must be from 1 through 3.");
    }
    public static void Noise(AmbiguousNoiseClassificationRequest request, AmbiguousNoiseClassificationResult result)
    {
        request.Context.Validate();
        var ids = PointerIds(request.Context.WritablePointers); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in result.Decisions) { Require(ids, decision.PointerId, "noise classification"); if (!seen.Add(decision.PointerId)) throw new ArgumentException("Noise classification contains duplicate pointers."); }
    }
    public static void AnomalyReview(ValidatedAnomalyReviewRequest request, ValidatedAnomalyReviewResult result)
    {
        request.Context.Validate();
        var ids = request.Anomalies.Select(anomaly => anomaly.PointerId).ToHashSet(StringComparer.Ordinal); var writable = PointerIds(request.Context.WritablePointers); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var review in result.Reviews) { Require(ids, review.PointerId, "anomaly review"); Require(writable, review.PointerId, "anomaly review"); if (!seen.Add(review.PointerId)) throw new ArgumentException("Anomaly review contains duplicate pointers."); }
    }
    private static HashSet<string> PointerIds(IEnumerable<SemanticPointer> pointers) => pointers.Select(pointer => pointer.PointerId).ToHashSet(StringComparer.Ordinal);
    private static void Require(ISet<string> ids, string? pointerId, string purpose) { if (string.IsNullOrWhiteSpace(pointerId) || !ids.Contains(pointerId)) throw new ArgumentException($"{purpose} refers to an unknown or ineligible pointer '{pointerId}'."); }
    private static int IndexOf(IReadOnlyList<SemanticPointer> pointers, string pointerId) { for (var index = 0; index < pointers.Count; index++) if (pointers[index].PointerId == pointerId) return index; throw new ArgumentException("Unknown pointer."); }
}

public static class SemanticOperationSchemas
{
    public static JsonDocument TocDetection { get; } = JsonDocument.Parse("""{"type":"object","required":["isTableOfContents"],"properties":{"isTableOfContents":{"type":"boolean"},"startPointerId":{"type":"string"},"endPointerId":{"type":"string"}}}""");
    public static JsonDocument TocParsing { get; } = JsonDocument.Parse("""{"type":"object","required":["entries"],"properties":{"entries":{"type":"array"}}}""");
    public static JsonDocument HeadingMatch { get; } = JsonDocument.Parse("""{"type":"object","properties":{"selectedPointerId":{"type":["string","null"]},"headingLevel":{"type":["integer","null"],"minimum":1,"maximum":3}}}""");
    public static JsonDocument Noise { get; } = JsonDocument.Parse("""{"type":"object","required":["decisions"],"properties":{"decisions":{"type":"array"}}}""");
    public static JsonDocument AnomalyReview { get; } = JsonDocument.Parse("""{"type":"object","required":["reviews"],"properties":{"reviews":{"type":"array"}}}""");
}
