using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PuddingSlack;

public sealed record SlackCredentialSnapshot(SlackCredentials Credentials, string Revision)
{
    public override string ToString() => "SlackCredentialSnapshot [redacted]";
}

/// <summary>The host supplies an exclusive, secret-preserving atomic store with compare-before-write.</summary>
public interface ISlackCredentialStore
{
    SlackCredentialSnapshot Read();
    SlackCredentialSnapshot Write(SlackCredentials credentials, string expectedRevision);
}

/// <summary>Refreshes rotating app tokens before expiry. An ambiguous refresh is never blindly repeated.</summary>
public sealed class SlackAppTokenProvider(HttpClient http, ISlackCredentialStore store, string appId,
    TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = store.Read();
            var credentials = snapshot.Credentials.Validate();
            if (!credentials.AppToken.StartsWith("xoxe.xapp-", StringComparison.Ordinal)) return credentials.AppToken;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.AppRefreshToken ?? "")));
            if (credentials.PendingRefreshTokenHash == hash)
                throw new SlackProtocolException("app_refresh_outcome_unknown");
            if (credentials.AppTokenExpiresAt > _time.GetUtcNow().AddMinutes(10)) return credentials.AppToken;
            if (string.IsNullOrWhiteSpace(credentials.AppRefreshToken) || string.IsNullOrWhiteSpace(credentials.ClientId)
                || string.IsNullOrWhiteSpace(credentials.ClientSecret))
                throw new SlackProtocolException("app_rotation_credentials_missing");

            var pending = credentials.Copy();
            pending.PendingRefreshTokenHash = hash;
            snapshot = store.Write(pending, snapshot.Revision); // durable BEFORE consuming the one-use refresh token
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/oauth.v2.access")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = credentials.ClientId, ["client_secret"] = credentials.ClientSecret,
                    ["grant_type"] = "refresh_token", ["refresh_token"] = credentials.AppRefreshToken
                })
            };
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    ClearPending(snapshot);
                    throw new SlackProtocolException("app_refresh_rate_limited");
                }
                if (!response.IsSuccessStatusCode) throw new SlackProtocolException("app_refresh_outcome_unknown");
                await response.Content.LoadIntoBufferAsync(1024 * 1024, deadline.Token);
                using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
                var root = json.RootElement;
                if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                {
                    var error = SlackEventParser.String(root, "error");
                    if (error is "invalid_refresh_token" or "invalid_client_id" or "bad_client_secret" or "invalid_client_secret"
                        or "invalid_grant" or "invalid_request" or "token_revoked" or "invalid_auth")
                    {
                        ClearPending(snapshot);
                        throw new SlackProtocolException("app_refresh_" + error);
                    }
                    throw new SlackProtocolException("app_refresh_outcome_unknown");
                }
                var access = SlackEventParser.String(root, "access_token");
                var refresh = SlackEventParser.String(root, "refresh_token");
                if (SlackEventParser.String(root, "token_type") != "app_level" || SlackEventParser.String(root, "app_id") != appId
                    || access is null || !access.StartsWith("xoxe.xapp-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(refresh)
                    || !root.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out var seconds) || seconds is < 60 or > 604800)
                    throw new SlackProtocolException("app_refresh_response_mismatch");
                var updated = credentials.Copy();
                updated.AppToken = access; updated.AppRefreshToken = refresh;
                updated.AppTokenExpiresAt = _time.GetUtcNow().AddSeconds(seconds);
                updated.PendingRefreshTokenHash = null;
                store.Write(updated.Validate(), snapshot.Revision); // don't use new credentials until BOTH are durable
                return updated.AppToken;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                throw new SlackProtocolException("app_refresh_outcome_unknown");
            }
        }
        finally { _gate.Release(); }
    }

    private void ClearPending(SlackCredentialSnapshot snapshot)
    {
        var unchanged = snapshot.Credentials.Copy(); unchanged.PendingRefreshTokenHash = null;
        store.Write(unchanged, snapshot.Revision);
    }

    public async Task MaintainAsync(Action<string> failure, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await GetAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SlackProtocolException ex) { failure(ex.Code); }
            catch (Exception) { failure("app_credential_store_failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}
