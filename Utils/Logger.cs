using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Pickles_Playlist_Editor.Utils
{
    internal class Logger : IDisposable
    {
        // 5 MB keeps a long-running install's log small enough to attach to a bug report
        // while still covering many sessions' worth of history.
        private const long MaxLogBytes = 5 * 1024 * 1024;

        // Declaration order matters: static field initializers run top-to-bottom, and
        // everything below depends on LogDirectory already being set.

        // Store the log next to crash.log in %LOCALAPPDATA%\PicklesPlaylistEditor so it
        // lives in a predictable, per-user, writable location regardless of how the app
        // was launched (Velopack stub, shortcut, direct exe, etc.).
        public static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PicklesPlaylistEditor");

        /// <summary>Full path of the crash log written by the global exception handlers.</summary>
        public static readonly string CrashLogPath = Path.Combine(LogDirectory, "crash.log");

        private static readonly string logFilePath = ResolveLogFilePath();
        private static StreamWriter logFileWriter = CreateLogFileWriter(logFilePath);
        // volatile: read outside _factoryLock in the double-checked init below.
        private static volatile ILoggerFactory? _loggerFactory;
        private static int _handlersInstalled;

        /// <summary>Full path of the rolling application log. Shown to users reporting bugs.</summary>
        public static string LogFilePath => logFilePath;

        private static string ResolveLogFilePath()
        {
            try { Directory.CreateDirectory(LogDirectory); } catch { }
            var path = Path.Combine(LogDirectory, "picklesPlaylistEditor.log");
            RotateIfTooLarge(path);
            return path;
        }

        // Keep exactly one previous generation (.1). Without this the log grew forever,
        // which made it useless to ask a user to send in.
        private static void RotateIfTooLarge(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxLogBytes) return;

                string previous = path + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            catch
            {
                // A locked or unrotatable log must never block startup.
            }
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
        // The playback monitor logs from a thread-pool thread while the UI thread logs
        // too, so the lazy init has to be synchronized. An unsynchronized check-then-act
        // could build two factories; the loser's CustomFileLoggerProvider would still
        // hold — and on Dispose close — the shared static writer, silently killing all
        // further logging.
        private static readonly object _factoryLock = new();

        public static ILogger<T> CreateLogger<T>()
        {
            var factory = _loggerFactory;
            if (factory == null)
            {
                lock (_factoryLock)
                {
                    factory = _loggerFactory ??= LoggerFactory.Create(builder =>
                    {
                        builder
                            .SetMinimumLevel(LogLevel.Debug)
                            .AddProvider(new CustomFileLoggerProvider(logFileWriter));
                    });
                }
            }
            return factory.CreateLogger<T>();
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

        /// <summary>
        /// Records a fatal or near-fatal error to both the rolling log and crash.log.
        /// Appends rather than overwrites, so a repeated crash doesn't erase the first
        /// occurrence — which is usually the informative one.
        /// </summary>
        public static void LogCrash(string source, object? error)
        {
            string detail = error?.ToString() ?? "(no detail)";

            // The rolling log first: it carries the breadcrumbs leading up to the crash,
            // and it is the copy most likely to survive if crash.log can't be written.
            try { LogError("CRASH [{Source}]: {Detail}", source, detail); } catch { }

            try
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfTooLarge(CrashLogPath);
                File.AppendAllText(CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // Nothing useful left to do if even the crash log is unwritable.
            }
        }

        /// <summary>
        /// Installs process-wide exception handlers. Call once, as early as possible.
        /// AppDomain.UnhandledException alone is not enough for a WinUI 3 app: exceptions
        /// thrown on the UI thread (event handlers, dispatcher callbacks) are routed to
        /// Application.UnhandledException instead and would otherwise kill the process
        /// without leaving any trace. See App.OnUnhandledException for that hookup.
        /// </summary>
        public static void InstallGlobalExceptionHandlers()
        {
            if (Interlocked.Exchange(ref _handlersInstalled, 1) == 1) return;

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LogCrash("AppDomain.UnhandledException", e.ExceptionObject);

            // Exceptions inside Task.Run bodies (e.g. the playback monitor) are otherwise
            // swallowed entirely by the runtime's default unobserved-exception policy.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { logFileWriter?.Flush(); } catch { }
            };
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

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
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

            string message;
            try
            {
                message = formatter(state, exception);
            }
            catch (Exception ex)
            {
                // A malformed message template (wrong placeholder count) must not turn a
                // diagnostic into the crash it was meant to diagnose.
                message = $"(log formatting failed: {ex.Message}) {state}";
            }

            if (exception != null)
                message += " | " + exception;

            // Timestamp every line so the sequence of operations before a failure is reconstructable.
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int threadId = Thread.CurrentThread.ManagedThreadId;

            // StreamWriter isn't thread-safe and the playback monitor logs from a
            // background thread while the UI thread logs too — serialize the writes so
            // lines can't interleave into garbage.
            lock (WriteLock)
            {
                try
                {
                    _logFileWriter.WriteLine($"{timestamp} [{logLevel}] [T{threadId}] [{_categoryName}] {message}");
                    _logFileWriter.Flush();
                }
                catch
                {
                    // Logging must never be the thing that takes the app down.
                }
            }
        }

        internal static readonly object WriteLock = new();
    }
}
