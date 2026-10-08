using PuddingSsh.Contracts;

namespace PuddingSsh.Probe;

/// <summary>探针命令行选项。所有取值都由外部脚本显式给出，探针**不做**目录搜索或首次信任。</summary>
internal sealed record ProbeOptions(
    string Scenario,
    string Host,
    int Port,
    string Username,
    string IdentityPath,
    string? ExpectedFingerprint,
    bool ExpectAccept,
    int OutputBudgetBytes,
    int OperationTimeoutSeconds,
    int ConnectTimeoutSeconds,
    string RemoteTempDirectory)
{
    public const string Usage = """
        用法: PuddingSsh.Probe <scenario> [选项]

        scenarios:
          fingerprint  只观测握手前出示的主机密钥并**始终拒绝**（用于与 ssh-keyscan 交叉核对指纹）
          hostkey      用 --fingerprint 建连：--expect accept|reject
          encrypted    用加密私钥建连：必须得到 ssh.passphrase_required
          deadline     用极短建连 deadline 连不可达地址：必须有界失败
          cancel       取消一个长时间命令：必须受限、且远端证据符合预期
          throughput   双流大输出：捕获必须受限、计数必须完整
          matrix       顺序执行上面全部（默认）

        选项:
          --host <地址>            默认 127.0.0.1
          --port <端口>            默认 22122
          --user <账号>            默认 root
          --identity <私钥路径>    必填（Windows 绝对路径）
          --fingerprint <SHA256:..>  期望指纹（hostkey 场景必填）
          --expect <accept|reject>  hostkey 场景的期望结果
          --budget <字节>          捕获预算，默认 65536
          --timeout <秒>           单次操作超时，默认 60
          --connect-timeout <秒>   建连 deadline，默认 15
          --remote-tmp <目录>      远端临时目录，默认 /tmp/pudding-ssh-probe
        """;

    public SshTargetRef Target => new(
        HostId: "wsl-openssh-probe",
        HostRevision: "probe-host-revision-1",
        Host: Host,
        Port: Port,
        Username: Username,
        TrustRevision: "probe-trust-revision-1");

    public static ProbeOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ProbeUsageException("缺少 scenario");
        }

        var scenario = args[0].ToLowerInvariant();
        string? host = null, user = null, identity = null, fingerprint = null, expect = null, remoteTmp = null;
        var port = 22122;
        var budget = 64 * 1024;
        var timeout = 60;
        var connectTimeout = 15;

        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ProbeUsageException($"选项 {key} 缺少取值");
                }

                return args[++i];
            }

            switch (key)
            {
                case "--host": host = Next(); break;
                case "--port": port = int.Parse(Next()); break;
                case "--user": user = Next(); break;
                case "--identity": identity = Next(); break;
                case "--fingerprint": fingerprint = Next(); break;
                case "--expect": expect = Next(); break;
                case "--budget": budget = int.Parse(Next()); break;
                case "--timeout": timeout = int.Parse(Next()); break;
                case "--connect-timeout": connectTimeout = int.Parse(Next()); break;
                case "--remote-tmp": remoteTmp = Next(); break;
                default: throw new ProbeUsageException($"未知选项 {key}");
            }
        }

        if (scenario is not ("fingerprint" or "hostkey" or "encrypted" or "deadline" or "cancel" or "throughput" or "matrix"))
        {
            throw new ProbeUsageException($"未知 scenario {scenario}");
        }

        if (identity is null)
        {
            throw new ProbeUsageException("必须给出 --identity");
        }

        if (scenario == "hostkey" && expect is null)
        {
            throw new ProbeUsageException("hostkey 场景必须给出 --expect accept|reject");
        }

        if (scenario == "hostkey" && fingerprint is null)
        {
            throw new ProbeUsageException("hostkey 场景必须给出 --fingerprint（reject 时给一个不匹配的诱饵指纹）");
        }

        if (expect is not null and not ("accept" or "reject"))
        {
            throw new ProbeUsageException("--expect 只能是 accept 或 reject");
        }

        return new ProbeOptions(
            scenario,
            host ?? "127.0.0.1",
            port,
            user ?? "root",
            identity,
            fingerprint,
            expect != "reject",
            budget,
            timeout,
            connectTimeout,
            remoteTmp ?? "/tmp/pudding-ssh-probe");
    }
}

internal sealed class ProbeUsageException(string message) : Exception(message);

/// <summary>断言/事实输出：一行一条，便于脚本与人工核对；退出码由失败条数决定。</summary>
internal sealed class ProbeLog
{
    private readonly List<string> _failures = [];

    public void Fact(string scenario, string name, object? value) =>
        Console.WriteLine($"FACT scenario={scenario} name={name} value={value}");

    public void Assert(string scenario, string name, bool ok, string detail)
    {
        Console.WriteLine($"ASSERT scenario={scenario} name={name} ok={(ok ? "true" : "false")} detail={detail}");
        if (!ok)
        {
            _failures.Add($"{scenario}/{name}: {detail}");
        }
    }

    public int Complete()
    {
        Console.WriteLine($"SUMMARY failures={_failures.Count}");
        foreach (var failure in _failures)
        {
            Console.WriteLine($"FAILED {failure}");
        }

        return _failures.Count == 0 ? 0 : 1;
    }
}
