using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TocCreator.Tests;

public sealed class OpenAICompatibleWindowHeadingDecisionProviderTests
{
    [Fact]
    public async Task Sends_supported_profile_options_and_returns_typed_result()
    {
        var handler = new RecordingHandler(ChatResponse("""{"1":1}"""));
        var profile = Profile(includeJsonSchema: true, supportsStructuredOutput: true, supportsTemperature: true, temperature: 0.2f);
        var provider = new OpenAICompatibleWindowHeadingDecisionProvider(new HttpClient(handler));

        var response = await provider.DecideAsync(new WindowHeadingProviderRequest(profile, Request(), JsonDocument.Parse("""{"type":"object"}""")));

        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-value", handler.AuthorizationParameter);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.RequestUri);
        Assert.Equal("gpt-oss:20b", handler.Body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.2, handler.Body.RootElement.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal("json_schema", handler.Body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        var input = JsonDocument.Parse(handler.Body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal(2, input.RootElement.GetProperty("currentLevel").GetInt32());
        Assert.Equal(1, input.RootElement.GetProperty("paragraphs")[0].GetProperty("number").GetInt32());
        Assert.Equal("Chapter", input.RootElement.GetProperty("paragraphs")[0].GetProperty("text").GetString());
        Assert.Equal(2, input.RootElement.GetProperty("ancestorLevels")[0].GetInt32());
        Assert.DoesNotContain("p00000001", input.RootElement.GetRawText());
        Assert.DoesNotContain("read-only", input.RootElement.GetRawText());
        Assert.DoesNotContain("look-ahead", input.RootElement.GetRawText());
        Assert.DoesNotContain("parent", input.RootElement.GetRawText());
        var decision = Assert.Single(response.Result.Decisions);
        Assert.Equal(StructuralDecisionKind.Annotate, decision.Kind);
        Assert.Equal(StructuralRole.Heading, decision.Annotation!.Role);
    }

    [Fact]
    public async Task Omits_options_the_profile_does_not_support()
    {
        var handler = new RecordingHandler(ChatResponse("""{}"""));
        var profile = Profile(includeJsonSchema: false, supportsStructuredOutput: false, supportsTemperature: false, temperature: null);
        var provider = new OpenAICompatibleWindowHeadingDecisionProvider(new HttpClient(handler));

        await provider.DecideAsync(new WindowHeadingProviderRequest(profile, Request(), JsonDocument.Parse("""{"type":"object"}""")));

        Assert.False(handler.Body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(handler.Body.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task Invalid_structured_content_throws_without_recovery()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"not-json"}}]}""");
        var provider = new OpenAICompatibleWindowHeadingDecisionProvider(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => provider.DecideAsync(new WindowHeadingProviderRequest(Profile(), Request(), null)));

        Assert.DoesNotContain("not-json", exception.Message);
        Assert.DoesNotContain("secret-value", exception.ToString());
    }

    [Fact]
    public async Task Unknown_local_paragraph_number_is_rejected()
    {
        var handler = new RecordingHandler(ChatResponse("""{"2":1}"""));
        var provider = new OpenAICompatibleWindowHeadingDecisionProvider(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.DecideAsync(new WindowHeadingProviderRequest(Profile(), Request(), null)));
    }

    [Fact]
    public async Task Transport_error_does_not_expose_credentials()
    {
        var handler = new RecordingHandler("unavailable", HttpStatusCode.BadGateway);
        var provider = new OpenAICompatibleWindowHeadingDecisionProvider(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecideAsync(new WindowHeadingProviderRequest(Profile(), Request(), null)));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.DoesNotContain("secret-value", exception.ToString());
        Assert.DoesNotContain("unavailable", exception.Message);
    }

    private static OpenAICompatibleModelProfile Profile(bool includeJsonSchema = true, bool supportsStructuredOutput = true, bool supportsTemperature = true, float? temperature = null) =>
        new("local", new Uri("http://localhost:11434/v1/"), "gpt-oss:20b", new OpenAICompatibleCredentials("secret-value"), includeJsonSchema, supportsStructuredOutput, supportsTemperature, temperature);

    private static StructuralDecisionRequest Request() => new(
        [new SemanticPointer("read-only", "Previous body", 0)],
        [new SemanticPointer("p00000001", "Chapter", 1)],
        [new SemanticPointer("look-ahead", "Future body", 2)],
        1,
        [new HeadingStackItem("parent", 2)],
        [], [], []);

    private static string ChatResponse(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    private sealed class RecordingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? RequestUri { get; private set; }
        public JsonDocument Body { get; private set; } = null!;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestUri = request.RequestUri!.ToString();
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
