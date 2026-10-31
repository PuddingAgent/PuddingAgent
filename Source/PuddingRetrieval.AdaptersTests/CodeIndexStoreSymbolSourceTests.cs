using System.Reflection;
using PuddingCodeIndex.Contracts;
using PuddingRetrieval;
using PuddingRetrieval.Adapters;

namespace PuddingRetrieval.AdaptersTests;

/// <summary>
/// 适配器行为门禁：逐方法断言「窄端口的入参被原样转发给 <see cref="ICodeIndexStore"/>」。
/// <para>
/// <b>为什么不用手写 fake store</b>：<see cref="ICodeIndexStore"/> 有 30+ 成员，
/// 手写替身要写满全部方法体，成本高且一改接口就全体失效。<see cref="DispatchProxy"/>
/// 在运行时生成实现，只记录调用，其余方法返回默认值 —— 测试因此可以只关心被验证的 6 条转发。
/// </para>
/// <para>
/// <b>为什么要断言 <c>relationKind: null</c></b>：<see cref="ICodeIndexStore"/> 上同时存在
/// <c>relationKind</c>（单个，旧）与 <c>relationKinds</c>（集合）两个可选参数。若适配器不显式传
/// <c>relationKind: null</c>，将来有人给旧参数加默认值时，调用语义会被静默改写。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeIndexStoreSymbolSourceTests
{
    [TestMethod]
    public void NullStore_Throws()
    {
        var ex = Assert.ThrowsExactly<ArgumentNullException>(() => new CodeIndexStoreSymbolSource(null!));
        Assert.AreEqual("store", ex.ParamName);
    }

    [TestMethod]
    public async Task ListProjects_ForwardsScopeId()
    {
        var (store, recorder) = RecordingStore.Create();
        var adapter = new CodeIndexStoreSymbolSource(store);

        using var cts = new CancellationTokenSource();
        await adapter.ListProjectsAsync("ws-alpha", cts.Token);

        var call = recorder.Single(nameof(ICodeIndexStore.ListProjectsAsync));
        Assert.AreEqual("ws-alpha", call.Args[0], "scopeId 必须原样转发。");
        Assert.AreEqual(cts.Token, (CancellationToken)call.Args[1]!, "取消令牌必须原样转发，否则取消不了底层查询。");
    }

    [TestMethod]
    public async Task GetSymbol_ForwardsAllThreeIdentifiers()
    {
        var (store, recorder) = RecordingStore.Create();
        var adapter = new CodeIndexStoreSymbolSource(store);

        await adapter.GetSymbolAsync("ws", "proj", "sym-1", CancellationToken.None);

        var call = recorder.Single(nameof(ICodeIndexStore.GetSymbolAsync));
        CollectionAssert.AreEqual(new object?[] { "ws", "proj", "sym-1" }, call.Args.Take(3).ToArray());
    }

    [TestMethod]
    public async Task IncomingRelations_ForcesLegacyRelationKindToNull()
    {
        var (store, recorder) = RecordingStore.Create();
        var adapter = new CodeIndexStoreSymbolSource(store);
        IReadOnlyList<CodeRelationKind> kinds = [CodeRelationKind.Calls, CodeRelationKind.References];

        await adapter.ListIncomingRelationsAsync("ws", "proj", "sym-1", kinds, CancellationToken.None);

        var call = recorder.Single(nameof(ICodeIndexStore.ListIncomingRelationsAsync));
        // 参数顺序：workspaceId, projectId, symbolId, relationKind, relationKinds, cancellationToken
        Assert.IsNull(call.Args[3], "relationKind 必须显式为 null —— 不得让默认值偷偷改写语义。");
        CollectionAssert.AreEqual(kinds.ToArray(), ((IReadOnlyList<CodeRelationKind>)call.Args[4]!).ToArray());
    }

    [TestMethod]
    public async Task OutgoingRelations_RoutesToStoreListRelations()
    {
        var (store, recorder) = RecordingStore.Create();
        var adapter = new CodeIndexStoreSymbolSource(store);
        IReadOnlyList<CodeRelationKind> kinds = [CodeRelationKind.Calls];

        await adapter.ListOutgoingRelationsAsync("ws", "proj", "sym-1", kinds, CancellationToken.None);

        // 关键：出边在存储层的方法名是 ListRelationsAsync，不是 ListOutgoingRelationsAsync。
        var call = recorder.Single(nameof(ICodeIndexStore.ListRelationsAsync));
        Assert.AreEqual("sym-1", call.Args[2]);
        Assert.IsNull(call.Args[3], "relationKind 必须显式为 null。");
        CollectionAssert.AreEqual(kinds.ToArray(), ((IReadOnlyList<CodeRelationKind>)call.Args[4]!).ToArray());
    }

    /// <summary>运行时可生成的 <see cref="ICodeIndexStore"/> 记录器：只记录调用，不实现业务。</summary>
    private class RecordingStore : DispatchProxy
    {
        private readonly List<(string Method, object?[] Args)> _calls = [];

        public static (ICodeIndexStore Store, RecordingStore Recorder) Create()
        {
            var proxy = DispatchProxy.Create<ICodeIndexStore, RecordingStore>();
            return (proxy, (RecordingStore)(object)proxy);
        }

        public (string Method, object?[] Args) Single(string method)
        {
            var matched = _calls.Where(c => c.Method == method).ToArray();
            Assert.HasCount(1, matched, $"应恰好调用 {method} 一次，实际 {matched.Length} 次。");
            return matched[0];
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            _calls.Add((targetMethod.Name, args ?? []));

            var returnType = targetMethod.ReturnType;
            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = returnType.GetGenericArguments()[0];
                var fromResult = typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(inner);
                var defaultValue = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return fromResult.Invoke(null, [defaultValue]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
