using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Pickles_Playlist_Editor.Utils
{
    internal class Logger : IDisposable
    {
        private static readonly string logFilePath = "picklesPlaylistEditor.log";
        private static StreamWriter logFileWriter = new StreamWriter(logFilePath, append: true);
        private static ILoggerFactory? _loggerFactory;
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

            //Write log messages to text file
            _logFileWriter.WriteLine($"[{logLevel}] [{_categoryName}] {message}");
            _logFileWriter.Flush();
        }
    }
}
