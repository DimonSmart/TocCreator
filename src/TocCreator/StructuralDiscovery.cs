using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TocCreator;

public sealed record TocRange(string StartPointerId, string EndPointerId, IReadOnlyList<TocEntry> Entries);

public sealed record TocEntry(string SourcePointerId, string Title, int Level, int? PrintedPageNumber);

public sealed record StructuralDiscoveryResult(TocRange? TableOfContents, AnnotationSet Annotations)
{
    public IReadOnlyList<TocEntry> Entries => TableOfContents?.Entries ?? [];
}

/// <summary>Finds a small, printed table of contents and applies only unambiguous body-heading annotations.</summary>
public sealed class StructuralDiscovery
{
    private const int StartSearchPointerLimit = 80;
    private const int CandidatePointerLimit = 32;
    private static readonly Regex ContentsLabel = new(@"^\s*(?:table\s+of\s+)?contents\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EntryWithPage = new(@"^\s*(?<title>.+?)(?:\s*[.·…]{2,}\s*|\s+)(?<page>\d{1,5})\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex NumberedTitle = new(@"^\s*(?<number>(?:\d+\.)*\d+|(?:chapter|part|appendix)\s+[a-z0-9ivxlcdm]+)\s*[:.\-–—]?\s*(?<title>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppendixTitle = new(@"^\s*(?:appendix\s+[a-z0-9ivxlcdm]+|(?:preface|introduction|references))\b.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public StructuralDiscoveryResult Discover(SourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var source = snapshot.GetSourceBytes();
        var lines = snapshot.Pointers.Select(pointer => new Line(pointer, ReadPointer(source, pointer))).ToList();
        var range = FindTableOfContents(lines);
        if (range is null)
        {
            return new StructuralDiscoveryResult(null, new AnnotationSet([]));
        }

        return BuildResult(lines, range);
    }

    /// <summary>Uses semantic executors only for small local ambiguities left by deterministic discovery.</summary>
    public async Task<StructuralDiscoveryResult> DiscoverAsync(
        SourceSnapshot snapshot,
        StructuralRecoverySemanticExecutors executors,
        ISemanticOperationDiagnosticSink? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(executors);
        var source = snapshot.GetSourceBytes();
        var lines = snapshot.Pointers.Select(pointer => new Line(pointer, ReadPointer(source, pointer))).ToList();
        var range = FindTableOfContents(lines);
        if (range is null && TryFindAmbiguousTocCandidate(lines, out var candidate))
        {
            var context = new SemanticOperationContext(candidate.Select(ToSemanticPointer).ToArray(), []);
            var detectionRequest = new TocDetectionRequest(context);
            try
            {
                var detected = await new ValidatedSemanticOperation<TocDetectionRequest, TocDetectionResult>(
                    SemanticOperationKind.TocDetection, "toc-detection", request => request.Context, SemanticOperationResultValidator.TocDetection)
                    .ExecuteAsync(detectionRequest, executors.TocDetection, diagnostics: diagnostics, cancellationToken: cancellationToken);
                if (detected.IsTableOfContents)
                {
                    var start = candidate.FindIndex(line => line.Pointer.Id == detected.StartPointerId);
                    var end = candidate.FindIndex(line => line.Pointer.Id == detected.EndPointerId);
                    var parseLines = candidate.Skip(start).Take(end - start + 1).ToArray();
                    var parsingRequest = new TocParsingRequest(new SemanticOperationContext(parseLines.Select(ToSemanticPointer).ToArray(), []));
                    var parsed = await new ValidatedSemanticOperation<TocParsingRequest, TocParsingResult>(
                        SemanticOperationKind.TocParsing, "toc-parsing", request => request.Context, SemanticOperationResultValidator.TocParsing)
                        .ExecuteAsync(parsingRequest, executors.TocParsing, diagnostics: diagnostics, cancellationToken: cancellationToken);
                    if (parsed.Entries.Count > 0)
                        range = new TocRange(detected.StartPointerId!, detected.EndPointerId!, parsed.Entries.Select(entry => new TocEntry(entry.SourcePointerId, entry.Title, entry.Level, entry.PrintedPageNumber)).ToArray());
                }
            }
            catch (InvalidDataException)
            {
                // A failed local ambiguity remains unresolved; deterministic recovery never invents a TOC.
            }
        }

        if (range is null) return new StructuralDiscoveryResult(null, new AnnotationSet([]));
        return await BuildResultAsync(lines, range, executors, diagnostics, cancellationToken);
    }

    private static StructuralDiscoveryResult BuildResult(IReadOnlyList<Line> lines, TocRange range)
    {
        var annotations = CreateTocAnnotations(lines, range);
        var body = BodyCandidates(lines, range);
        foreach (var entry in range.Entries)
        {
            var matches = body.Where(line => TitlesMatch(entry.Title, line.Text)).ToList();
            if (matches.Count == 1) annotations.Add(Heading(matches[0], entry.Level));
        }
        return new StructuralDiscoveryResult(range, new AnnotationSet(annotations));
    }

    private static async Task<StructuralDiscoveryResult> BuildResultAsync(IReadOnlyList<Line> lines, TocRange range, StructuralRecoverySemanticExecutors executors, ISemanticOperationDiagnosticSink? diagnostics, CancellationToken cancellationToken)
    {
        var annotations = CreateTocAnnotations(lines, range);
        var body = BodyCandidates(lines, range);
        foreach (var entry in range.Entries)
        {
            var matches = body.Where(line => TitlesMatch(entry.Title, line.Text)).ToList();
            if (matches.Count == 1) { annotations.Add(Heading(matches[0], entry.Level)); continue; }
            if (matches.Count is < 2 or > 8) continue;

            var writable = matches.Select(ToSemanticPointer).ToArray();
            var readOnly = LocalReadOnlyContext(lines, matches).Select(ToSemanticPointer).ToArray();
            var request = new AmbiguousHeadingMatchRequest(new SemanticOperationContext(readOnly, writable), entry);
            try
            {
                var result = await new ValidatedSemanticOperation<AmbiguousHeadingMatchRequest, AmbiguousHeadingMatchResult>(
                    SemanticOperationKind.AmbiguousHeadingMatch, "toc-body-match", value => value.Context, SemanticOperationResultValidator.HeadingMatch)
                    .ExecuteAsync(request, executors.HeadingMatch, diagnostics: diagnostics, cancellationToken: cancellationToken);
                if (result.SelectedPointerId is not null)
                    annotations.Add(new StructuralAnnotation { PointerId = result.SelectedPointerId, Role = StructuralRole.Heading, HeadingLevel = result.HeadingLevel });
            }
            catch (InvalidDataException) { }
        }

        var headingIds = annotations.Where(annotation => annotation.Role == StructuralRole.Heading).Select(annotation => annotation.PointerId).ToHashSet(StringComparer.Ordinal);
        var ambiguousNoise = body.Where(line => !headingIds.Contains(line.Pointer.Id))
            .GroupBy(line => NormalizeTitle(line.Text), StringComparer.Ordinal).Where(group => group.Key.Length > 0 && group.Count() == 2).SelectMany(group => group).Take(8).ToArray();
        if (ambiguousNoise.Length > 0)
        {
            var request = new AmbiguousNoiseClassificationRequest(new SemanticOperationContext([], ambiguousNoise.Select(ToSemanticPointer).ToArray()));
            try
            {
                var result = await new ValidatedSemanticOperation<AmbiguousNoiseClassificationRequest, AmbiguousNoiseClassificationResult>(
                    SemanticOperationKind.AmbiguousNoiseClassification, "noise-classification", value => value.Context, SemanticOperationResultValidator.Noise)
                    .ExecuteAsync(request, executors.NoiseClassification, diagnostics: diagnostics, cancellationToken: cancellationToken);
                annotations.AddRange(result.Decisions.Where(decision => decision.IsNoise).Select(decision => new StructuralAnnotation { PointerId = decision.PointerId, Role = StructuralRole.Noise }));
            }
            catch (InvalidDataException) { }
        }
        return new StructuralDiscoveryResult(range, new AnnotationSet(annotations));
    }

    private static List<StructuralAnnotation> CreateTocAnnotations(IReadOnlyList<Line> lines, TocRange range)
    {
        var annotations = new List<StructuralAnnotation>();
        var start = IndexOf(lines, range.StartPointerId);
        var end = IndexOf(lines, range.EndPointerId);
        for (var index = start; index <= end; index++)
        {
            annotations.Add(new StructuralAnnotation { PointerId = lines[index].Pointer.Id, Role = StructuralRole.Toc });
        }

        return annotations;
    }

    private static List<Line> BodyCandidates(IReadOnlyList<Line> lines, TocRange range) => lines.Skip(IndexOf(lines, range.EndPointerId) + 1).Where(IsEligibleBodyCandidate).ToList();
    private static StructuralAnnotation Heading(Line line, int level) => new() { PointerId = line.Pointer.Id, Role = StructuralRole.Heading, HeadingLevel = level };
    private static SemanticPointer ToSemanticPointer(Line line) => new(line.Pointer.Id, line.Text, line.Pointer.Span.ByteStart > int.MaxValue ? int.MaxValue : (int)line.Pointer.Span.ByteStart);
    private static IEnumerable<Line> LocalReadOnlyContext(IReadOnlyList<Line> lines, IReadOnlyList<Line> matches) => matches.SelectMany(match => { var index = IndexOf(lines, match.Pointer.Id); return lines.Skip(Math.Max(0, index - 1)).Take(3); }).Where(line => !matches.Contains(line)).DistinctBy(line => line.Pointer.Id).Take(8);
    private static bool TryFindAmbiguousTocCandidate(IReadOnlyList<Line> lines, out List<Line> candidate)
    {
        var start = lines.Take(Math.Min(lines.Count, StartSearchPointerLimit)).ToList().FindIndex(line => ContentsLabel.IsMatch(line.Text.TrimEnd()));
        if (start < 0) { candidate = []; return false; }
        candidate = lines.Skip(start).Take(Math.Min(CandidatePointerLimit, lines.Count - start)).TakeWhile((line, index) => index == 0 || !string.IsNullOrWhiteSpace(line.Text)).ToList();
        return candidate.Count > 1;
    }
    private static int IndexOf(IReadOnlyList<Line> lines, string pointerId)
    {
        for (var index = 0; index < lines.Count; index++) if (lines[index].Pointer.Id == pointerId) return index;
        throw new ArgumentException($"Unknown pointer '{pointerId}'.");
    }

    private static TocRange? FindTableOfContents(IReadOnlyList<Line> lines)
    {
        var searchLimit = Math.Min(lines.Count, StartSearchPointerLimit);
        for (var start = 0; start < searchLimit; start++)
        {
            if (!ContentsLabel.IsMatch(lines[start].Text.TrimEnd()))
            {
                continue;
            }

            var entries = new List<TocEntry>();
            var end = start;
            for (var index = start + 1; index < Math.Min(searchLimit, start + CandidatePointerLimit); index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index].Text))
                {
                    if (entries.Count > 0)
                    {
                        break;
                    }
                    continue;
                }

                if (!TryParseEntry(lines[index], out var entry))
                {
                    break;
                }

                entries.Add(entry);
                end = index;
            }

            if (entries.Count >= 2)
            {
                return new TocRange(lines[start].Pointer.Id, lines[end].Pointer.Id, entries);
            }
        }

