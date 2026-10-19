using PuddingHomeAssistant.Contracts;
using PuddingHomeAssistant.Diagnostics;
using PuddingHomeAssistant.Policy;
using PuddingHomeAssistant.Rest;

// S3 探针：对真实实例验证组件行为，不依赖宿主、不读 DataRoot。
// 用法：PuddingHomeAssistant.Probe <baseUrl> <token>

var baseUrl = args.Length > 0 ? args[0] : "http://192.168.100.10:8123/";
var token = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("HA_TOKEN") ?? string.Empty;

if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("USAGE: PuddingHomeAssistant.Probe <baseUrl> <token>   (or set HA_TOKEN)");
    return 2;
}

var instance = new HaInstance
{
    Alias = "probe",
    BaseUrl = new Uri(baseUrl, UriKind.Absolute),
    Token = token,
    OptInDomains = new HashSet<string>(StringComparer.Ordinal),
};

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var client = new HaRestClient(http);

try
{
    var status = await client.GetStatusAsync(instance, CancellationToken.None);
    Console.WriteLine($"STATUS={status.Message}");

    var config = await client.GetConfigAsync(instance, CancellationToken.None);
    Console.WriteLine($"VERSION={config.Version} TZ={config.TimeZone} COMPONENTS={config.ComponentCount}");

    var states = await client.GetStatesAsync(instance, CancellationToken.None);
    Console.WriteLine($"STATES_TOTAL={states.Count}");

    var page = HaOutputBudget.Project(states, 50, domains: ["light"]);
    Console.WriteLine($"LIGHTS_TOTAL={page.Total} RETURNED={page.Returned} BYTES={page.EstimatedBytes} TRUNCATED={page.Truncated}");

    var all = HaOutputBudget.Project(states, 500);
    Console.WriteLine($"ALL_RETURNED={all.Returned} BYTES={all.EstimatedBytes} UNDER_BUDGET={all.EstimatedBytes <= HaOutputBudget.MaxBytes}");

    // 关键负向验证：lock 必须被策略拦截，且不应发出请求
    try
    {
        await client.CallServiceAsync(
            instance, new HaServiceCall("lock", "unlock", null, null, false), CancellationToken.None);
        Console.WriteLine("POLICY=FAIL lock 未被拦截");
        return 1;
    }
    catch (HaClientException ex) when (ex.Code == HaErrorCodes.DomainBlocked)
    {
        Console.WriteLine("POLICY=OK lock domain_blocked");
    }

    // 白名单域：只读探测，不实际改变设备（调用 light.turn_on 会影响真实灯具，故不做）
    Console.WriteLine("PROBE=PASS");
    return 0;
}
catch (HaClientException ex)
{
    Console.WriteLine($"PROBE=FAIL code={ex.Code} message={ex.Message}");
    return 1;
}
