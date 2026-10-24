using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace PuddingTokenization;

/// <summary>
/// Applies HuggingFace Sequence[Split(Isolated)...] while preserving every unmatched range.
/// Ranges are UTF-16 offsets into the original input. Special tokens bypass the split stages.
/// </summary>
internal sealed class IsolatedSplitPreTokenizer : PreTokenizer
{
    private readonly Regex[] _stages;
    private readonly Regex? _specialTokens;

    public IsolatedSplitPreTokenizer(IEnumerable<Regex> stages, IEnumerable<string> specialTokens)
    {
        _stages = stages.ToArray();
        var tokens = specialTokens.OrderByDescending(t => t.Length).Select(Regex.Escape).ToArray();
        if (tokens.Length > 0)
            _specialTokens = new Regex(string.Join('|', tokens), RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
    }

    public override IEnumerable<(int Offset, int Length)> PreTokenize(string text)
        => PreTokenize(text.AsSpan());

    public override IEnumerable<(int Offset, int Length)> PreTokenize(ReadOnlySpan<char> text)
    {
        // Materialize offsets before returning. No iterator may outlive borrowed span/pool storage.
        var result = new List<(int Offset, int Length)>();
        var cursor = 0;
        if (_specialTokens is not null)
        {
            foreach (var match in _specialTokens.EnumerateMatches(text))
            {
                Split(text.Slice(cursor, match.Index - cursor), cursor, result);
                result.Add((match.Index, match.Length));
                cursor = match.Index + match.Length;
            }
        }
        Split(text[cursor..], cursor, result);
        return result;
    }

    private void Split(ReadOnlySpan<char> text, int origin, List<(int Offset, int Length)> result)
    {
        if (text.IsEmpty)
            return;
        var ranges = new List<(int Offset, int Length)> { (0, text.Length) };
        foreach (var regex in _stages)
        {
            var next = new List<(int Offset, int Length)>();
            foreach (var (offset, length) in ranges)
            {
                var cursor = 0;
                foreach (var match in regex.EnumerateMatches(text.Slice(offset, length)))
                {
                    if (match.Length == 0)
                        throw new InvalidDataException("Isolated Split must not match an empty string.");
                    if (match.Index > cursor)
                        next.Add((offset + cursor, match.Index - cursor));
                    next.Add((offset + match.Index, match.Length));
                    cursor = match.Index + match.Length;
                }
                if (cursor < length)
                    next.Add((offset + cursor, length - cursor));
            }
            ranges = next;
        }
        foreach (var (offset, length) in ranges)
            result.Add((origin + offset, length));
    }
}