        return null;
    }

    private static bool TryParseEntry(Line line, out TocEntry entry)
    {
        var text = line.Text.Trim();
        var match = EntryWithPage.Match(text);
        if (!match.Success)
        {
            entry = default!;
            return false;
        }

        var title = match.Groups["title"].Value.Trim();
        if (title.Length == 0 || title.All(char.IsDigit))
        {
            entry = default!;
            return false;
        }

        var level = GetLevel(title);
        entry = new TocEntry(line.Pointer.Id, title, level, int.Parse(match.Groups["page"].Value, CultureInfo.InvariantCulture));
        return true;
    }

    private static int GetLevel(string title)
    {
        var numbered = NumberedTitle.Match(title);
        if (numbered.Success && numbered.Groups["number"].Value[0] is >= '0' and <= '9')
        {
            return Math.Min(3, numbered.Groups["number"].Value.Count(character => character == '.') + 1);
        }

        return AppendixTitle.IsMatch(title) ? 1 : 1;
    }

    private static bool IsEligibleBodyCandidate(Line line)
    {
        var text = line.Text.Trim();
        return text.Length is > 0 and <= 120
            && !text.All(char.IsDigit)
            && !EntryWithPage.IsMatch(text)
            && !text.EndsWith(".", StringComparison.Ordinal);
    }

    private static bool TitlesMatch(string tocTitle, string bodyTitle) =>
        string.Equals(NormalizeTitle(tocTitle), NormalizeTitle(bodyTitle), StringComparison.Ordinal);

    private static string NormalizeTitle(string value)
    {
        var numbered = NumberedTitle.Match(value);
        if (numbered.Success)
        {
            value = numbered.Groups["title"].Value;
        }

        var builder = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character switch { '0' => 'o', '1' => 'l', _ => char.ToLowerInvariant(character) });
            }
        }

        return builder.ToString();
    }

    private static string ReadPointer(byte[] source, TextPointer pointer)
    {
        return Encoding.UTF8.GetString(source, checked((int)pointer.Span.ByteStart), pointer.Span.ByteLength).TrimEnd('\r', '\n');
    }

    private sealed record Line(TextPointer Pointer, string Text);
}
