using HarnessAgent.Core.Provider;

namespace HarnessAgent.Core.Middleware;

/// <summary>
/// Chat middleware function — intercepts requests/responses.
/// Modeled after MS Agent Framework's Chat-level Middleware.
/// </summary>
public delegate Task<ChatCompletionResponse> ChatMiddlewareDelegate(
    ChatCompletionRequest request,
    Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResponse>> next,
    CancellationToken ct);

/// <summary>
/// Pipeline that chains middleware around an inner chat handler.
/// </summary>
public sealed class ChatPipeline
{
    private readonly List<ChatMiddlewareDelegate> _middlewares = new();
    private readonly Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResponse>> _inner;

    public ChatPipeline(IModelProvider provider, string modelId)
    {
        _inner = (req, ct) => provider.CompleteAsync(req with { ModelId = modelId }, ct);
    }

    public ChatPipeline(Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResponse>> inner)
    {
        _inner = inner;
    }

    /// <summary>Add a middleware to the pipeline.</summary>
    public ChatPipeline Use(ChatMiddlewareDelegate middleware)
    {
        _middlewares.Add(middleware);
        return this;
    }

    /// <summary>Execute the full pipeline.</summary>
    public async Task<ChatCompletionResponse> ExecuteAsync(
        ChatCompletionRequest request, CancellationToken ct = default)
    {
        Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResponse>> pipeline = _inner;

        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var next = pipeline;
            pipeline = (req, c) => middleware(req, next, c);
        }

        return await pipeline(request, ct);
    }
}

/// <summary>Built-in middleware implementations.</summary>
public static class ChatMiddleware
{
    /// <summary>Log requests and responses.</summary>
    public static ChatMiddlewareDelegate Logging(
        Action<string>? onRequest = null,
        Action<string, TokenUsage>? onResponse = null)
    {
        onRequest ??= msg => Console.WriteLine($"[Chat] → {msg}");
        onResponse ??= (msg, usage) => Console.WriteLine($"[Chat] ← {msg} ({usage.TotalTokens}t)");

        return async (req, next, ct) =>
        {
            var lastMsg = req.Messages.LastOrDefault();
            onRequest(lastMsg?.Content[..Math.Min(80, lastMsg?.Content.Length ?? 0)] ?? "(empty)");

            var resp = await next(req, ct);

            onResponse(resp.Message.Content[..Math.Min(80, resp.Message.Content.Length)], resp.Usage);
            return resp;
        };
    }

    /// <summary>Track token usage and cost.</summary>
    public static ChatMiddlewareDelegate CostTracking(
        ModelDescriptor model,
        Action<decimal>? onCost = null)
    {
        onCost ??= cost => Console.WriteLine($"[Cost] ${cost:F4}");

        return async (req, next, ct) =>
        {
            var resp = await next(req, ct);
            var cost = resp.Usage.InputTokens / 1_000_000m * model.InputPricePerMTokens
                     + resp.Usage.OutputTokens / 1_000_000m * model.OutputPricePerMTokens;
            onCost(cost);
            return resp;
        };
    }

    /// <summary>Retry on transient failures.</summary>
    public static ChatMiddlewareDelegate Retry(
        int maxRetries = 3,
        Func<Exception, bool>? shouldRetry = null)
    {
        shouldRetry ??= _ => true;

        return async (req, next, ct) =>
        {
            Exception? lastEx = null;
            for (int i = 0; i <= maxRetries; i++)
            {
                try
                {
                    return await next(req, ct);
                }
                catch (Exception ex) when (i < maxRetries && shouldRetry(ex))
                {
                    lastEx = ex;
                    await Task.Delay((int)Math.Pow(2, i) * 500, ct); // exponential backoff
                }
            }
            throw lastEx!;
        };
    }

    /// <summary>Validate request before sending.</summary>
    public static ChatMiddlewareDelegate Validate(
        Func<ChatCompletionRequest, string?>? validator = null)
    {
        validator ??= req =>
        {
            if (req.Messages.Count == 0) return "No messages in request";
            if (string.IsNullOrWhiteSpace(req.ModelId)) return "No model specified";
            return null;
        };

        return async (req, next, ct) =>
        {
            var error = validator(req);
            if (error != null)
                throw new InvalidOperationException($"Request validation failed: {error}");
            return await next(req, ct);
        };
    }

    /// <summary>Inject system message if missing.</summary>
    public static ChatMiddlewareDelegate EnsureSystemMessage(string systemPrompt)
    {
        return async (req, next, ct) =>
        {
            if (!req.Messages.Any(m => m.Role == ChatRole.System))
            {
                var messages = new List<ChatMessage> { ChatMessage.System(systemPrompt) };
                messages.AddRange(req.Messages);
                req = req with { Messages = messages };
            }
            return await next(req, ct);
        };
    }

    /// <summary>Compose multiple middleware into one.</summary>
    public static ChatMiddlewareDelegate Compose(
        params ChatMiddlewareDelegate[] middlewares)
    {
        return async (req, next, ct) =>
        {
            // Build chain in reverse
            var pipeline = next;
            for (int i = middlewares.Length - 1; i >= 0; i--)
            {
                var mw = middlewares[i];
                var current = pipeline;
                pipeline = (r, c) => mw(r, current, c);
            }
            return await pipeline(req, ct);
        };
    }
}
