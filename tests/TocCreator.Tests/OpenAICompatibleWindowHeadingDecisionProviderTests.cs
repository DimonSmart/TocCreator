using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TocCreator.Tests;

public sealed class OpenAICompatibleHeadingSemanticProviderTests
{
    [Fact]
    public async Task Candidate_detector_sends_contextual_model_visible_payload_without_stable_ids()
    {
        var handler = new RecordingHandler(ChatResponse("""{"candidates":[2]}"""));
        var profile = Profile(temperature: 0.2f);
        var provider = new OpenAICompatibleHeadingCandidateDetectionProvider(new HttpClient(handler));
        var request = new HeadingCandidateDetectionRequest(
            [new SemanticBreadcrumb(1, "Chapter One")],
            [new HeadingContextParagraph("previous body", 10)],
            [
                new HeadingDecisionParagraph(1, "ordinary body", true, null, null, 11),
                new HeadingDecisionParagraph(2, "SECTION", true, null, null, 12)
            ],
            [new HeadingContextParagraph("following body", 13)],
            100);

        var response = await provider.DecideAsync(new SemanticProviderRequest<HeadingCandidateDetectionRequest>(
            profile,
            request,
            HeadingSemanticOperationSchemas.CandidateDetection));

        Assert.Equal([2], response.Result.Candidates);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-value", handler.AuthorizationParameter);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.RequestUri);
        Assert.Equal(0.2, handler.Body.RootElement.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal("json_schema", handler.Body.RootElement.GetProperty("response_format").GetProperty("type").GetString());

        var system = handler.Body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("recall", system, StringComparison.OrdinalIgnoreCase);
        var input = JsonDocument.Parse(handler.Body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal("previous body", input.RootElement.GetProperty("paragraphsBefore")[0].GetProperty("text").GetString());
        Assert.Equal("ordinary body", input.RootElement.GetProperty("paragraphs")[0].GetProperty("text").GetString());
        Assert.Equal("following body", input.RootElement.GetProperty("paragraphsAfter")[0].GetProperty("text").GetString());
        Assert.Equal("Chapter One", input.RootElement.GetProperty("breadcrumbs")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("pointerId", input.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("p000000", input.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifier_returns_independent_accept_and_level_result()
    {
        var handler = new RecordingHandler(ChatResponse("""{"accept":true,"level":2}"""));
        var provider = new OpenAICompatibleHeadingCandidateVerificationProvider(new HttpClient(handler));
        var request = new HeadingCandidateVerificationRequest(
            [new SemanticBreadcrumb(1, "Chapter")],
            "Section",
            [new HeadingContextParagraph("before", 2)],
            [new HeadingContextParagraph("after", 4)],
            3,
            10);

        var response = await provider.DecideAsync(new SemanticProviderRequest<HeadingCandidateVerificationRequest>(
            Profile(),
            request,
            HeadingSemanticOperationSchemas.CandidateVerification));

        Assert.True(response.Result.Accept);
        Assert.Equal(2, response.Result.Level);
        var system = handler.Body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("exactly one", system, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsupported_profile_options_are_omitted_and_malformed_json_is_not_recovered()
    {
        var handler = new RecordingHandler(ChatResponse("""{"candidates":[]}"""));
        var profile = Profile(includeJsonSchema: false, supportsStructuredOutput: false, supportsTemperature: false);
        var provider = new OpenAICompatibleHeadingCandidateDetectionProvider(new HttpClient(handler));
        var request = DetectionRequest();

        await provider.DecideAsync(new SemanticProviderRequest<HeadingCandidateDetectionRequest>(
            profile,
            request,
            HeadingSemanticOperationSchemas.CandidateDetection));

        Assert.False(handler.Body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(handler.Body.RootElement.TryGetProperty("response_format", out _));

        var malformed = new OpenAICompatibleHeadingCandidateDetectionProvider(
            new HttpClient(new RecordingHandler("""{"choices":[{"message":{"content":"not-json"}}]}""")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            malformed.DecideAsync(new SemanticProviderRequest<HeadingCandidateDetectionRequest>(
                Profile(),
                request,
                null)));

        Assert.DoesNotContain("not-json", exception.Message);
        Assert.DoesNotContain("secret-value", exception.ToString());
    }

    [Fact]
    public void Application_validators_reject_duplicate_candidates_and_invalid_accept_level_pairs()
    {
        var request = DetectionRequest();

        Assert.Throws<ArgumentException>(() =>
            HeadingSemanticResultValidator.CandidateDetection(
                request,
                new HeadingCandidateDetectionResult([1, 1])));

        Assert.Throws<ArgumentException>(() =>
            HeadingSemanticResultValidator.CandidateVerification(
                VerificationRequest(),
                new HeadingCandidateVerificationResult(false, 2)));

        Assert.Throws<ArgumentException>(() =>
            HeadingSemanticResultValidator.CandidateVerification(
                VerificationRequest(),
                new HeadingCandidateVerificationResult(true)));

        HeadingSemanticResultValidator.CandidateVerification(
            VerificationRequest(),
            new HeadingCandidateVerificationResult(true, 3));
    }

    [Fact]
    public async Task Transport_error_does_not_expose_credentials()
    {
        var provider = new OpenAICompatibleHeadingCandidateDetectionProvider(
            new HttpClient(new RecordingHandler("unavailable", HttpStatusCode.BadGateway)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.DecideAsync(new SemanticProviderRequest<HeadingCandidateDetectionRequest>(
                Profile(),
                DetectionRequest(),
                null)));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.DoesNotContain("secret-value", exception.ToString());
        Assert.DoesNotContain("unavailable", exception.Message);
    }

    private static HeadingCandidateDetectionRequest DetectionRequest() => new(
        [],
        [],
        [new HeadingDecisionParagraph(1, "Chapter", true, null, null, 0)],
        [],
        1);

    private static HeadingCandidateVerificationRequest VerificationRequest() =>
        new([], "Chapter", [], [], 0, 1);

    private static OpenAICompatibleModelProfile Profile(
        bool includeJsonSchema = true,
        bool supportsStructuredOutput = true,
        bool supportsTemperature = true,
        float? temperature = null) =>
        new(
            "local",
            new Uri("http://localhost:11434/v1/"),
            "gpt-oss:20b",
            new OpenAICompatibleCredentials("secret-value"),
            includeJsonSchema,
            supportsStructuredOutput,
            supportsTemperature,
            temperature);

    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    private sealed class RecordingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? RequestUri { get; private set; }
        public JsonDocument Body { get; private set; } = null!;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestUri = request.RequestUri!.ToString();
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
