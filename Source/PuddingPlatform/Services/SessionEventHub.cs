using System.Collections.Concurrent;
using System.Threading.Channels;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// 会话级 SSE 事件总线：解耦执行与投递。
/// 后台执行任务向 Channel 写入帧，任意数量的前端 SSE 订阅者从 Channel 读取。
///
/// 生命周期：
///   执行开始 → GetOrCreate(sessionId) → 写入
///   前端连接 → Subscribe(sessionId) → 从 Channel 读取（已有帧 + 新帧）
///   执行结束 / 前端全部断开 → TryRemove(sessionId)
/// </summary>
public sealed class SessionEventHub
{
    // 每个活跃会话一个 Channel，多个订阅者共享读取
    // 使用 ConcurrentDictionary 保证线程安全
    private readonly ConcurrentDictionary<string, Channel<ServerSentEventFrame>> _channels = new();

    /// <summary>
    /// 获取或创建会话的事件 Channel，并返回 Reader。
    /// Producer（后台执行任务）使用 Writer 写入帧。
    /// </summary>
    public Channel<ServerSentEventFrame> GetOrCreate(string sessionId)
    {
        return _channels.GetOrAdd(sessionId, _ =>
        {
            // 有界 Channel：缓存最近 256 帧，新订阅者可以追上历史
            return Channel.CreateBounded<ServerSentEventFrame>(
                new BoundedChannelOptions(256)
                {
                    FullMode = BoundedChannelFullMode.DropOldest // 丢弃最旧帧，保证新帧可达
                });
        });
    }

    /// <summary>
    /// 获取会话 Channel 的 Reader。如果会话 Channel 不存在，返回 Default（null Channel）。
    /// 订阅者应检查 Reader 是否为 null，null 表示当前无执行中任务，只从 DB 加载即可。
    /// </summary>
    public ChannelReader<ServerSentEventFrame>? GetReader(string sessionId)
    {
        return _channels.TryGetValue(sessionId, out var ch) ? ch.Reader : null;
    }

    /// <summary>
    /// 移除会话 Channel（执行结束后调用）。
    /// 标记 Writer 完成，订阅者会正常退出 ReadAllAsync 循环。
    /// </summary>
    public void CompleteAndRemove(string sessionId)
    {
        if (_channels.TryRemove(sessionId, out var ch))
        {
            ch.Writer.TryComplete();
        }
    }

    /// <summary>
    /// 获取当前活跃会话数量（用于监控）。
    /// </summary>
    public int ActiveCount => _channels.Count;
}
