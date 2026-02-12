using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Services;

/// <summary>
/// Default implementation of <see cref="IFileOutlinerRegistry"/>.
/// Routes file paths to the appropriate language outliner based on file extension.
/// </summary>
public sealed class FileOutlinerRegistry : IFileOutlinerRegistry
{
    private readonly Dictionary<string, IFileOutliner> _outliners = new(StringComparer.OrdinalIgnoreCase);

    public FileOutlinerRegistry(IEnumerable<IFileOutliner> outliners)
    {
        foreach (var outliner in outliners)
        {
            foreach (var ext in outliner.SupportedExtensions)
            {
                var normalizedExt = ext.StartsWith('.') ? ext : "." + ext;
                _outliners[normalizedExt] = outliner;
            }
        }
    }

    /// <inheritdoc />
    public IFileOutliner? GetOutliner(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return _outliners.TryGetValue(ext, out var outliner) ? outliner : null;
    }

    /// <inheritdoc />
    public bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return _outliners.ContainsKey(ext);
    }
}
