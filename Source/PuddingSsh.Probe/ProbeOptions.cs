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
    string Expectation,
    int OutputBudgetBytes,
    int OperationTimeoutSeconds,
    int ConnectTimeoutSeconds,
    string RemoteTempDirectory,
    string? KnownHostsPath,
    string? IdentityDirectory,
    string? IdentityFileName,
    string? ScopeADirectory,
    string? ScopeBDirectory,
    string? RotatedKnownHostsPath,
    string? PresentCwd,
    string? MissingCwd,
    string? SourceSha)
{
    /// <summary>冻结的绝对 `.ssh` 目录（默认为 <c>--identity</c> 的所在目录）。</summary>
    public string EffectiveIdentityDirectory =>
        IdentityDirectory ?? Path.GetDirectoryName(Path.GetFullPath(IdentityPath)) ?? "/";

    /// <summary>冻结的 identity 文件名（默认为 <c>--identity</c> 的文件名）。</summary>
    public string EffectiveIdentityFileName => IdentityFileName ?? Path.GetFileName(IdentityPath);

    public SshIdentityRequest IdentityRequest => new(EffectiveIdentityDirectory, EffectiveIdentityFileName);

    public const string Usage = """
        用法: PuddingSsh.Probe <scenario> [选项]

        scenarios:
          fingerprint  只观测握手前出示的主机密钥并**始终拒绝**（用于与 ssh-keyscan 交叉核对指纹）
          hostkey      用 --fingerprint 建连：--expect accept|reject
          encrypted    用加密私钥建连：必须得到 ssh.passphrase_required
          deadline     用极短建连 deadline 连不可达地址：必须有界失败
          cancel       取消一个长时间命令：必须受限、且远端证据符合预期
          throughput   双流大输出：捕获必须受限、计数必须完整
          coordinator  经 ISshClient 协调器走完整链路（ssh_test / 成功执行 / 错指纹 / 非零退出 / 非法请求）
          knownhosts   真实 known_hosts 记录端到端：--expect accept|revoked|rotated（撤销/换 key 必须被拒）
          dirs         两类 .ssh 目录（current_user / agent_private 形态）互不回退，且组件不写这两个目录
          ignoreterm   服务端忽略 TERM：取消后不得声称远端已退出（execution_state 必须 unknown）
          disconnect   命令执行中连接被服务端切断：必须是 connection_lost + 状态未知、绝不重放
          auth         错误账号：必须 authentication_failed，且不得发生命令提交
          precancel    预先取消/已过期 deadline：零连接零认证零 marker
          lateconnect  极短 deadline 下真实连接迟到：不得执行 marker
          noeof        服务端不发 stdout EOF：关闭与 reader 在宽限内收敛
          submitfail   提交窗口断线：unknown 且 retry_safe=false
          rounds       多轮成功/超时/取消后资源计数回基线
          signal       signal-only 退出映射与已知 exit code 保留
          cwd          cwd 守卫：cd 失败时整个脚本零执行、零副作用；cd 正常时原脚本语义不变
          matrix       顺序执行上面全部（默认）

        选项:
          --host <地址>            默认 127.0.0.1
          --port <端口>            默认 22122
          --user <账号>            默认 root
          --identity <私钥路径>    必填（Windows 绝对路径）
          --identity-dir <目录>    冻结的 .ssh 目录（默认取 --identity 的目录）
          --identity-name <文件名> 冻结的 identity 文件名（默认取 --identity 的文件名）
          --fingerprint <SHA256:..>  期望指纹（hostkey / coordinator 场景）
          --expect <accept|reject|revoked|rotated>  hostkey / knownhosts 场景的期望结果
          --known-hosts <路径>     knownhosts 场景的信任文件
          --scope-a <目录>         dirs 场景的第一个 .ssh 目录（默认 = identity 目录）
          --scope-b <目录>         dirs 场景的第二个 .ssh 目录
          --rotated-known-hosts <路径>  dirs 场景的「换 key」信任文件
          --cwd <路径>             cwd 场景中「存在」的目录，默认 /tmp
          --missing-cwd <路径>     cwd 场景中「不存在」的目录
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
        string? knownHosts = null, identityDir = null, identityName = null, scopeA = null, scopeB = null, rotatedKnownHosts = null;
        string? presentCwd = null, missingCwd = null, sourceSha = null;
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
                case "--identity-dir": identityDir = Next(); break;
                case "--identity-name": identityName = Next(); break;
                case "--fingerprint": fingerprint = Next(); break;
                case "--expect": expect = Next(); break;
                case "--known-hosts": knownHosts = Next(); break;
                case "--scope-a": scopeA = Next(); break;
                case "--scope-b": scopeB = Next(); break;
                case "--rotated-known-hosts": rotatedKnownHosts = Next(); break;
                case "--cwd": presentCwd = Next(); break;
                case "--missing-cwd": missingCwd = Next(); break;
                case "--source-sha": sourceSha = Next(); break;
                case "--budget": budget = int.Parse(Next()); break;
                case "--timeout": timeout = int.Parse(Next()); break;
                case "--connect-timeout": connectTimeout = int.Parse(Next()); break;
                case "--remote-tmp": remoteTmp = Next(); break;
                default: throw new ProbeUsageException($"未知选项 {key}");
            }
        }

        if (scenario is not ("fingerprint" or "hostkey" or "encrypted" or "deadline" or "cancel" or "throughput"
            or "coordinator" or "knownhosts" or "dirs" or "ignoreterm" or "disconnect" or "auth" or "cwd"
            or "precancel" or "lateconnect" or "noeof" or "submitfail" or "rounds" or "signal" or "matrix"))
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

        if (scenario == "knownhosts" && knownHosts is null)
        {
            throw new ProbeUsageException("knownhosts 场景必须给出 --known-hosts");
        }

        if (expect is not null and not ("accept" or "reject" or "revoked" or "rotated"))
        {
            throw new ProbeUsageException("--expect 只能是 accept / reject / revoked / rotated");
        }

        return new ProbeOptions(
            scenario,
            host ?? "127.0.0.1",
            port,
            user ?? "root",
            identity,
            fingerprint,
            expect ?? "accept",
            budget,
            timeout,
            connectTimeout,
            remoteTmp ?? "/tmp/pudding-ssh-probe",
            knownHosts,
            identityDir,
            identityName,
            scopeA,
            scopeB,
            rotatedKnownHosts,
            presentCwd,
            missingCwd,
            sourceSha);
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
