using System.Drawing;
using System.Text;
using Reloaded.Mod.Interfaces;

namespace ff16.qol.cutscenefastforward.Diagnostics;

internal sealed class DebugLogger : IDisposable
{
    private const string Prefix = "[CutsceneFastForward]";

    private readonly ILogger? _console;
    private readonly object _sync = new();

    private StreamWriter? _writer;
    private string _logFilePath = string.Empty;
    private bool _debugEnabled;
    private bool _fileEnabled = true;
    private bool _initializedFile;
    private bool _disposed;

    public string LogFilePath => _logFilePath;
    public bool IsDebugEnabled => _debugEnabled;

    public DebugLogger(ILogger? console, string logDirectory, bool debugEnabled, bool fileEnabled)
    {
        _console = console;
        _debugEnabled = debugEnabled;
        _fileEnabled = fileEnabled;

        if (!string.IsNullOrEmpty(logDirectory))
            _logFilePath = Path.Combine(logDirectory, "debug.log");

        if (_debugEnabled && _fileEnabled)
            OpenLogFile(rotateExisting: true);
    }

    public void UpdateSettings(bool debugEnabled, bool fileEnabled)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            bool wasDebug = _debugEnabled;
            bool wasFile = _fileEnabled;

            _debugEnabled = debugEnabled;
            _fileEnabled = fileEnabled;

            if (_debugEnabled && _fileEnabled)
            {
                if (_writer == null)
                    OpenLogFile(rotateExisting: !_initializedFile);
            }
            else if (_writer != null)
            {
                WriteFileLineLocked("INFO", $"Closing debug log file (DebugLogging={_debugEnabled}, DebugLogToFile={_fileEnabled})");
                CloseLogFileLocked();
            }

            if (wasDebug != _debugEnabled || wasFile != _fileEnabled)
            {
                string msg = $"Debug settings updated: DebugLogging={_debugEnabled}, DebugLogToFile={_fileEnabled}" +
                             (_writer != null ? $", File='{_logFilePath}'" : "");
                _console?.WriteLine($"{Prefix} {msg}", Color.LightSkyBlue);
                WriteFileLineLocked("INFO", msg);
            }
        }
    }

    public void Info(string message)
    {
        lock (_sync)
        {
            _console?.WriteLine($"{Prefix} {message}", Color.LightSkyBlue);
            WriteFileLineLocked("INFO", message);
        }
    }

    public void Debug(string message)
    {
        if (!_debugEnabled)
            return;

        lock (_sync)
        {
            if (!_debugEnabled)
                return;

            _console?.WriteLine($"{Prefix} [DBG] {message}", Color.LightGray);
            WriteFileLineLocked("DBG ", message);
        }
    }

    public void DebugFileOnly(string message)
    {
        if (!_debugEnabled || !_fileEnabled)
            return;

        lock (_sync)
        {
            if (!_debugEnabled || !_fileEnabled)
                return;

            WriteFileLineLocked("DBG ", message);
        }
    }

    public void Warn(string message)
    {
        lock (_sync)
        {
            _console?.WriteLine($"{Prefix} [WARN] {message}", Color.Gold);
            WriteFileLineLocked("WARN", message);
        }
    }

    public void Error(string message, Exception? ex = null)
    {
        lock (_sync)
        {
            string full = ex == null ? message : $"{message} :: {ex}";
            _console?.WriteLine($"{Prefix} [ERROR] {full}", Color.OrangeRed);
            WriteFileLineLocked("ERR ", full);
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // Ignore flush errors during shutdown.
            }
        }
    }

    private void OpenLogFile(bool rotateExisting)
    {
        if (string.IsNullOrEmpty(_logFilePath))
            return;

        try
        {
            string? dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (rotateExisting && File.Exists(_logFilePath))
            {
                string prevPath = Path.Combine(dir!, "debug.prev.log");
                try
                {
                    File.Copy(_logFilePath, prevPath, overwrite: true);
                }
                catch
                {
                    // Non-fatal if previous log is locked.
                }
            }

            FileMode mode = rotateExisting ? FileMode.Create : FileMode.Append;
            var stream = new FileStream(_logFilePath, mode, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
            _initializedFile = true;

            WriteFileLineLocked("INFO", $"=== Session log opened at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
        }
        catch (Exception ex)
        {
            _console?.WriteLine($"{Prefix} [WARN] Could not open debug log file '{_logFilePath}': {ex.Message}", Color.Gold);
            _writer = null;
        }
    }

    private void WriteFileLineLocked(string level, string message)
    {
        if (_writer == null)
            return;

        try
        {
            _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId:D2}] {message}");
        }
        catch
        {
            // Never throw from logging.
        }
    }

    private void CloseLogFileLocked()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
            // Ignore close errors.
        }
        finally
        {
            _writer = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            WriteFileLineLocked("INFO", "=== Session log closed ===");
            CloseLogFileLocked();
        }
    }
}
