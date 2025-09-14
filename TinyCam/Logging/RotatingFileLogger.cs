using System.Collections.Concurrent;
using System.Text;

namespace TinyCam.Logging;

public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, RotatingFileLogger> _loggers = new();
    private readonly FileSink _sink;

    public RotatingFileLoggerProvider(string path, long maxBytes, int maxFiles, bool rollDaily)
    {
        _sink = new FileSink(path, maxBytes, maxFiles, rollDaily);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RotatingFileLogger(name, _sink));

    public void Dispose() => _sink.Dispose();

    private sealed class RotatingFileLogger : ILogger
    {
        private readonly string _name;
        private readonly FileSink _sink;

        public RotatingFileLogger(string name, FileSink sink) { _name = name; _sink = sink; }

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                                Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var sb = new StringBuilder(256);
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
            sb.Append(" [").Append(logLevel).Append("] ");
            sb.Append(_name).Append(" - ").Append(formatter(state, exception));
            if (exception != null) sb.AppendLine().Append(exception);
            _sink.WriteLine(sb.ToString());
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FileSink : IDisposable
    {
        private readonly object _lock = new();
        private readonly string _basePath;
        private readonly long _maxBytes;
        private readonly int _maxFiles;
        private readonly bool _rollDaily;

        private StreamWriter _writer;
        private DateOnly _currentDate;

        public FileSink(string path, long maxBytes, int maxFiles, bool rollDaily)
        {
            _basePath = Path.GetFullPath(path);
            _maxBytes = Math.Max(1024 * 128, maxBytes); // Min 128KB
            _maxFiles = Math.Max(1, maxFiles);
            _rollDaily = rollDaily;

            Directory.CreateDirectory(Path.GetDirectoryName(_basePath)!);
            _currentDate = DateOnly.FromDateTime(DateTime.Now);
            _writer = CreateWriter(_basePath, append: true);
        }

        public void WriteLine(string line)
        {
            lock (_lock)
            {
                if (_rollDaily && DateOnly.FromDateTime(DateTime.Now) != _currentDate)
                {
                    RotateByDay();
                }
                else if (TryGetLength(_writer.BaseStream) > _maxBytes)
                {
                    RotateBySize();
                }

                _writer.WriteLine(line);
                _writer.Flush();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            }
        }

        private void RotateByDay()
        {
            try { _writer.Flush(); } catch { }
            try { _writer.Dispose(); } catch { }
            _currentDate = DateOnly.FromDateTime(DateTime.Now);

            // 날짜별 파일명: tinycam_YYYY-MM-DD.log (원본이 .log면 확장자 앞에 날짜)
            var ext = Path.GetExtension(_basePath);
            var stem = Path.ChangeExtension(_basePath, null);
            var dated = $"{stem}_{_currentDate:yyyy-MM-dd}{ext}";
            _writer = CreateWriter(dated, append: true);
        }

        private void RotateBySize()
        {
            try { _writer.Flush(); } catch { }
            try { _writer.Dispose(); } catch { }

            // 뒤에서 앞으로 밀기: .(N-1) → .N
            for (int i = _maxFiles - 1; i >= 1; i--)
            {
                var src = $"{_basePath}.{i}";
                var dst = $"{_basePath}.{i + 1}";
                if (File.Exists(dst)) { try { File.Delete(dst); } catch { } }
                if (File.Exists(src)) { try { File.Move(src, dst); } catch { } }
            }

            // base → .1
            var first = $"{_basePath}.1";
            if (File.Exists(first)) { try { File.Delete(first); } catch { } }
            if (File.Exists(_basePath)) { try { File.Move(_basePath, first); } catch { } }

            _writer = CreateWriter(_basePath, append: false);
        }

        private static long TryGetLength(Stream s) { try { return s.Length; } catch { return 0; } }

        private static StreamWriter CreateWriter(string path, bool append)
        {
            var fs = new FileStream(path,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read); // 읽기 공유 허용
            return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }
}
