using System.Text;
using Xunit;

namespace TocCreator.Tests;

public sealed class StructuralDiscoveryTests
{
    private readonly SourceImporter importer = new();
    private readonly StructuralDiscovery discovery = new();

    [Fact]
    public void Matches_exact_toc_entries_to_existing_body_pointers()
    {
        var snapshot = Import("Contents\nChapter 1: Beginning .... 3\n1.1 First steps .... 5\n\nChapter 1: Beginning\nText\n1.1 First steps\n");

        var result = discovery.Discover(snapshot);

        Assert.Equal(["Chapter 1: Beginning", "1.1 First steps"], result.Entries.Select(entry => entry.Title));
        Assert.Equal(StructuralRole.Toc, result.Annotations.GetOrBody("p00000001").Role);
        AssertHeading(result, "p00000005", 1);
        AssertHeading(result, "p00000007", 2);
        Assert.Equal("Chapter 1: Beginning\n", Text(snapshot, "p00000005"));
    }

    [Fact]
    public void Matches_punctuation_case_numbering_and_ocr_differences_without_copying_toc_text()
    {
        var snapshot = Import("TABLE OF CONTENTS\n2. Getting-Started .... 10\nAppendix A .... 30\n\ngetting started\nBody\nAppendix A\n");

        var result = discovery.Discover(snapshot);

        AssertHeading(result, "p00000005", 1);
        Assert.Equal("getting started\n", Text(snapshot, "p00000005"));
    }

    [Fact]
    public void Printed_page_numbers_are_parsed_but_never_become_headings()
    {
        var snapshot = Import("Contents\nAppendix A: Sources .... 145\nReferences .... 151\n\nAppendix A: Sources\n145\nReferences\n");

        var result = discovery.Discover(snapshot);

        Assert.Equal(145, result.Entries[0].PrintedPageNumber);
        AssertHeading(result, "p00000005", 1);
        AssertHeading(result, "p00000007", 1);
        Assert.Equal(StructuralRole.Body, result.Annotations.GetOrBody("p00000006").Role);
    }

    [Fact]
    public void Does_not_promote_repeated_running_headers_when_match_is_ambiguous()
    {
        var snapshot = Import("Contents\nChapter One .... 3\nAppendix A .... 99\n\nChapter One\nBody\nChapter One\nAppendix A\n");

        var result = discovery.Discover(snapshot);

        Assert.Equal(StructuralRole.Body, result.Annotations.GetOrBody("p00000005").Role);
        Assert.Equal(StructuralRole.Body, result.Annotations.GetOrBody("p00000007").Role);
        AssertHeading(result, "p00000008", 1);
    }

    [Fact]
    public void Leaves_documents_without_a_toc_unannotated()
    {
        var snapshot = Import("An ordinary opening paragraph with no contents list.\nIt continues here.\n");

        var result = discovery.Discover(snapshot);

        Assert.Null(result.TableOfContents);
        Assert.Empty(result.Annotations.Items);
    }

    [Fact]
    public async Task Resolves_only_an_ambiguous_body_match_with_a_small_writable_context_and_retries_invalid_output()
    {
        var snapshot = Import("Contents\nChapter One .... 3\nAppendix A .... 9\n\nChapter One\nText\nChapter One\nAppendix A\n");
        var heading = new SequenceExecutor<AmbiguousHeadingMatchRequest, AmbiguousHeadingMatchResult>([
            new AmbiguousHeadingMatchResult("p00000001", 1),
            new AmbiguousHeadingMatchResult("p00000007", 1)]);
        var executors = new StructuralRecoverySemanticExecutors(
            new FailExecutor<TocDetectionRequest, TocDetectionResult>(), new FailExecutor<TocParsingRequest, TocParsingResult>(), heading,
            new SequenceExecutor<AmbiguousNoiseClassificationRequest, AmbiguousNoiseClassificationResult>([new AmbiguousNoiseClassificationResult([])]));

        var result = await discovery.DiscoverAsync(snapshot, executors);

        Assert.Equal(2, heading.CallCount);
        AssertHeading(result, "p00000007", 1);
        Assert.All(heading.Requests, request => Assert.InRange(request.Context.WritablePointers.Count, 2, 8));
        Assert.All(heading.Requests.SelectMany(request => request.Context.WritablePointers), pointer => Assert.True(new[] { "p00000005", "p00000007" }.Contains(pointer.PointerId)));
    }

    [Fact]
    public async Task Uses_provider_independent_typed_executors_for_an_ambiguous_toc_range()
    {
        var snapshot = Import("Contents\nChapter One\nAppendix A\n\nChapter One\nText\nAppendix A\n");
        var detection = new SequenceExecutor<TocDetectionRequest, TocDetectionResult>([new TocDetectionResult(true, "p00000001", "p00000003")]);
        var parsing = new SequenceExecutor<TocParsingRequest, TocParsingResult>([new TocParsingResult([
            new ParsedTocEntry("p00000002", "Chapter One", 1, null), new ParsedTocEntry("p00000003", "Appendix A", 1, null)])]);
        var executors = new StructuralRecoverySemanticExecutors(detection, parsing,
            new FailExecutor<AmbiguousHeadingMatchRequest, AmbiguousHeadingMatchResult>(),
            new FailExecutor<AmbiguousNoiseClassificationRequest, AmbiguousNoiseClassificationResult>());

        var result = await discovery.DiscoverAsync(snapshot, executors);

        Assert.Equal(1, detection.CallCount);
        Assert.Equal(1, parsing.CallCount);
        Assert.All(detection.Requests.Single().Context.ReadOnlyPointers, pointer => Assert.InRange(pointer.GlobalPosition, 0, 32));
        AssertHeading(result, "p00000005", 1);
        AssertHeading(result, "p00000007", 1);
    }

    private SourceSnapshot Import(string text) => importer.Import(Encoding.UTF8.GetBytes(text));

    private static void AssertHeading(StructuralDiscoveryResult result, string pointerId, int level)
    {
        var annotation = result.Annotations.GetOrBody(pointerId);
        Assert.Equal(StructuralRole.Heading, annotation.Role);
        Assert.Equal(level, annotation.HeadingLevel);
    }

    private static string Text(SourceSnapshot snapshot, string pointerId)
    {
        var pointer = snapshot.Pointers.Single(pointer => pointer.Id == pointerId);
        return Encoding.UTF8.GetString(snapshot.GetSourceBytes(), (int)pointer.Span.ByteStart, pointer.Span.ByteLength);
    }

    private sealed class SequenceExecutor<TRequest, TResult>(IReadOnlyList<TResult> results) : ISemanticOperationExecutor<TRequest, TResult>
    {
        private int index;
        public List<TRequest> Requests { get; } = [];
        public int CallCount => index;
        public Task<TResult> DecideAsync(TRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(results[index++]);
        }
    }

    private sealed class FailExecutor<TRequest, TResult> : ISemanticOperationExecutor<TRequest, TResult>
    {
        public Task<TResult> DecideAsync(TRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This deterministic path must not call this executor.");
    }
}
