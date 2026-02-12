using System.Collections.Concurrent;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

/// <summary>
/// 基于文件系统的蜂群传输实现。本地模式下通过文件系统实现 Agent 之间的消息传递。
/// </summary>
public sealed class FileSwarmTransport : ISwarmTransport, IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _messagesDirectory;
    private readonly string _nodeId;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers;
    private readonly BlockingCollection<SwarmMessage> _messageQueue;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="FileSwarmTransport"/> 类的新实例。
    /// </summary>
    /// <param name="swarmDir">蜂群目录路径（.pudding/swarm/）</param>
    /// <param name="nodeId">当前节点 ID</param>
    public FileSwarmTransport(string swarmDir, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(swarmDir);
        ArgumentNullException.ThrowIfNull(nodeId);

        _messagesDirectory = Path.Combine(swarmDir, "messages");
        _nodeId = nodeId;
        _watchers = new ConcurrentDictionary<string, FileSystemWatcher>();
        _messageQueue = new BlockingCollection<SwarmMessage>();
        _cts = new CancellationTokenSource();

        // 确保消息目录存在
        Directory.CreateDirectory(_messagesDirectory);
    }

    /// <inheritdoc />
    public async Task SendAsync(string targetNodeId, SwarmMessage message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(targetNodeId);
        ArgumentNullException.ThrowIfNull(message);

        var inboxPath = GetInboxFilePath(targetNodeId);

        // 读取现有消息列表
        var messages = await LoadInboxMessagesAsync(inboxPath, ct);

        // 添加新消息
        messages.Add(message);

        // 写回文件
        await SaveInboxMessagesAsync(inboxPath, messages, ct);
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(SwarmMessage message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);

        var broadcastPath = Path.Combine(_messagesDirectory, "broadcast.json");

        // 读取现有广播消息列表
        var messages = await LoadInboxMessagesAsync(broadcastPath, ct);

        // 添加新消息
        messages.Add(message);

        // 写回文件
        await SaveInboxMessagesAsync(broadcastPath, messages, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SwarmMessage> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inboxPath = GetInboxFilePath(_nodeId);

        // 确保收件箱文件存在
        if (!File.Exists(inboxPath))
        {
            File.WriteAllText(inboxPath, "[]");
        }

        // 设置 FileSystemWatcher 监听文件变化
        SetupWatcher(_nodeId);

        // 先读取现有消息
        var existingMessages = await LoadInboxMessagesAsync(inboxPath, ct);
        foreach (var message in existingMessages)
        {
            yield return message;
        }

        // 清空已读取的消息
        await SaveInboxMessagesAsync(inboxPath, [], ct);

        // 监听新消息
        while (!ct.IsCancellationRequested && !_disposed)
        {
            // 从队列中获取新消息（带超时）
            if (_messageQueue.TryTake(out var message, 100, ct))
            {
                yield return message;
            }
        }
    }

    /// <summary>
    /// 获取收件箱文件路径。
    /// </summary>
    private string GetInboxFilePath(string nodeId) =>
        Path.Combine(_messagesDirectory, $"{nodeId}.inbox.json");

    /// <summary>
    /// 从收件箱文件加载消息列表。
    /// </summary>
    private static async Task<List<SwarmMessage>> LoadInboxMessagesAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(filePath, ct);
        var messages = JsonSerializer.Deserialize<List<SwarmMessage>>(json, s_jsonOptions);

        return messages ?? [];
    }

    /// <summary>
    /// 保存消息列表到收件箱文件。
    /// </summary>
    private static async Task SaveInboxMessagesAsync(string filePath, List<SwarmMessage> messages, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(messages, s_jsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    /// <summary>
    /// 为指定节点设置文件系统监视器。
    /// </summary>
    private void SetupWatcher(string nodeId)
    {
        var inboxPath = GetInboxFilePath(nodeId);
        var directory = Path.GetDirectoryName(inboxPath) ?? _messagesDirectory;
        var fileName = Path.GetFileName(inboxPath);

        // 如果已存在监视器，则不重复创建
        if (_watchers.ContainsKey(nodeId))
        {
            return;
        }

        var watcher = new FileSystemWatcher
        {
            Path = directory,
            Filter = fileName,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        watcher.Changed += OnInboxChanged;
        watcher.EnableRaisingEvents = true;

        _watchers.TryAdd(nodeId, watcher);
    }

    /// <summary>
    /// 处理收件箱文件变化事件。
    /// </summary>
    private async void OnInboxChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // 延迟一小段时间确保文件写入完成
            await Task.Delay(50);

            // 读取新消息
            if (File.Exists(e.FullPath))
            {
                var messages = await LoadInboxMessagesAsync(e.FullPath, _cts.Token);

                foreach (var message in messages)
                {
                    _messageQueue.Add(message);
                }

                // 清空已读取的消息
                await SaveInboxMessagesAsync(e.FullPath, [], _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消，忽略
        }
        catch (Exception ex)
        {
            // 记录错误但不抛出（避免崩溃）
            Console.Error.WriteLine($"FileSwarmTransport error watching inbox: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        // 停止所有监视器
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _messageQueue.CompleteAdding();
        _cts.Dispose();

        await Task.CompletedTask;
    }
}
