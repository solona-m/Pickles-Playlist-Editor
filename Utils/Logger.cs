using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Pickles_Playlist_Editor.Utils
{
    internal class Logger : IDisposable
    {
        private static readonly string logFilePath = ResolveLogFilePath();
        private static StreamWriter logFileWriter = CreateLogFileWriter(logFilePath);
        private static ILoggerFactory? _loggerFactory;

        // Store the log next to crash.log in %LOCALAPPDATA%\PicklesPlaylistEditor so it
        // lives in a predictable, per-user, writable location regardless of how the app
        // was launched (Velopack stub, shortcut, direct exe, etc.).
        private static string ResolveLogFilePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PicklesPlaylistEditor");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "picklesPlaylistEditor.log");
        }

        // Never let a locked or unwritable log file take down app startup. Fall back to a
        // no-op writer (Stream.Null) so logging silently degrades instead of crashing.
        private static StreamWriter CreateLogFileWriter(string path)
        {
            try
            {
                return new StreamWriter(path, append: true) { AutoFlush = true };
            }
            catch
            {
                return StreamWriter.Null;
            }
        }
        public static ILogger<T> CreateLogger<T>()
        {
            if (_loggerFactory == null)
            {
                _loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder
                        .SetMinimumLevel(LogLevel.Debug)
                        .AddProvider(new CustomFileLoggerProvider(logFileWriter));
                });
            }
            return _loggerFactory.CreateLogger<T>();
        }

        public static void LogInfo(string message, params object[] args)
        {
            var logger = CreateLogger<Logger>();
            logger.LogInformation(message, args);
        }

        public static void LogWarn(string message, params object[] args)
        {
            var logger = CreateLogger<Logger>();
            logger.LogWarning(message, args);
        }

        public static void LogError(string message, params object[] args)
        {
            var logger = CreateLogger<Logger>();
            logger.LogError(message, args);
        }

        public void Dispose()
        {
            logFileWriter?.Dispose();
        }
    }
    public class CustomFileLoggerProvider : ILoggerProvider
    {
        private readonly StreamWriter _logFileWriter;

        public CustomFileLoggerProvider(StreamWriter logFileWriter)
        {
            _logFileWriter = logFileWriter ?? throw new ArgumentNullException(nameof(logFileWriter));
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new CustomFileLogger(categoryName, _logFileWriter);
        }

        public void Dispose()
        {
            _logFileWriter.Dispose();
        }
    }
    public class CustomFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly StreamWriter _logFileWriter;

        public CustomFileLogger(string categoryName, StreamWriter logFileWriter)
        {
            _categoryName = categoryName;
            _logFileWriter = logFileWriter;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            // Ensure that only information level and higher logs are recorded
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            // Ensure that only information level and higher logs are recorded
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Get the formatted log message
            var message = formatter(state, exception);
            if (exception != null)
                message += " | " + exception;

            // Timestamp every line so the sequence of operations before a failure is reconstructable.
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _logFileWriter.WriteLine($"{timestamp} [{logLevel}] [{_categoryName}] {message}");
            _logFileWriter.Flush();
        }
    }
}
