using System.Text.Json;

namespace Pudding.DesktopService;

/// <summary>
/// **外部观察事实的读取**（B7 外部一侧的入口）：把外部控制器交回来的 JSON 读成
/// <see cref="ExternalAcceptanceSession"/> 的输入。
///
/// 为什么单独抽出来并做校验：这份 JSON 是**外部一方手写/脚本产出**的，最容易出现"字段名拼错、
/// 结果写成奇怪字符串、证据忘了写"这类问题；如果直接反序列化成默认值，就会把"没写"悄悄当成"通过"。
/// 因此这里**逐字段 fail closed**，把问题如实列出来。
/// </summary>
public static class ExternalAcceptanceFacts
{
    /// <summary>结果字段允许的取值（大小写不敏感）。</summary>
    public static readonly IReadOnlyList<string> AllowedOutcomes = ["passed", "failed", "not-run"];

    /// <summary>解析并校验；返回的输入只有在无问题时才可用（问题非空时也应展示给外部一方修正）。</summary>
    public static bool TryParse(
        string? json,
        DeliveryManifest deliveredManifest,
        out ExternalAcceptanceInput? input,
        out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(deliveredManifest);
        input = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            problems = ["the acceptance facts file is empty"];
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            problems = [$"the acceptance facts file is not valid JSON: {exception.Message}"];
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            var collected = new List<string>();

            // 未知字段一律拒绝：静默忽略会把"写了却没生效"变成最难查的一类问题。
            // 尤其是有人想在这里写 acceptRetries（接受重试是**命令行开关**，不是观察事实的一部分）——
            // 那种写法如果被忽略，他会以为已接受，实际结论仍被拒。
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is "observedBuildId" or "smoke" or "lifecycle")
                {
                    continue;
                }

                var hint = string.Equals(property.Name, "acceptRetries", StringComparison.OrdinalIgnoreCase)
                    ? " (accepting retried results is a command-line switch: --accept-retries / -AcceptRetries,"
                        + " not part of the observation facts)"
                    : string.Empty;
                collected.Add(
                    $"unknown field '{property.Name}' (allowed: observedBuildId, smoke, lifecycle){hint}");
            }

            var observedBuildId = ReadString(root, "observedBuildId");
            if (string.IsNullOrWhiteSpace(observedBuildId))
            {
                collected.Add("observedBuildId is required (which build the observations were made on)");
            }

            var observations = new List<SmokeObservation>();
            if (!root.TryGetProperty("smoke", out var smoke) || smoke.ValueKind != JsonValueKind.Array)
            {
                collected.Add("smoke must be an array of observations (one per checklist item)");
            }
            else
            {
                var index = 0;
                foreach (var element in smoke.EnumerateArray())
                {
                    index++;
                    var checkId = ReadString(element, "checkId");
                    var outcomeText = ReadString(element, "outcome");
                    var evidence = ReadString(element, "evidence");

                    if (string.IsNullOrWhiteSpace(checkId))
                    {
                        collected.Add($"smoke[{index}] has no checkId");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(outcomeText)
                        || !TryParseOutcome(outcomeText, out var outcome))
                    {
                        // 不认识的写法必须拒绝：把 "ok"/"pass"/"done" 当成通过是最容易出错的宽容。
                        collected.Add(
                            $"smoke[{index}] ('{checkId}') has outcome '{outcomeText}';"
                            + $" allowed values are {string.Join(", ", AllowedOutcomes)}");
                        continue;
                    }

                    observations.Add(new SmokeObservation(checkId.Trim(), outcome, evidence));
                }
            }

            var lifecycle = new List<LifecycleEvidence>();
            if (!root.TryGetProperty("lifecycle", out var lifecycleElement)
                || lifecycleElement.ValueKind != JsonValueKind.Array)
            {
                collected.Add("lifecycle must be an array (one entry per lifecycle step)");
            }
            else
            {
                var index = 0;
                foreach (var element in lifecycleElement.EnumerateArray())
                {
                    index++;
                    var step = ReadString(element, "step");
                    var evidence = ReadString(element, "evidence");

                    if (string.IsNullOrWhiteSpace(step))
                    {
                        collected.Add($"lifecycle[{index}] has no step");
                        continue;
                    }

                    if (!element.TryGetProperty("passed", out var passedElement)
                        || passedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        collected.Add($"lifecycle[{index}] ('{step}') must state passed = true|false");
                        continue;
                    }

                    lifecycle.Add(new LifecycleEvidence(step.Trim(), passedElement.GetBoolean(), evidence));
                }
            }

            if (collected.Count > 0)
            {
                problems = collected;
                return false;
            }

            input = new ExternalAcceptanceInput(
                deliveredManifest,
                observedBuildId!.Trim(),
                observations,
                lifecycle);
            problems = [];
            return true;
        }
    }

    private static bool TryParseOutcome(string text, out SmokeObservationOutcome outcome)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "passed":
                outcome = SmokeObservationOutcome.Passed;
                return true;
            case "failed":
                outcome = SmokeObservationOutcome.Failed;
                return true;
            case "not-run":
                outcome = SmokeObservationOutcome.NotRun;
                return true;
            default:
                outcome = default;
                return false;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
