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
