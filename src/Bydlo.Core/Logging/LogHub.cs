using Bydlo.Core.Security;

namespace Bydlo.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} {Level,-7} [{Source}] {Message}";
}

/// <summary>
/// Central redacting log sink: in-memory ring buffer for the UI plus an optional rotating file.
/// Every message is passed through <see cref="LogRedactor"/> before it is stored anywhere.
/// </summary>
public sealed class LogHub
{
    private readonly Lock _gate = new();
    private readonly LinkedList<LogEntry> _buffer = new();
    private readonly LogRedactor _redactor;
    private readonly int _capacity;
    private readonly string? _filePath;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    public LogHub(LogRedactor? redactor = null, string? logDirectory = null, int capacity = 2000)
    {
        _redactor = redactor ?? LogRedactor.Shared;
        _capacity = capacity;
        if (logDirectory is not null)
        {
            Directory.CreateDirectory(logDirectory);
            _filePath = Path.Combine(logDirectory, "bydlo.log");
        }
    }

    public event EventHandler<LogEntry>? EntryAdded;

    public LogRedactor Redactor => _redactor;

    public IReadOnlyList<LogEntry> Snapshot(string? source = null)
    {
        lock (_gate)
        {
            return source is null ? [.. _buffer] : [.. _buffer.Where(e => e.Source == source)];
        }
    }

    public void Clear(string? source = null)
    {
        lock (_gate)
        {
            if (source is null)
            {
                _buffer.Clear();
                return;
            }

            var node = _buffer.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Source == source)
                {
                    _buffer.Remove(node);
                }

                node = next;
            }
        }
    }

    public void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, source, _redactor.Redact(message));
        lock (_gate)
        {
            _buffer.AddLast(entry);
            while (_buffer.Count > _capacity)
            {
                _buffer.RemoveFirst();
            }

            AppendToFile(entry);
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void Info(string source, string message) => Write(LogLevel.Info, source, message);

    public void Warn(string source, string message) => Write(LogLevel.Warning, source, message);

    public void Error(string source, string message) => Write(LogLevel.Error, source, message);

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);

    private void AppendToFile(LogEntry entry)
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(_filePath);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                File.Move(_filePath, _filePath + ".1", overwrite: true);
            }

            File.AppendAllText(_filePath, entry + Environment.NewLine);
        }
        catch (IOException)
        {
            // Logging must never crash the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
