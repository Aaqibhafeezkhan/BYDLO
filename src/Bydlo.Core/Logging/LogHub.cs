using System.Text;
using System.Threading.Channels;
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
/// Every message is passed through <see cref="LogRedactor"/> exactly once before it is stored:
/// here for ordinary messages, at the source for engine lines (<see cref="WriteRedacted"/>, issue #357).
/// The file is written by a background task over a <see cref="Channel{T}"/> that keeps one
/// <see cref="FileStream"/> open, and the UI receives coalesced batches instead of one callback
/// per line.
/// </summary>
public sealed class LogHub : IDisposable
{
    /// <summary>How often coalesced batches reach the UI (issue #357 asks for 100–250 ms).</summary>
    public static readonly TimeSpan UiFlushInterval = TimeSpan.FromMilliseconds(150);

    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int FileQueueCapacity = 16 * 1024;

    private readonly Lock _gate = new();
    private readonly Lock _flushGate = new();
    private readonly Queue<LogEntry> _buffer = new();
    private readonly Queue<LogEntry> _pending = new();
    private readonly List<TaskCompletionSource> _flushWaiters = [];
    private readonly LogRedactor _redactor;
    private readonly int _capacity;
    private readonly string? _filePath;
    private readonly Channel<LogEntry?>? _fileQueue;
    private readonly Task? _fileWriter;
    private readonly Timer _uiTimer;
    private int _disposed;

    public LogHub(LogRedactor? redactor = null, string? logDirectory = null, int capacity = 2000)
    {
        _redactor = redactor ?? LogRedactor.Shared;
        _capacity = capacity;
        if (logDirectory is not null)
        {
            Directory.CreateDirectory(logDirectory);
            _filePath = Path.Combine(logDirectory, "bydlo.log");
            // Bounded on purpose: a stalled disk must never block the engine's stdout reader.
            // 16k lines is several seconds of a verbose engine; the writer drains far faster.
            var queue = Channel.CreateBounded<LogEntry?>(new BoundedChannelOptions(FileQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
            _fileQueue = queue;
            _fileWriter = Task.Run(() => WriteFileAsync(queue.Reader));
        }

        _uiTimer = new Timer(_ => FlushPending(), null, UiFlushInterval, UiFlushInterval);
    }

    /// <summary>Raised synchronously for every entry. UI code should use <see cref="EntriesAdded"/>.</summary>
    public event EventHandler<LogEntry>? EntryAdded;

    /// <summary>Coalesced batch of entries, raised on a background thread every <see cref="UiFlushInterval"/>.</summary>
    public event EventHandler<IReadOnlyList<LogEntry>>? EntriesAdded;

    public LogRedactor Redactor => _redactor;

    public IReadOnlyList<LogEntry> Snapshot(string? source = null)
    {
        lock (_gate)
        {
            if (source is null)
            {
                return _buffer.ToArray();
            }

            return _buffer.Where(e => e.Source == source).ToArray();
        }
    }

    public void Clear(string? source = null)
    {
        lock (_gate)
        {
            if (source is null)
            {
                _buffer.Clear();
                _pending.Clear();
                return;
            }

            RemoveSource(_buffer, source);
            RemoveSource(_pending, source);
        }
    }

    public void Write(LogLevel level, string source, string message) => Append(level, source, _redactor.Redact(message));

    /// <summary>
    /// Stores an engine line that the engine already passed through its own <see cref="LogRedactor"/>
    /// (see <see cref="Engines.IEngine.Log"/>). Redacting at the source matters: a helper-hosted engine
    /// registers its secrets only in the helper process, so this sink could not mask them. Skipping the
    /// second pass keeps redaction at one per line (issue #357).
    /// </summary>
    public void WriteRedacted(LogLevel level, string source, string redactedMessage) => Append(level, source, redactedMessage);

    private void Append(LogLevel level, string source, string message)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, source, message);
        lock (_gate)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > _capacity)
            {
                _buffer.Dequeue();
            }

