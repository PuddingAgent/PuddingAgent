namespace PuddingCode.Models;

public sealed record SwarmSessionState
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string UserInput { get; set; } = "";
    public Contract Contract { get; init; } = new Contract
    {
        Id = "unknown",
        Files = [],
        Symbols = [],
        Specification = ""
    };
    public List<SwarmTask> Tasks { get; init; } = [];
}

