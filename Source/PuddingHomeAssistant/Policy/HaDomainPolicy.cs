using PuddingHomeAssistant.Contracts;

namespace PuddingHomeAssistant.Policy;

/// <summary>域策略判定结果。</summary>
public enum HaDomainDecision
{
    /// <summary>在当前配置下允许调用。</summary>
    Allowed = 0,

    /// <summary>属于可选放开集合，但用户未在 Admin 中开启 → 拒绝并提示去开启。</summary>
    OptInRequired = 1,

    /// <summary>永久拒绝：无论配置如何都不允许（可在 HA 内执行任意逻辑或管理宿主机）。</summary>
    Blocked = 2,
}

/// <summary>
/// 动作域策略。这是本组件**最重要的安全边界**：
/// 一个 `POST /api/services/&lt;domain&gt;/&lt;service&gt;` 能做的事远超「开灯」——
/// 可以开锁、撤防、开车库门，甚至在宿主机执行任意命令（`shell_command`）。
///
/// <para>三档模型（fail-closed）：</para>
/// <list type="bullet">
/// <item><b>永久拒绝</b>：`shell_command` / `hassio` / `homeassistant` / `script` / `automation` /
/// `python_script` / `pyscript` / `node_red` / `lock` / `alarm_control_panel` / `camera`。
/// 这些不是「配置问题」，而是「工具不该拥有这条通路」。</item>
/// <item><b>内置允许</b>：`light` / `switch` / `scene` / `input_*` / `notify` / `tts` / `button` 等
/// 无物理安全后果的常规控制。</item>
/// <item><b>需显式放开</b>：`media_player` / `fan` / `climate` / `humidifier` / `water_heater` /
/// `cover` / `vacuum` / `valve` / `siren`。默认不放开，由用户在 Admin 界面逐域开启。</item>
/// </list>
///
/// 未在任一清单中的域**默认不放行**（同样归入 OptInRequired），避免 HA 新增集成时被自动授权。
/// </summary>
public static class HaDomainPolicy
{
    private static readonly HashSet<string> s_alwaysBlocked = new(StringComparer.Ordinal)
    {
        // 可在 HA 内执行任意逻辑 / 触碰宿主机
        "shell_command", "hassio", "homeassistant", "python_script", "pyscript", "node_red",
        "script", "automation", "scene_editor", "pyscript_runtime",
        // 物理安全：开锁 / 撤防 / 摄像头画面
        "lock", "alarm_control_panel", "camera",
        // 系统自省与配置写入
        "system_log", "recorder", "logger", "frontend", "config", "persistent_notification",
        "homeassistant_scene", "mqtt", "rest_command", "command_line",
    };

    private static readonly HashSet<string> s_allowedByDefault = new(StringComparer.Ordinal)
    {
        "light", "switch", "scene", "input_boolean", "input_number", "input_select",
        "input_text", "input_datetime", "notify", "tts", "button", "select", "number",
        "text", "todo", "calendar",
    };

    private static readonly HashSet<string> s_optIn = new(StringComparer.Ordinal)
    {
        "media_player", "fan", "climate", "humidifier", "water_heater",
        "cover", "vacuum", "valve", "siren",
    };

    /// <summary>判定某域在当前实例配置下是否可以调用。</summary>
    public static HaDomainDecision Decide(string? domain, HaInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!TryNormalize(domain, out var normalized))
        {
            return HaDomainDecision.Blocked;
        }

        if (s_alwaysBlocked.Contains(normalized))
        {
            return HaDomainDecision.Blocked;
        }

        if (s_allowedByDefault.Contains(normalized))
        {
            return HaDomainDecision.Allowed;
        }

        if (s_optIn.Contains(normalized))
        {
            return instance.OptInDomains.Contains(normalized)
                ? HaDomainDecision.Allowed
                : HaDomainDecision.OptInRequired;
        }

        // 未知域：默认不放行；但用户在 Admin 中显式放开后必须可用，
        // 否则 HA 新增集成永远无法获得授权（这是策略漏洞，不是保守）。
        return instance.OptInDomains.Contains(normalized)
            ? HaDomainDecision.Allowed
            : HaDomainDecision.OptInRequired;
    }

    /// <summary>
    /// 规范化 domain / service 标识：小写、去空白，且只允许 `[a-z0-9_]`。
    /// 拒绝含 `.` `/` `:` 空格等字符 —— 那些可用于拼接出意料之外的路径段。
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 64)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            var ok = ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
            if (!ok)
            {
                return false;
            }
        }

        normalized = trimmed.ToLowerInvariant();
        return normalized.Length > 0;
    }

    /// <summary>供 Admin 界面展示：当前用户可选择的 opt-in 域（按字母序）。</summary>
    public static IReadOnlyList<string> OptInCandidateDomains() =>
        s_optIn.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>供 Admin 界面展示：永久拒绝的域（不可配置）。</summary>
    public static IReadOnlyList<string> AlwaysBlockedDomains() =>
        s_alwaysBlocked.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>供 Admin 界面展示：默认允许的域。</summary>
    public static IReadOnlyList<string> DefaultAllowedDomains() =>
        s_allowedByDefault.OrderBy(x => x, StringComparer.Ordinal).ToArray();
}
