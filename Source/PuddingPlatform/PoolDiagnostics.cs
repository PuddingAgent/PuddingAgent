namespace PuddingPlatform.Services;

public static class PoolDiagnostics
{
    public static string FormatPoolSummary(SubAgentPool pool)
    {
        var agents = pool.List();
        int idle = 0, busy = 0, sleeping = 0, dead = 0;
        foreach (var a in agents)
        {
            switch (a.Status)
            {
                case PooledSubAgentStatus.Idle: idle++; break;
                case PooledSubAgentStatus.Busy: busy++; break;
                case PooledSubAgentStatus.Sleeping: sleeping++; break;
                case PooledSubAgentStatus.Dead: dead++; break;
            }
        }
        return $"Pool: {agents.Count} agents | Idle={idle} Busy={busy} Sleeping={sleeping} Dead={dead}";
    }
}
