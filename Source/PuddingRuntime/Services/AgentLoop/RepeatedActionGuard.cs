namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 无效循环检测（纯状态，无 IO）。
/// <para>
/// 目标模式有两侧钳制：一侧是「别停太早」（见 <see cref="GoalLoopDecider"/> 的决策顺序），
/// 另一侧是「该停就停」——本类负责后者：**同一个动作连续重复**说明这一轮没有产出，
/// 继续跑只是烧 token。
/// </para>
/// <para>
/// 为什么按**连续**重复而不是"窗口内出现次数"：交替模式（读 A → 改 B → 读 A → 改 B）
/// 在真实工作里是正常节奏，按窗口计数会被误判成空转。连续重复才是空转的确凿形态。
/// </para>
/// </summary>
public sealed class RepeatedActionGuard
{
    private readonly int _threshold;
    private string? _last;
    private int _streak;

    /// <param name="threshold">同一签名连续出现多少次即判定为循环（默认 3）。</param>
    public RepeatedActionGuard(int threshold = 3)
    {
        if (threshold < 2)
            throw new ArgumentOutOfRangeException(nameof(threshold), "threshold must be at least 2.");

        _threshold = threshold;
    }

    /// <summary>当前连续重复次数。</summary>
    public int Streak => _streak;

    /// <summary>记一个动作签名，返回是否已经构成循环。</summary>
    public bool Record(string? signature)
    {
        // 空签名代表"没有可比较的动作"：既不计数，也不打断已有连续计数
        // —— 否则中间插一条空动作就能把循环洗掉。
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        if (string.Equals(signature, _last, StringComparison.Ordinal))
            _streak++;
        else
        {
            _last = signature;
            _streak = 1;
        }

        return _streak >= _threshold;
    }

    /// <summary>清空历史（目标被编辑、或人类插话之后重新计）。</summary>
    public void Reset()
    {
        _last = null;
        _streak = 0;
    }
}
