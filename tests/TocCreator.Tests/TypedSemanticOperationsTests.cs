using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TocCreator.Tests;

public sealed class TypedSemanticOperationsTests
{
    [Fact]
    public void Validators_reject_unknown_and_read_only_pointer_results()
    {
        var context = Context();

        Assert.Throws<ArgumentException>(() => SemanticOperationResultValidator.HeadingMatch(
            new AmbiguousHeadingMatchRequest(context, new TocEntry("toc", "Chapter", 1, 1)),
            new AmbiguousHeadingMatchResult("read-only", 1)));
        Assert.Throws<ArgumentException>(() => SemanticOperationResultValidator.Noise(
            new AmbiguousNoiseClassificationRequest(context),
            new AmbiguousNoiseClassificationResult([new NoiseClassification("missing", true)])));
        Assert.Throws<ArgumentException>(() => SemanticOperationResultValidator.AnomalyReview(
            new ValidatedAnomalyReviewRequest(context, [new ValidatedAnomaly("writable", "gap", "Validated by deterministic code")]),
            new ValidatedAnomalyReviewResult([new AnomalyReview("read-only", AnomalyReviewDisposition.Keep)])));
    }

    [Fact]
    public async Task Invalid_result_is_retried_only_by_its_operation_with_diagnostic_evidence()
    {
        var context = Context();
        var executor = new DelegateExecutor<AmbiguousHeadingMatchRequest, AmbiguousHeadingMatchResult>([
            new AmbiguousHeadingMatchResult("read-only", 1),
            new AmbiguousHeadingMatchResult("writable", 2)]);
        var diagnostics = new CollectingDiagnostics();
        var operation = new ValidatedSemanticOperation<AmbiguousHeadingMatchRequest, AmbiguousHeadingMatchResult>(
            SemanticOperationKind.AmbiguousHeadingMatch, "toc-body-match", request => request.Context, SemanticOperationResultValidator.HeadingMatch);

        var result = await operation.ExecuteAsync(new AmbiguousHeadingMatchRequest(context, new TocEntry("toc", "Chapter", 1, 1)), executor, diagnostics: diagnostics);

        Assert.Equal("writable", result.SelectedPointerId);
        Assert.Equal(2, executor.CallCount);
        Assert.Equal(2, diagnostics.Items.Count);
        Assert.Contains("ineligible", diagnostics.Items[0].ValidationFailure!);
        Assert.Equal(0, diagnostics.Items[0].RetryCount);
        Assert.Equal(["writable"], diagnostics.Items[0].CandidateIds);
        Assert.Null(diagnostics.Items[1].ValidationFailure);
        Assert.Equal(1, diagnostics.Items[1].RetryCount);
    }

    [Fact]
    public void Malformed_toc_parse_and_reversed_detection_are_rejected_before_application()
    {
        SemanticPointer[] readOnly = [new SemanticPointer("first", "Contents", 0), new SemanticPointer("last", "Chapter 1 .... 1", 1)];
        var context = new SemanticOperationContext(readOnly, []);

        Assert.Throws<ArgumentException>(() => SemanticOperationResultValidator.TocDetection(new TocDetectionRequest(context), new TocDetectionResult(true, "last", "first")));
        Assert.Throws<ArgumentException>(() => SemanticOperationResultValidator.TocParsing(new TocParsingRequest(context), new TocParsingResult([new ParsedTocEntry("missing", "Chapter", 1, 1)])));
    }

    [Fact]
    public async Task Openai_compatible_toc_provider_sends_typed_request_and_rejects_malformed_content()
    {
        var handler = new RecordingHandler(ChatResponse("""{"isTableOfContents":true,"startPointerId":"first","endPointerId":"last"}"""));
        var provider = new OpenAICompatibleTocDetectionProvider(new HttpClient(handler));
        var response = await provider.DecideAsync(new SemanticProviderRequest<TocDetectionRequest>(Profile(), new TocDetectionRequest(new SemanticOperationContext([new SemanticPointer("first", "Contents", 0), new SemanticPointer("last", "Chapter", 1)], [])), SemanticOperationSchemas.TocDetection));

        Assert.True(response.Result.IsTableOfContents);
        Assert.Equal("gpt-oss:20b", handler.Body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_schema", handler.Body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains("TocDetection", handler.Body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

        var malformed = new OpenAICompatibleTocDetectionProvider(new HttpClient(new RecordingHandler("""{"choices":[{"message":{"content":"not-json"}}]}""")));
        await Assert.ThrowsAsync<InvalidDataException>(() => malformed.DecideAsync(new SemanticProviderRequest<TocDetectionRequest>(Profile(), new TocDetectionRequest(Context()), null)));
    }

    private static SemanticOperationContext Context() => new([new SemanticPointer("read-only", "Earlier context", 0)], [new SemanticPointer("writable", "Chapter one", 1)]);
    private static OpenAICompatibleModelProfile Profile() => new("local", new Uri("http://localhost:11434/v1/"), "gpt-oss:20b", new OpenAICompatibleCredentials("secret"));
    private static string ChatResponse(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    private sealed class DelegateExecutor<TRequest, TResult>(IReadOnlyList<TResult> results) : ISemanticOperationExecutor<TRequest, TResult>
    {
        private int index;
        public int CallCount => index;
        public Task<TResult> DecideAsync(TRequest request, CancellationToken cancellationToken = default) => Task.FromResult(results[index++]);
    }

    private sealed class CollectingDiagnostics : ISemanticOperationDiagnosticSink
    {
        public List<SemanticOperationDiagnostic> Items { get; } = [];
        public void Record(SemanticOperationDiagnostic diagnostic) => Items.Add(diagnostic);
    }

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public JsonDocument Body { get; private set; } = null!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
