using System.Collections.Immutable;
using PuddingInvocationPipeline.Contracts;

namespace PuddingInvocationPipeline;

/// <summary>
/// PA-07 07B pipeline plan and transform fence: Guards run before Transforms before Advice/Observer, each kind by
/// descending priority then stable id. An extension runs only when the host granted every declared capability;
/// other extensions are reported as skipped, never silently rewritten. A transform can never change tool identity,
/// authorization, the real exit code, budget ownership or a failure code, and can never flip a failure into success.
/// </summary>
public sealed class ExtensionPipeline
{
    public ExtensionPlan Plan(ExtensionContext context, ExtensionSnapshot snapshot, long observedGeneration)
    {
        if (context is null || snapshot is null)
            throw Error(ExtensionErrorCodes.DescriptorInvalid, "A context and a snapshot are required.");
        if (!ExtensionPoints.Known(context.Point))
            throw Error(ExtensionErrorCodes.PointUnknown, "The extension point is not published.");
        ExtensionRegistry.EnsureGeneration(observedGeneration, snapshot.Generation);
        var granted = context.GrantedCapabilities.IsDefault
            ? ImmutableHashSet<string>.Empty
            : context.GrantedCapabilities.ToImmutableHashSet(StringComparer.Ordinal);
        var ordered = ImmutableArray.CreateBuilder<ExtensionDescriptor>();
        var skipped = ImmutableArray.CreateBuilder<ExtensionDescriptor>();
        foreach (var descriptor in snapshot.Extensions
                     .Where(d => string.Equals(d.Point, context.Point, StringComparison.Ordinal))
                     .OrderBy(d => Rank(d.Kind))
                     .ThenByDescending(d => d.Priority)
                     .ThenBy(d => d.ExtensionId, StringComparer.Ordinal))
        {
            if (descriptor.Grants.IsDefaultOrEmpty || descriptor.Grants.All(granted.Contains)) ordered.Add(descriptor);
            else skipped.Add(descriptor);
        }
        return new(ordered.ToImmutable(), skipped.ToImmutable());
    }

    public static void ValidateTransform(ExtensionDescriptor descriptor, IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after, bool wasFailure, bool nowSuccess)
    {
        if (descriptor is null || descriptor.Kind != ExtensionKind.Transform)
            throw Error(ExtensionErrorCodes.TransformDenied, "Only a transform extension may rewrite tool data.");
        if (before is null || after is null)
            throw Error(ExtensionErrorCodes.TransformForbidden, "Before/after data is required.");
        foreach (var field in ProtectedToolFields.All)
        {
            var had = before.TryGetValue(field, out var oldValue);
            var has = after.TryGetValue(field, out var newValue);
            if (had != has || !string.Equals(oldValue, newValue, StringComparison.Ordinal))
                throw Error(ExtensionErrorCodes.TransformForbidden,
                    $"A transform may not change '{field}' (tool identity/authorization/exit/budget/failure).");
        }
        if (wasFailure && nowSuccess)
            throw Error(ExtensionErrorCodes.TransformForbidden,
                "A transform may not turn a failed tool result into a success.");
    }

    private static int Rank(ExtensionKind kind) => kind switch
    {
        ExtensionKind.Guard => 0,
        ExtensionKind.Transform => 1,
        ExtensionKind.Advice => 2,
        _ => 3,
    };

    private static ExtensionException Error(string code, string message) => new(code, message);
}
