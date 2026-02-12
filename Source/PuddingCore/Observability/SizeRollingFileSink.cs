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

            _formatter.Format(logEvent, _currentWriter!);
            _currentWriter!.Flush();
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
        _currentWriter = new StreamWriter(
            _currentFilePath,
            append: true,
            Encoding.UTF8,
            bufferSize: 4096);

        CleanupOldFiles();
    }

    private int FindNextSequence(string datePrefix)
    {
        if (!Directory.Exists(_logDirectory))
            return 1;

        var pattern = $"{_baseName}-{datePrefix}_*.log";
        var existing = Directory.GetFiles(_logDirectory, pattern);

        if (existing.Length == 0)
            return 1;

        var maxSeq = 0;
        foreach (var file in existing)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var underscoreIdx = fileName.LastIndexOf('_');
            if (underscoreIdx > 0 && int.TryParse(fileName[(underscoreIdx + 1)..], out var seq) && seq > maxSeq)
                maxSeq = seq;
        }

        return maxSeq + 1;
    }

    private void CleanupOldFiles()
    {
        if (!Directory.Exists(_logDirectory))
            return;

        var pattern = $"{_baseName}-*.log";
        var allFiles = Directory.GetFiles(_logDirectory, pattern);

        if (allFiles.Length <= _retainedFileCountLimit)
            return;

        var toDelete = allFiles
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(allFiles.Length - _retainedFileCountLimit);

        foreach (var file in toDelete)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private void CloseWriter()
    {
        if (_currentWriter is null) return;

        _currentWriter.Dispose();
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
