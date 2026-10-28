using System.Text;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace PuddingCode.Observability;

public sealed class SizeRollingFileSink : ILogEventSink, IDisposable
{
    private readonly string _logDirectory;
    private readonly string _baseName;
    private readonly long _maxFileSizeBytes;
    private readonly int _retainedFileCountLimit;
    private readonly MessageTemplateTextFormatter _formatter;
    private readonly object _sync = new();
    private StreamWriter? _currentWriter;
    private string? _currentFilePath;
    private int _sequence;
    private string? _currentDatePrefix;

    public SizeRollingFileSink(
        string logDirectory,
        string baseName,
        long maxFileSizeBytes = 1_048_576,
        int retainedFileCountLimit = 200,
        string outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    {
        _logDirectory = logDirectory;
        _baseName = baseName;
        _maxFileSizeBytes = maxFileSizeBytes;
        _retainedFileCountLimit = Math.Max(retainedFileCountLimit, 2);
        _formatter = new MessageTemplateTextFormatter(outputTemplate, null);
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null) return;

        lock (_sync)
        {
            SwitchFileIfNeeded(logEvent.Timestamp);

            try
            {
                _formatter.Format(logEvent, _currentWriter!);
                _currentWriter!.Flush();
            }
            catch (Exception ex)
            {
                // 格式化/写入失败不应让日志异常冒泡到宿主；
                // 丢弃 writer 以便下一条事件重建文件（自愈）。
                Serilog.Debugging.SelfLog.WriteLine(
                    "SizeRollingFileSink emit failed for {0}: {1}", _currentFilePath ?? "(null)", ex);
                CloseWriter();
            }
        }
    }

    private void SwitchFileIfNeeded(DateTimeOffset timestamp)
    {
        var datePrefix = timestamp.ToString("yyyyMMdd");
        var needSwitch = _currentWriter is null || _currentDatePrefix != datePrefix;

        if (!needSwitch)
        {
            needSwitch = _currentWriter!.BaseStream.Length >= _maxFileSizeBytes;
        }

        // 自愈：当前文件若被外部删除（例如另一进程的清理逻辑误删），
        // 强制重建，避免把事件写入已被删除的孤立句柄（表现为磁盘上 0KB 空文件滚动）。
        if (!needSwitch && !File.Exists(_currentFilePath))
        {
            needSwitch = true;
        }

        if (!needSwitch)
            return;

        if (_currentWriter is null)
        {
            _sequence = FindNextSequence(datePrefix);
        }
        else if (_currentDatePrefix != datePrefix)
        {
            CloseWriter();
            _sequence = FindNextSequence(datePrefix);
        }
        else
        {
            CloseWriter();
            _sequence++;
        }

        _currentDatePrefix = datePrefix;
        Directory.CreateDirectory(_logDirectory);
        _currentFilePath = Path.Combine(
            _logDirectory,
            $"{_baseName}-{datePrefix}_{_sequence:D3}.log");

        // FileShare.Read：允许诊断工具在进程运行时读取日志文件
        // （旧实现用 StreamWriter 路径便捷重载，等效 FileShare.None，连只读检查都被拒绝）。
        var stream = new FileStream(
            _currentFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096);
        _currentWriter = new StreamWriter(stream, Encoding.UTF8, bufferSize: 4096, leaveOpen: false);

        CleanupOldFiles();
    }

    private int FindNextSequence(string datePrefix)
    {
        if (!Directory.Exists(_logDirectory))
            return 1;

        var maxSeq = 0;
        foreach (var seq in EnumerateSequences(datePrefix))
        {
            if (seq > maxSeq) maxSeq = seq;
        }

        return maxSeq + 1;
    }

    /// <summary>
    /// 解析 {baseName}-{datePrefix}_{seq}.log 中的数字序号。
    /// 序号超过 999 后文件名宽度不一致，必须按数值而非字符串比较，
    /// 否则 "10000" 会被字符串排序排在 "9999" 之前（旧实现的清理顺序 bug）。
    /// </summary>
    private IEnumerable<int> EnumerateSequences(string datePrefix)
    {
        var pattern = $"{_baseName}-{datePrefix}_*.log";
        string[] files;
        try
        {
            files = Directory.GetFiles(_logDirectory, pattern);
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var underscoreIdx = fileName.LastIndexOf('_');
            if (underscoreIdx > 0 && int.TryParse(fileName[(underscoreIdx + 1)..], out var seq))
                yield return seq;
        }
    }

    private void CleanupOldFiles()
    {
        if (!Directory.Exists(_logDirectory))
            return;

        var pattern = $"{_baseName}-*.log";
        string[] allFiles;
        try
        {
            allFiles = Directory.GetFiles(_logDirectory, pattern);
        }
        catch (IOException)
        {
            return;
        }

        if (allFiles.Length <= _retainedFileCountLimit)
            return;

        // 保留最新的 N 个：先按**日期**降序（yyyyMMdd 定宽，序数比较即时间序），再按当日序号降序。
        //
        // 旧实现只按序号降序，而序号是**按日重置**的（FindNextSequence(datePrefix)）。
        // 于是今天的 _020 会排在历史日期的 _060 之后，被当成「最旧」删掉。
        // 现场证据（2026-10-28）：D:\Data\logs\system 下 pudding-20261028_001.._020 全部消失，
        // 而 pudding-20261009_043.._060 全部还在 —— 当天排障要用的日志被自己的清理删了。
        var toDelete = allFiles
            .Select(file => (File: file, Key: ParseDateAndSequence(file)))
            .OrderByDescending(x => x.Key.Date, StringComparer.Ordinal)
            .ThenByDescending(x => x.Key.Sequence)
            .ThenByDescending(x => x.File, StringComparer.Ordinal)
            .Skip(_retainedFileCountLimit)
            .Select(x => x.File);

        foreach (var file in toDelete)
        {
            // 绝不清理当前正在写入的文件（防止序号解析边界或并发竞态误删）。
            if (string.Equals(file, _currentFilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(file);
            }
            catch
            {
                // best effort cleanup（被其他进程独占打开时删除会失败，跳过即可）
            }
        }
    }

    /// <summary>
    /// 从 {baseName}-{yyyyMMdd}_{seq}.log 里取出（日期前缀, 序号）。
    /// <para>
    /// 必须同时返回日期：序号按日重置，只比序号会把「今天刚建的 _002」判成比「上个月的 _060」更旧。
    /// 解析失败时返回空日期 + 0，这类文件会排在最末，优先被清理。
    /// </para>
    /// </summary>
    private static (string Date, int Sequence) ParseDateAndSequence(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var dashIdx = fileName.LastIndexOf('-');
        if (dashIdx < 0 || dashIdx + 1 >= fileName.Length)
            return (string.Empty, 0);

        var tail = fileName[(dashIdx + 1)..];
        var underscoreIdx = tail.LastIndexOf('_');
        if (underscoreIdx <= 0)
            return (string.Empty, 0);

        var date = tail[..underscoreIdx];
        return int.TryParse(tail[(underscoreIdx + 1)..], out var seq) ? (date, seq) : (date, 0);
    }

    private void CloseWriter()
    {
        if (_currentWriter is null) return;

        try
        {
            _currentWriter.Dispose();
        }
        catch
        {
            // 写入器释放失败不影响切换
        }
        _currentWriter = null;
        _currentFilePath = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            CloseWriter();
        }
    }
}
