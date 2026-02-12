using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

public interface ILanguageServerService
{
    Task<LanguageServerResponse> ExecuteAsync(
        LanguageServerRequest request,
        CancellationToken cancellationToken = default);
}
