using SharpToken;

namespace PuddingCodeCLI;

internal static class TokenEstimator
{
    private static readonly object Gate = new();
    private static GptEncoding? _encoding;
    private static bool _disabled;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var encoding = TryGetEncoding();
        if (encoding is null)
            return Math.Max(1, text.Length / 4);

        try
        {
            return Math.Max(1, encoding.Encode(text).Count);
        }
        catch
        {
            return Math.Max(1, text.Length / 4);
        }
    }

    private static GptEncoding? TryGetEncoding()
    {
        if (_disabled) return null;
        if (_encoding is not null) return _encoding;

        lock (Gate)
        {
            if (_disabled) return null;
            if (_encoding is not null) return _encoding;

            try
            {
                _encoding = GptEncoding.GetEncoding("cl100k_base");
            }
            catch
            {
                _disabled = true;
            }
        }

        return _encoding;
    }
}