            _pending.Enqueue(entry);
            while (_pending.Count > _capacity)
            {
                _pending.Dequeue();
            }
        }

        _fileQueue?.Writer.TryWrite(entry);
        EntryAdded?.Invoke(this, entry);
    }

    public void Info(string source, string message) => Write(LogLevel.Info, source, message);

    public void Warn(string source, string message) => Write(LogLevel.Warning, source, message);

    public void Error(string source, string message) => Write(LogLevel.Error, source, message);

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);

    /// <summary>Waits until every entry written so far has reached the file.</summary>
    public Task FlushAsync()
    {
        if (_fileQueue is null || Volatile.Read(ref _disposed) != 0)
        {
            return Task.CompletedTask;
        }

        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_flushGate)
        {
            _flushWaiters.Add(waiter);
        }

        if (!_fileQueue.Writer.TryWrite(null))
        {
            // The writer is gone; nothing will ever drain the queue.
            lock (_flushGate)
            {
                _flushWaiters.Remove(waiter);
            }

            return Task.CompletedTask;
        }

        return waiter.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _uiTimer.Dispose();
        if (_fileQueue is not null)
        {
            _fileQueue.Writer.TryComplete();
            try
            {
                _fileWriter?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Logging must never crash the app.
            }
        }

        CompleteFlushWaiters();
        GC.SuppressFinalize(this);
    }

    private static void RemoveSource(Queue<LogEntry> queue, string source)
    {
        var kept = queue.Where(e => e.Source != source).ToArray();
        queue.Clear();
        foreach (var entry in kept)
        {
            queue.Enqueue(entry);
        }
    }

    private void FlushPending()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        LogEntry[]? batch = null;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            if (EntriesAdded is not null)
            {
                batch = _pending.ToArray();
            }

            _pending.Clear();
        }

        if (batch is null)
        {
            return;
        }

        try
        {
            EntriesAdded?.Invoke(this, batch);
        }
        catch (Exception)
        {
            // A faulty subscriber must not take the process down from a timer callback.
        }
    }

    private void CompleteFlushWaiters()
    {
        TaskCompletionSource[] waiters;
        lock (_flushGate)
        {
            if (_flushWaiters.Count == 0)
            {
                return;
            }

            waiters = [.. _flushWaiters];
            _flushWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }

    private async Task WriteFileAsync(ChannelReader<LogEntry?> reader)
    {
        FileStream? stream = null;
        long size = 0;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var entry))
                {
                    if (entry is null)
                    {
                        if (stream is not null)
                        {
                            await stream.FlushAsync().ConfigureAwait(false);
                        }

                        CompleteFlushWaiters();
                        continue;
                    }

                    if (stream is null)
                    {
                        stream = OpenFile(out size);
                        if (stream is null)
                        {
                            continue;
                        }
                    }

                    var bytes = Encoding.UTF8.GetBytes(entry + Environment.NewLine);
                    if (size + bytes.Length > MaxFileBytes)
                    {
                        await stream.FlushAsync().ConfigureAwait(false);
                        await stream.DisposeAsync().ConfigureAwait(false);
                        stream = null;
                        var rotated = Rotate();
                        stream = OpenFile(out size);
                        if (stream is null)
                        {
                            continue;
                        }

                        if (!rotated)
                        {
                            // Another process holds the file: keep appending and retry after another MaxFileBytes.
                            size = 0;
                        }
                    }

                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                    size += bytes.Length;
                }

                if (stream is not null)
                {
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // Logging must never crash the app.
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            _fileQueue?.Writer.TryComplete();
            CompleteFlushWaiters();
        }
    }

    private FileStream? OpenFile(out long size)
    {
        size = 0;
        try
        {
            var stream = new FileStream(_filePath!, FileMode.Append, FileAccess.Write,
                // Readers (diagnostics, an editor, tests) may open the live file; rotation may rename it.
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            size = stream.Length;
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool Rotate()
    {
        try
        {
            File.Move(_filePath!, _filePath + ".1", overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
