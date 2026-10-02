using System.Text.Json;

namespace TocCreator;

public sealed record SemanticBreadcrumb(int Level, string Text);

public sealed record HeadingContextParagraph(
    string Text,
    int GlobalPosition,
    bool Truncated = false,
    int? SourcePage = null);

public sealed record HeadingDecisionParagraph(
    int Number,
    string Text,
    bool Selectable,
    string? KnownRole,
    int? KnownHeadingLevel,
    int GlobalPosition,
    bool Truncated = false,
    int? SourcePage = null);

public sealed record HeadingCandidateDetectionRequest(
    IReadOnlyList<SemanticBreadcrumb> Breadcrumbs,
    IReadOnlyList<HeadingContextParagraph> ParagraphsBefore,
    IReadOnlyList<HeadingDecisionParagraph> Paragraphs,
    IReadOnlyList<HeadingContextParagraph> ParagraphsAfter,
    int TotalPointerCount);

public sealed record HeadingCandidateDetectionResult(IReadOnlyList<int> Candidates);

public sealed record HeadingCandidateVerificationRequest(
    IReadOnlyList<SemanticBreadcrumb> Breadcrumbs,
    string CandidateText,
    IReadOnlyList<HeadingContextParagraph> ParagraphsBefore,
    IReadOnlyList<HeadingContextParagraph> ParagraphsAfter,
    int GlobalPosition,
    int TotalPointerCount,
    bool Truncated = false,
    int? SourcePage = null);

public sealed record HeadingCandidateVerificationResult(bool Accept, int? Level = null);

public interface IHeadingCandidateDetectionProvider
{
    Task<SemanticProviderResponse<HeadingCandidateDetectionResult>> DecideAsync(
        SemanticProviderRequest<HeadingCandidateDetectionRequest> request,
        CancellationToken cancellationToken = default);
}

public interface IHeadingCandidateVerificationProvider
{
    Task<SemanticProviderResponse<HeadingCandidateVerificationResult>> DecideAsync(
        SemanticProviderRequest<HeadingCandidateVerificationRequest> request,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAICompatibleHeadingCandidateDetectionProvider(HttpClient httpClient)
    : OpenAICompatibleSemanticProvider<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult>(
        httpClient,
        SemanticOperationKind.WindowHeadingCandidateDetection),
      IHeadingCandidateDetectionProvider
{
    protected override string SystemPrompt =>
        """
        Find possible structural headings in the numbered paragraphs. Use breadcrumbs, previous context, the full decision zone, and following context semantically. Prefer recall over precision: include a doubtful structural-heading hypothesis rather than miss a plausible heading. Select only paragraphs with selectable=true. Do not select previous/following context or already-confirmed non-selectable paragraphs. Do not infer heading levels and do not return explanations. Return only the typed JSON result.
        """;

    public Task<SemanticProviderResponse<HeadingCandidateDetectionResult>> DecideAsync(
        SemanticProviderRequest<HeadingCandidateDetectionRequest> request,
        CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken);
}

public sealed class OpenAICompatibleHeadingCandidateVerificationProvider(HttpClient httpClient)
    : OpenAICompatibleSemanticProvider<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult>(
        httpClient,
        SemanticOperationKind.HeadingCandidateVerification),
      IHeadingCandidateVerificationProvider
{
    protected override string SystemPrompt =>
        """
        Verify exactly one proposed structural-heading candidate. The candidate is only a hypothesis from a recall-oriented detector; do not assume it is a heading. Be conservative and distinguish structural headings from body text, dialogue, quotations, transitions, running headers, footers, and accidental short lines. Use confirmed breadcrumbs and local context before/after. If accepted, independently choose level 1, 2, or 3. Return only the typed JSON result and no explanation.
        """;

    public Task<SemanticProviderResponse<HeadingCandidateVerificationResult>> DecideAsync(
        SemanticProviderRequest<HeadingCandidateVerificationRequest> request,
        CancellationToken cancellationToken = default) => SendAsync(request, cancellationToken);
}

public sealed class HeadingCandidateDetectionExecutor(
    OpenAICompatibleModelProfile profile,
    IHeadingCandidateDetectionProvider provider,
    bool retainRawMessages = false)
    : SemanticOperationExecutor<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult>(
        "window-heading-candidate-detection",
        profile,
        retainRawMessages)
{
    protected override JsonDocument Schema => HeadingSemanticOperationSchemas.CandidateDetection;

    protected override Task<SemanticProviderResponse<HeadingCandidateDetectionResult>> InvokeAsync(
        SemanticProviderRequest<HeadingCandidateDetectionRequest> request,
        CancellationToken cancellationToken) => provider.DecideAsync(request, cancellationToken);
}

public sealed class HeadingCandidateVerificationExecutor(
    OpenAICompatibleModelProfile profile,
    IHeadingCandidateVerificationProvider provider,
    bool retainRawMessages = false)
    : SemanticOperationExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult>(
        "heading-candidate-verification",
        profile,
        retainRawMessages)
{
    protected override JsonDocument Schema => HeadingSemanticOperationSchemas.CandidateVerification;

    protected override Task<SemanticProviderResponse<HeadingCandidateVerificationResult>> InvokeAsync(
        SemanticProviderRequest<HeadingCandidateVerificationRequest> request,
        CancellationToken cancellationToken) => provider.DecideAsync(request, cancellationToken);
}

public sealed record HeadingScanSemanticExecutors(
    ISemanticOperationExecutor<HeadingCandidateDetectionRequest, HeadingCandidateDetectionResult> CandidateDetection,
    ISemanticOperationExecutor<HeadingCandidateVerificationRequest, HeadingCandidateVerificationResult> CandidateVerification);

public static class HeadingSemanticResultValidator
{
    public static void CandidateDetection(HeadingCandidateDetectionRequest request, HeadingCandidateDetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Candidates is null)
        {
            throw new ArgumentException("Candidate detection must contain a candidates array.", nameof(result));
        }

        var selectable = request.Paragraphs
            .Where(paragraph => paragraph.Selectable)
            .Select(paragraph => paragraph.Number)
            .ToHashSet();
        var seen = new HashSet<int>();

        foreach (var number in result.Candidates)
        {
            if (!seen.Add(number))
            {
                throw new ArgumentException($"Candidate detection contains duplicate paragraph number '{number}'.", nameof(result));
            }

            if (!selectable.Contains(number))
            {
                throw new ArgumentException($"Candidate paragraph number '{number}' is unknown or not selectable.", nameof(result));
            }
        }
    }

    public static void CandidateVerification(HeadingCandidateVerificationRequest request, HeadingCandidateVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Accept)
        {
            if (result.Level is null or < 1 or > 3)
            {
                throw new ArgumentException("An accepted heading must have level 1 through 3.", nameof(result));
            }

            return;
        }

        if (result.Level is not null)
        {
            throw new ArgumentException("A rejected heading must not have a level.", nameof(result));
        }
    }
}

public static class HeadingSemanticOperationSchemas
{
    public static JsonDocument CandidateDetection { get; } = JsonDocument.Parse(
        """{"type":"object","required":["candidates"],"properties":{"candidates":{"type":"array","items":{"type":"integer","minimum":1},"uniqueItems":true}},"additionalProperties":false}""");

    public static JsonDocument CandidateVerification { get; } = JsonDocument.Parse(
        """{"oneOf":[{"type":"object","required":["accept","level"],"properties":{"accept":{"const":true},"level":{"type":"integer","minimum":1,"maximum":3}},"additionalProperties":false},{"type":"object","required":["accept"],"properties":{"accept":{"const":false}},"additionalProperties":false}]}""");
}
