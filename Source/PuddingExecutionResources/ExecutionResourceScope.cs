using PuddingExecutionResources.Contracts;

namespace PuddingExecutionResources;

/// <summary>One lock for a budget tree. Reservations debit every ancestor atomically; siblings cannot overspend.</summary>
public sealed class ExecutionResourceScope : IExecutionResourceScope
{
    private readonly object _gate;
    private readonly ExecutionResourceScope? _parent;
    private readonly ExecutionResourceLimits _limits;
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private long _input, _output, _cache, _reservedOutput, _unknownOutput;
    private decimal _cost, _reservedCost, _unknownCost;
    private int _calls, _active;
    private bool _revoked;
    private string? _failure;
    public ExecutionResourceIdentity Identity { get; }
    public DateTimeOffset Deadline { get; }
    public CancellationToken Cancellation => _token;
    public ExecutionResourceScope(ExecutionResourceIdentity identity, ExecutionResourceLimits limits, DateTimeOffset deadline, CancellationToken parent = default)
        : this(identity, limits, deadline, parent, null) { }
    private ExecutionResourceScope(ExecutionResourceIdentity identity, ExecutionResourceLimits limits, DateTimeOffset deadline, CancellationToken token, ExecutionResourceScope? parent)
    {
        if (new[] { identity.WorkspaceId, identity.AgentId, identity.SessionId, identity.RunId }.Any(string.IsNullOrWhiteSpace)
            || limits.MaxOutputTokens < 0 || limits.MaxCost < 0 || limits.MaxCalls < 0 || limits.MaxInputTokensPerRequest < 0
            || limits.MaxConcurrentCalls is < 1 or > 64 || deadline > DateTimeOffset.UtcNow.AddDays(7))
            throw new ArgumentException("A bounded lifetime and valid host limits are required.");
        Identity = identity; _limits = limits; Deadline = deadline; _parent = parent; _gate = parent?._gate ?? new();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _stop.Token;
        _stop.CancelAfter(deadline - DateTimeOffset.UtcNow > TimeSpan.Zero ? deadline - DateTimeOffset.UtcNow : TimeSpan.Zero);
    }
    private IEnumerable<ExecutionResourceScope> Ancestors() { for (var p = this; p is not null; p = p._parent) yield return p; }
    public IExecutionResourceScope CreateChild(ExecutionResourceLimits limits, DateTimeOffset deadline)
    {
        lock (_gate)
        {
            EnsureLive();
            if (deadline <= DateTimeOffset.UtcNow) throw Error("execution.revoked", "The requested child lifetime has ended.");
            return new ExecutionResourceScope(Identity, limits, deadline < Deadline ? deadline : Deadline, Cancellation, this);
        }
    }
    public ExecutionResourceSnapshot Snapshot()
    {
        lock (_gate) return new(_input, _output, _cache, _cost, _reservedOutput, _reservedCost, _unknownOutput, _unknownCost,
            _calls, _active, _failure, _revoked || Cancellation.IsCancellationRequested);
    }
    // Existing main-loop and delegated usage may already have been dispatched outside this resource pool.
    // Record actual consumption, even if it exceeds limits; never label it a pre-reserved call.
    public void RecordExternal(ModelUsage usage, decimal cost)
    {
        ValidateUsage(usage);
        if (cost < 0) throw new ArgumentOutOfRangeException(nameof(cost));
        lock (_gate) foreach (var p in Ancestors()) p.AddUsage(usage, cost);
    }
    public void FailUnknown()
    {
        lock (_gate) foreach (var p in Ancestors()) p._failure ??= "budget.usage_unknown";
    }
    public IModelBudgetReservation Reserve(ModelResourceDescriptor model, int maxOutputTokens)
    {
        ValidateModel(model);
        if (maxOutputTokens < 1 || maxOutputTokens > model.MaxOutputTokens) throw Error("request.invalid", "Invalid output reservation.");
        var cost = Cost(model.Price, new(model.MaxInputTokens, maxOutputTokens));
        lock (_gate)
        {
            foreach (var p in Ancestors())
            {
                p.EnsureLive();
                if (p._failure is not null) throw Error(p._failure, "Parent budget is not available for further model calls.");
                if (p._limits.MaxInputTokensPerRequest is { } input && model.MaxInputTokens > input)
                    throw Error("budget.input_capacity", "Model input reservation exceeds parent capacity.");
                if (p._limits.MaxCalls is { } calls && p._calls >= calls
                    || p._limits.MaxOutputTokens is { } output && (decimal)p._output + p._unknownOutput + p._reservedOutput + maxOutputTokens > output
                    || p._limits.MaxCost is { } money && p._cost + p._unknownCost + p._reservedCost + cost > money)
                    throw Error("budget.exhausted", "Parent model budget is exhausted.");
                if (p._active >= p._limits.MaxConcurrentCalls) throw Error("budget.concurrency", "Parent model concurrency is occupied.");
            }
            foreach (var p in Ancestors()) { p._calls++; p._active++; p._reservedOutput += maxOutputTokens; p._reservedCost += cost; }
            return new Reservation(this, model, maxOutputTokens, cost);
        }
    }
    private void EnsureLive()
    {
        if (_revoked || Cancellation.IsCancellationRequested || DateTimeOffset.UtcNow >= Deadline)
            throw Error("execution.revoked", "The parent execution has ended.");
    }
    public void Dispose()
    {
        lock (_gate) { if (_revoked) return; _revoked = true; }
        try { _stop.Cancel(); } catch (AggregateException) { }
        // Saved handles and late reservations retain a readable cancelled token, not a live timer/registration.
        _stop.Dispose();
    }
    private void AddUsage(ModelUsage u, decimal cost)
    { _input = checked(_input + u.InputTokens); _output = checked(_output + u.OutputTokens); _cache = checked(_cache + u.CacheHitTokens); _cost = checked(_cost + cost); }
    internal static void ValidateUsage(ModelUsage u)
    {
        if (u.InputTokens < 0 || u.OutputTokens < 0 || u.CacheHitTokens < 0 || u.CacheHitTokens > u.InputTokens
            || u.InputTokens > int.MaxValue || u.OutputTokens > int.MaxValue) throw Error("model.usage_invalid", "Invalid provider usage.");
    }
    internal static void ValidateModel(ModelResourceDescriptor d)
    {
        if (d is null || new[] { d.Key, d.Revision, d.ModelId }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256)
            || d.MaxInputTokens is < 1 or > 10000000 || d.MaxOutputTokens is < 1 or > 1000000 || d.MaxConcurrentCalls is < 1 or > 64
            || d.Price is null || d.Price.InputPerMillion < 0 || d.Price.OutputPerMillion < 0 || d.Price.CacheHitPerMillion < 0
            || d.Price.CacheHitPerMillion > d.Price.InputPerMillion || d.Price.Unit != "configured-price-unit"
            || d.Price.InputPerMillion > 1000000 || d.Price.OutputPerMillion > 1000000)
            throw Error("model.configuration_invalid", "A bounded frozen model and pricing snapshot is required.");
    }
    public static decimal Cost(ModelPrice p, ModelUsage u)
        => ((u.InputTokens - u.CacheHitTokens) * p.InputPerMillion + u.CacheHitTokens * p.CacheHitPerMillion + u.OutputTokens * p.OutputPerMillion) / 1000000m;
    private static ExecutionResourceException Error(string code, string message) => new(code, message);
    private sealed class Reservation(ExecutionResourceScope owner, ModelResourceDescriptor model, int output, decimal cost) : IModelBudgetReservation
    {
        private bool _dispatched, _finished;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public bool Dispatched { get { lock (owner._gate) return _dispatched; } }
        public void MarkDispatched()
        {
            lock (owner._gate)
            {
                foreach (var p in owner.Ancestors()) p.EnsureLive();
                if (_finished || _dispatched) throw Error("model.dispatch_invalid", "Each reservation permits exactly one dispatch.");
                _dispatched = true;
            }
        }
        public void MarkUncertain()
        {
            lock (owner._gate) if (!_finished && _dispatched)
                foreach (var p in owner.Ancestors()) p._failure ??= "budget.usage_unknown";
        }
        public void Settle(ModelUsage usage)
        {
            ValidateUsage(usage);
            lock (owner._gate)
            {
                if (_finished || !_dispatched) throw Error("model.settlement_invalid", "A dispatched lease can be settled only once.");
                var actual = Cost(model.Price, usage);
                var overrun = usage.InputTokens > model.MaxInputTokens || usage.OutputTokens > output || actual > cost;
                foreach (var p in owner.Ancestors())
                {
                    p.AddUsage(usage, actual); p._reservedOutput -= output; p._reservedCost -= cost; p._active--;
                    if (overrun) p._failure = "budget.provider_overrun";
                }
                _finished = true;
                if (overrun) throw Error("budget.provider_overrun", "Provider usage exceeded the frozen reservation; actual usage was retained.");
            }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_finished) return;
                foreach (var p in owner.Ancestors())
                {
                    p._reservedOutput -= output; p._reservedCost -= cost; p._active--;
                    if (_dispatched) { p._unknownOutput += output; p._unknownCost += cost; p._failure ??= "budget.usage_unknown"; }
                }
                _finished = true;
            }
        }
    }
}
