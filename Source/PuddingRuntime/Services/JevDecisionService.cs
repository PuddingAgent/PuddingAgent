using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// Jev 决策模型适配器：POST <c>{baseUrl}/api/v1/decide</c>，Bearer 鉴权，
/// 返回结构化决策数据（choice / score / noul），不做任何自然语言解析。
/// <para>
/// 契约见 <see cref="IJevDecisionService"/>。连接参数（端点 / 密钥 / 模型）来自
/// <see cref="IJevDecisionOptionsProvider"/>，每次调用前解析 —— 密钥不落日志。
/// 非 2xx 一律 fail-closed 抛 <see cref="JevDecisionException"/>（错误体截断 ≤4096 字符）；
/// 502/503/504 按 <see cref="JevDecisionOptions.MaxRetries"/> 指数退避重试。
/// </para>
/// </summary>
public sealed class JevDecisionService(
    IHttpClientFactory httpClientFactory,
    IJevDecisionOptionsProvider optionsProvider,
    ILogger<JevDecisionService> logger) : IJevDecisionService
{
    /// <summary>命名 HttpClient（在组合根注册并设超时）。</summary>
    public const string HttpClientName = "Jev";

    private const string DecidePath = "/api/v1/decide";
    private const int MaxErrorBodyCharacters = 4_096;
    private const int MaxChoiceOptions = 255;
    private const int MinScoreLevels = 2;
    private const int MaxScoreLevels = 10;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<JevDecisionResult> DecideAsync(
        JevDecisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var options = await optionsProvider.GetOptionsAsync(ct).ConfigureAwait(false);
        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? options.ModelId
            : request.ModelId!;
        if (string.IsNullOrWhiteSpace(modelId))
            modelId = JevDecisionOptions.DefaultModelId;

        var endpoint = options.BaseUrl.TrimEnd('/') + DecidePath;
        var payload = BuildPayload(request, modelId);
        var requestBody = JsonSerializer.Serialize(payload, JsonOptions);

        var client = httpClientFactory.CreateClient(HttpClientName);
        var maxAttempts = Math.Max(0, options.MaxRetries) + 1;

        HttpResponseMessage response;
        string responseBody;
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
            };
            message.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);

            logger.LogInformation(
                "[Jev] REQUEST endpoint={Endpoint} model={Model} questions={QuestionCount} attempt={Attempt}",
                endpoint, modelId, request.Questions.Count, attempt + 1);

            response = await client.SendAsync(message, ct).ConfigureAwait(false);
            responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode
                && IsTransient(response.StatusCode)
                && attempt + 1 < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(
                    Math.Max(0, options.RetryDelayMilliseconds) * Math.Pow(2, attempt));
                logger.LogWarning(
                    "[Jev] HTTP {Status} 上游错误，{DelayMs}ms 后重试 ({Attempt}/{MaxAttempts})",
                    (int)response.StatusCode, delay.TotalMilliseconds, attempt + 2, maxAttempts);
                response.Dispose();
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = Truncate(responseBody);
                logger.LogError(
                    "[Jev] ERROR HTTP {Status}: {Body}", (int)response.StatusCode, errorBody);
                throw MapError(response.StatusCode, errorBody);
            }

            return ParseResult(responseBody, modelId, (int)response.StatusCode);
        }
    }

    // ── 请求侧 ────────────────────────────────────────────────────────────

    private static void ValidateRequest(JevDecisionRequest request)
    {
        if (request.State is null)
        {
            throw new JevDecisionException(
                JevDecisionCodes.InvalidRequest,
                "Jev 决策请求缺少 state（string|object|array 任一形态，必填）。");
        }

        if (request.Questions is null || request.Questions.Count == 0)
        {
            throw new JevDecisionException(
                JevDecisionCodes.InvalidRequest,
                "Jev 决策请求至少需要一个 question。");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.Name))
            {
                throw new JevDecisionException(
                    JevDecisionCodes.InvalidRequest, "question.name 必填。");
            }

            if (!names.Add(question.Name))
            {
                throw new JevDecisionException(
                    JevDecisionCodes.InvalidRequest,
                    $"question 名称重复：{question.Name}");
            }

            switch (question.Type)
            {
                case JevQuestionType.Choice:
                    if (question.ChoiceCriteria is null || question.ChoiceCriteria.Count == 0)
                    {
                        throw new JevDecisionException(
                            JevDecisionCodes.InvalidRequest,
                            $"choice 类型 question '{question.Name}' 必须提供非空 criteria 映射。");
                    }

                    if (question.ChoiceCriteria.Count > MaxChoiceOptions)
                    {
                        throw new JevDecisionException(
                            JevDecisionCodes.InvalidRequest,
                            $"choice 类型 question '{question.Name}' 选项数 {question.ChoiceCriteria.Count} 超过上限 {MaxChoiceOptions}。");
                    }

                    break;

                case JevQuestionType.Score:
                    var levels = question.ScoreCriteria?.Count ?? 0;
                    if (levels < MinScoreLevels || levels > MaxScoreLevels)
                    {
                        throw new JevDecisionException(
                            JevDecisionCodes.InvalidRequest,
                            $"score 类型 question '{question.Name}' 的 criteria 必须是有序描述数组，长度 {MinScoreLevels}..{MaxScoreLevels}（低→高），当前 {levels}。");
                    }

                    break;

                case JevQuestionType.Noul:
                    if (string.IsNullOrWhiteSpace(question.Instructions))
                    {
                        throw new JevDecisionException(
                            JevDecisionCodes.InvalidRequest,
                            $"noul 类型 question '{question.Name}' 必须提供 instructions。");
                    }

                    break;

                default:
                    throw new JevDecisionException(
                        JevDecisionCodes.InvalidRequest,
                        $"不支持的 question 类型：{question.Type}（question '{question.Name}'）。");
            }
        }
    }

    private static JsonObject BuildPayload(JevDecisionRequest request, string modelId)
    {
        var questions = new JsonObject();
        foreach (var question in request.Questions)
        {
            var node = new JsonObject { ["type"] = ToWireType(question.Type) };
            if (!string.IsNullOrWhiteSpace(question.Instructions))
                node["instructions"] = question.Instructions;

            switch (question.Type)
            {
                case JevQuestionType.Choice:
                    // Dictionary<string,string?> 序列化后保留 null 值（选项无描述时 wire 上必须是 null）。
                    node["criteria"] =
                        JsonSerializer.SerializeToNode(question.ChoiceCriteria, JsonOptions);
                    break;

                case JevQuestionType.Score:
                    node["criteria"] =
                        JsonSerializer.SerializeToNode(question.ScoreCriteria, JsonOptions);
                    break;

                case JevQuestionType.Noul:
                {
                    var criteria = BuildNoulCriteria(question.NoulCriteria);
                    if (criteria is not null)
                        node["criteria"] = criteria;
                    break;
                }
            }

            questions[question.Name] = node;
        }

        return new JsonObject
        {
            ["model"] = modelId,
            // DeepClone：JsonNode 只能有一个父节点，复用调用方节点会抛异常。
            ["state"] = request.State!.DeepClone(),
            ["questions"] = questions,
        };
    }

    private static JsonObject? BuildNoulCriteria(JevBoolCriteria? criteria)
    {
        if (criteria is null)
            return null;

        var node = new JsonObject();
        if (!string.IsNullOrWhiteSpace(criteria.True))
            node["true"] = criteria.True;
        if (!string.IsNullOrWhiteSpace(criteria.False))
            node["false"] = criteria.False;

        return node.Count == 0 ? null : node;
    }

    private static string ToWireType(JevQuestionType type)
        => type switch
        {
            JevQuestionType.Choice => "choice",
            JevQuestionType.Score => "score",
            JevQuestionType.Noul => "noul",
            _ => throw new JevDecisionException(
                JevDecisionCodes.InvalidRequest, $"不支持的 question 类型：{type}"),
        };

    // ── 错误映射 ──────────────────────────────────────────────────────────

    private static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static JevDecisionException MapError(HttpStatusCode status, string responseBody)
    {
        var code = status switch
        {
            HttpStatusCode.BadRequest => JevDecisionCodes.InvalidRequest,
            HttpStatusCode.Unauthorized => JevDecisionCodes.Unauthorized,
            (HttpStatusCode)402 => JevDecisionCodes.InsufficientCredits,
            HttpStatusCode.Forbidden => JevDecisionCodes.AccountDisabled,
            HttpStatusCode.BadGateway => JevDecisionCodes.UpstreamError,
            HttpStatusCode.ServiceUnavailable => JevDecisionCodes.UpstreamError,
            HttpStatusCode.GatewayTimeout => JevDecisionCodes.UpstreamError,
            _ => JevDecisionCodes.HttpError,
        };

        return new JevDecisionException(
            code,
            $"Jev decide failed with HTTP {(int)status} ({code}): {responseBody}",
            (int)status,
            responseBody);
    }

    private static string Truncate(string body)
        => body.Length > MaxErrorBodyCharacters ? body[..MaxErrorBodyCharacters] : body;

    // ── 响应侧 ────────────────────────────────────────────────────────────

    private static JevDecisionResult ParseResult(string body, string requestedModel, int statusCode)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new JevDecisionException(
                JevDecisionCodes.InvalidResponse,
                $"Jev 返回 HTTP {statusCode} 但响应体不是合法 JSON（fail-closed，不伪造决策结果）。",
                statusCode,
                Truncate(body),
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("answers", out var answersElement)
                || answersElement.ValueKind != JsonValueKind.Object)
            {
                throw new JevDecisionException(
                    JevDecisionCodes.InvalidResponse,
                    $"Jev 返回 HTTP {statusCode} 但响应缺少 answers 对象（fail-closed，不伪造决策结果）。",
                    statusCode,
                    Truncate(body));
            }

            var answers = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
            foreach (var property in answersElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;
                answers[property.Name] = ParseAnswer(property.Name, property.Value);
            }

            if (answers.Count == 0)
            {
                throw new JevDecisionException(
                    JevDecisionCodes.InvalidResponse,
                    $"Jev 返回 HTTP {statusCode} 但 answers 为空（fail-closed，不伪造决策结果）。",
                    statusCode,
                    Truncate(body));
            }

            var model = root.TryGetProperty("model", out var modelElement)
                && modelElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(modelElement.GetString())
                    ? modelElement.GetString()!
                    : requestedModel;

            var usage = root.TryGetProperty("usage", out var usageElement)
                && usageElement.ValueKind == JsonValueKind.Object
                    ? ParseUsage(usageElement)
                    : null;

            return new JevDecisionResult
            {
                Model = model,
                Answers = answers,
                Usage = usage,
            };
        }
    }

    private static JevAnswer ParseAnswer(string name, JsonElement element)
    {
        var type = element.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() ?? ""
                : "";

        return new JevAnswer
        {
            Name = name,
            Type = type,
            Choice = TryGetString(element, "choice"),
            Score = TryGetDouble(element, "score"),
            Noul = TryGetDouble(element, "noul"),
            Confidence = TryGetDouble(element, "confidence"),
            Probabilities = TryGetProbabilityMap(element, "probabilities"),
            ScoreProbabilities = TryGetProbabilityList(element, "probabilities"),
            Legend = TryGetStringList(element, "legend"),
        };
    }

    private static JevUsage ParseUsage(JsonElement element)
        => new()
        {
            InputTokens = TryGetInt(element, "input_tokens"),
            OutputTokens = TryGetInt(element, "output_tokens"),
            CostUsd = TryGetDecimal(element, "cost_usd"),
            CreditsRemainingUsd = TryGetDecimal(element, "credits_remaining_usd"),
        };

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return TryGetScalarDouble(value);
    }

    /// <summary>读取标量节点的数值（数字或可解析的字符串），否则 null。</summary>
    private static double? TryGetScalarDouble(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static decimal? TryGetDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static IReadOnlyDictionary<string, double>? TryGetProbabilityMap(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var probability = TryGetScalarDouble(property.Value);
            if (probability is not null)
                map[property.Name] = probability.Value;
        }

        return map.Count == 0 ? null : map;
    }

    private static IReadOnlyList<double>? TryGetProbabilityList(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<double>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            var probability = TryGetScalarDouble(item);
            if (probability is not null)
                list.Add(probability.Value);
        }

        return list.Count == 0 ? null : list;
    }

    private static IReadOnlyList<string>? TryGetStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
                list.Add(text!);
        }

        return list.Count == 0 ? null : list;
    }

}
