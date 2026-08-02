using System.Net.Http;

namespace PuddingDesktop.Core;

/// <summary>
/// Lightweight health-check client for Core (uses a simple HTTP GET).
/// Does NOT depend on ASP.NET — uses HttpClient directly.
/// </summary>
public sealed class CoreHealthClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _healthEndpoint;

    public CoreHealthClient(Uri baseAddress)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        _healthEndpoint = new Uri(baseAddress, "/health/ready");
    }

    /// <summary>
    /// Returns true if Core responds to GET /health/ready within the timeout.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(_healthEndpoint, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
