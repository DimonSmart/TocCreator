using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI.Workflows;

namespace TocCreator;

/// <summary>Credentials for an OpenAI-compatible endpoint. The value is never written to scan state or diagnostics.</summary>
public sealed record OpenAICompatibleCredentials(string ApiKey);

/// <summary>Provider-neutral configuration for a local or remote OpenAI-compatible model.</summary>
public sealed record OpenAICompatibleModelProfile(
    string Name,
    Uri Endpoint,
    string Model,
    OpenAICompatibleCredentials Credentials,
    bool IncludeJsonSchema = true,
    bool SupportsStructuredOutput = true,
    bool SupportsTemperature = true,
    float? Temperature = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !Endpoint.IsAbsoluteUri || string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(Credentials.ApiKey))
        {
            throw new ArgumentException("A model profile requires a name, absolute endpoint, model, and credentials.");
        }
        if (Temperature is not null && !SupportsTemperature)
        {
            throw new ArgumentException("This model profile does not support temperature.");
        }
    }
}

/// <summary>Typed provider boundary. An implementation may use any OpenAI-compatible runtime.</summary>
public sealed record WindowHeadingProviderRequest(OpenAICompatibleModelProfile Profile, StructuralDecisionRequest Input, JsonDocument? JsonSchema);
public sealed record WindowHeadingProviderResponse(StructuralDecisionResult Result, string? RawPrompt = null, string? RawResponse = null);

public interface IWindowHeadingDecisionProvider
{
    Task<WindowHeadingProviderResponse> DecideAsync(WindowHeadingProviderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>HTTP provider for the OpenAI chat-completions contract.</summary>
public sealed class OpenAICompatibleWindowHeadingDecisionProvider : IWindowHeadingDecisionProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient httpClient;

    public OpenAICompatibleWindowHeadingDecisionProvider(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
    }

    public async Task<WindowHeadingProviderResponse> DecideAsync(WindowHeadingProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Profile.Validate();

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(request.Profile.Endpoint, "chat/completions"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Profile.Credentials.ApiKey);
        message.Content = JsonContent.Create(CreateRequestBody(request), options: JsonOptions);

        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenAI-compatible request failed with status code {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return new WindowHeadingProviderResponse(ParseResult(responseJson, request.Input));
    }

    private static object CreateRequestBody(WindowHeadingProviderRequest request)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Profile.Model,
            ["messages"] = new[]
            {
                new
                {
                    role = "system",
                    content = "Select headings only from the supplied numbered paragraphs. Return one JSON object and nothing else: each property name is a selected paragraph number and its integer value is the heading level 1, 2, or 3. Omit ordinary body paragraphs. Return {} when there are no headings. Never use Markdown fences. currentLevel and ancestorLevels describe the current structure."
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
                    {
                        currentLevel = request.Input.CurrentHeadingLevel,
                        ancestorLevels = request.Input.ActiveHeadingStack.Select(item => item.Level),
                        paragraphs = request.Input.DecisionZone.Select((pointer, index) => new { number = index + 1, text = pointer.Text })
                    }, JsonOptions)
                }
            }
        };

        if (request.Profile.SupportsTemperature && request.Profile.Temperature is not null)
        {
            body["temperature"] = request.Profile.Temperature;
        }

        if (request.Profile.IncludeJsonSchema && request.Profile.SupportsStructuredOutput && request.JsonSchema is not null)
        {
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "window_heading_decision", strict = true, schema = request.JsonSchema.RootElement }
            };
        }

        return body;
    }

    private static StructuralDecisionResult ParseResult(string responseJson, StructuralDecisionRequest input)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var choices = document.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
            {
                throw new InvalidDataException("OpenAI-compatible response contains no choices.");
            }

            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException("OpenAI-compatible response has no structured content.");
            }

            var levels = JsonSerializer.Deserialize<Dictionary<string, int>>(content, JsonOptions)
                ?? throw new InvalidDataException("OpenAI-compatible response contains no pointer-level result.");
            var decisions = new List<StructuralDecision>();
            foreach (var item in levels)
            {
                if (!int.TryParse(item.Key, out var number) || number < 1 || number > input.DecisionZone.Count)
                    throw new InvalidDataException($"Window heading result contains unknown local paragraph number '{item.Key}'.");
                var pointerId = input.DecisionZone[number - 1].PointerId;
                decisions.Add(new StructuralDecision(
                    pointerId,
                    StructuralDecisionKind.Annotate,
                    new StructuralAnnotation { PointerId = pointerId, Role = StructuralRole.Heading, HeadingLevel = item.Value }));
            }
            return new StructuralDecisionResult(decisions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("OpenAI-compatible response is not valid structured JSON.", exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidDataException("OpenAI-compatible response does not match the chat-completions contract.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("OpenAI-compatible response does not match the chat-completions contract.", exception);
        }
    }
}

/// <summary>
/// The sole Agent Framework executor for a scan-window heading decision. It receives no book memory beyond the typed local request.
/// Application code, not this executor, validates and commits the returned decision.
/// </summary>
public sealed class LocalWindowHeadingDecisionExecutor : Executor<StructuralDecisionRequest, StructuralDecisionResult>, IStructuralDecisionExecutor, ISemanticDecisionIdentity, ISemanticRawTranscriptSource
{
    private readonly IWindowHeadingDecisionProvider provider;
    private readonly OpenAICompatibleModelProfile profile;
    private readonly bool retainRawMessages;
    private WindowHeadingProviderResponse? lastResponse;

    public LocalWindowHeadingDecisionExecutor(OpenAICompatibleModelProfile profile, IWindowHeadingDecisionProvider provider, bool retainRawMessages = false)
        : base("window-heading-decision", options: null, declareCrossRunShareable: false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(provider);
        profile.Validate();
        this.profile = profile;
        this.provider = provider;
        this.retainRawMessages = retainRawMessages;
    }

    public string ProfileName => profile.Name;
    public string Model => profile.Model;

    public async Task<StructuralDecisionResult> DecideAsync(StructuralDecisionRequest request, CancellationToken cancellationToken = default)
    {
        using var schema = profile.IncludeJsonSchema ? CreateDecisionSchema(request) : null;
        var response = await provider.DecideAsync(new WindowHeadingProviderRequest(profile, request, schema), cancellationToken);
        if (retainRawMessages) lastResponse = response;
        return response.Result;
    }

    public override async ValueTask<StructuralDecisionResult> HandleAsync(StructuralDecisionRequest input, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await DecideAsync(input, cancellationToken);

    public bool TryGetRawTranscript(out string? rawPrompt, out string? rawResponse)
    {
        rawPrompt = lastResponse?.RawPrompt;
        rawResponse = lastResponse?.RawResponse;
        return rawPrompt is not null || rawResponse is not null;
    }

    private static JsonDocument CreateDecisionSchema(StructuralDecisionRequest request) => JsonSerializer.SerializeToDocument(new
    {
        type = "object",
        properties = request.DecisionZone.Select((_, index) => index + 1).ToDictionary(
            number => number.ToString(),
            _ => (object)new { type = "integer", minimum = 1, maximum = 3 },
            StringComparer.Ordinal),
        additionalProperties = false
    });
}
