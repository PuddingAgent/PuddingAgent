using System.Xml.Linq;

namespace Pudding.DesktopService;

/// <summary>
/// **从测试运行的 TRX 结果里读出真实计数**（交付清单 <c>checks</c> 的事实来源）。
///
/// 为什么必须从结果文件读、而不是让调用方"报个数"：交付清单里"通过 276 条、失败 0 条"这种句子
/// 一旦靠人工填写，就会变成**无法复核的声明**。TRX 是测试框架自己写的文件，读它至少保证数字来自一次真实运行。
///
/// 解析刻意保守：找不到计数器 / XML 不合法 / 属性缺失 ⇒ **失败**（不猜 0、不返回"通过了"）。
/// </summary>
public static class TrxTestResultParser
{
    /// <summary>
    /// 读一个 TRX 文件的内容。<paramref name="suiteName"/> 由调用方给出（TRX 里没有稳定的套件名）。
    /// </summary>
    public static bool TryParse(string suiteName, string? trxXml, out DeliveryCheck check, out string? error)
    {
        check = new DeliveryCheck(string.Empty, 0, 0);
        error = null;

        if (string.IsNullOrWhiteSpace(suiteName))
        {
            error = "a suite name is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(trxXml))
        {
            error = $"TRX content is missing for suite '{suiteName}'";
            return false;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(trxXml);
        }
        catch (System.Xml.XmlException exception)
        {
            error = $"TRX for suite '{suiteName}' is not valid XML: {exception.Message}";
            return false;
        }

        // TRX 有命名空间，且不同版本略有差异 ⇒ 按 LocalName 找 Counters。
        var counters = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Counters");

        if (counters is null)
        {
            error = $"TRX for suite '{suiteName}' has no Counters element (nothing actually ran?)";
            return false;
        }

        if (!TryReadInt(counters, "passed", out var passed)
            || !TryReadInt(counters, "failed", out var failed))
        {
            error = $"TRX for suite '{suiteName}' is missing passed/failed counters";
            return false;
        }

        check = new DeliveryCheck(suiteName.Trim(), passed, failed);
        return true;
    }

    /// <summary>批量读多个 TRX（任一失败即整体失败：不拿"读不全的检查"去交付）。</summary>
    public static bool TryParseAll(
        IReadOnlyList<(string SuiteName, string? TrxXml)> runs,
        out IReadOnlyList<DeliveryCheck> checks,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var parsed = new List<DeliveryCheck>(runs.Count);
        var problems = new List<string>();

        foreach (var (suiteName, trxXml) in runs)
        {
            if (TryParse(suiteName, trxXml, out var check, out var error))
            {
                parsed.Add(check);
            }
            else if (error is not null)
            {
                problems.Add(error);
            }
        }

        if (problems.Count > 0)
        {
            checks = [];
            errors = problems;
            return false;
        }

        if (parsed.Count == 0)
        {
            checks = [];
            errors = ["no TRX results were provided (an empty check list is not a delivery)"];
            return false;
        }

        checks = parsed;
        errors = [];
        return true;
    }

    private static bool TryReadInt(XElement element, string attributeName, out int value)
    {
        value = 0;
        var attribute = element.Attribute(attributeName);
        return attribute is not null
            && int.TryParse(
                attribute.Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }
}
