using System;
using System.IO;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace LASTE_Mate.Services;

/// <summary>
/// Service for managing application logging with file rotation.
/// </summary>
public class LoggingService
{
    private static readonly string LogFileName = "LASTE-Mate.log";
    private static readonly string OldLogFileName = "LASTE-Mate.log.old";
    private static bool _initialized;

    /// <summary>
    /// Initializes the logging system with file rotation.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            // Get the directory where the executable is located
            // For single-file apps, use AppContext.BaseDirectory instead of Assembly.Location
            var logDirectory = AppContext.BaseDirectory;
            
            // Ensure the directory exists
            if (string.IsNullOrEmpty(logDirectory))
            {
                logDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }
            
            // Normalize the path (remove trailing separator if present)
            logDirectory = Path.GetFullPath(logDirectory);

            var logFilePath = Path.Combine(logDirectory, LogFileName);
            var oldLogFilePath = Path.Combine(logDirectory, OldLogFileName);

            // Rotate logs: if log file exists, rename to .old (delete old .old first)
            if (File.Exists(logFilePath))
            {
                // Delete old log file if it exists
                if (File.Exists(oldLogFilePath))
                {
                    try
                    {
                        File.Delete(oldLogFilePath);
                    }
                    catch (Exception ex)
                    {
                        // If we can't delete, try to continue anyway
                        System.Diagnostics.Debug.WriteLine($"Warning: Could not delete old log file: {ex.Message}");
                    }
                }

                // Rename current log to .old
                try
                {
                    File.Move(logFilePath, oldLogFilePath);
                }
                catch (Exception ex)
                {
                    // If we can't rename, try to continue anyway
                    System.Diagnostics.Debug.WriteLine($"Warning: Could not rotate log file: {ex.Message}");
                }
            }

            // Configure NLog
            var config = new LoggingConfiguration();

            // Console target (for IDE/debug output)
            var consoleTarget = new ConsoleTarget("console")
            {
                Layout = "${longdate} [${level:uppercase=true}] ${logger}: ${message} ${exception:format=tostring}"
            };

            // File target
            var fileTarget = new FileTarget("file")
            {
                FileName = logFilePath,
                Layout = "${longdate} [${level:uppercase=true}] ${logger}: ${message} ${exception:format=tostring}",
                ArchiveFileName = oldLogFilePath,
                // We handle rotation manually, so disable automatic archiving
                MaxArchiveFiles = 1,
                KeepFileOpen = false,
                ConcurrentWrites = true
            };

            config.AddTarget(consoleTarget);
            config.AddTarget(fileTarget);
            
            // Add rules for both targets
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, consoleTarget);
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, fileTarget);

            LogManager.Configuration = config;

            _initialized = true;

            var logger = LogManager.GetCurrentClassLogger();
            logger.Info("Logging initialized. Log file: {LogFile}", logFilePath);
        }
        catch (Exception ex)
        {
            // Fallback to console if file logging fails
            System.Diagnostics.Debug.WriteLine($"Failed to initialize logging: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a logger instance for the specified type.
    /// </summary>
    public static ILogger GetLogger<T>()
    {
        if (!_initialized)
        {
            Initialize();
        }
        return LogManager.GetLogger(typeof(T).FullName ?? typeof(T).Name);
    }

    /// <summary>
    /// Gets a logger instance for the specified name.
    /// </summary>
    public static ILogger GetLogger(string name)
    {
        if (!_initialized)
        {
            Initialize();
        }
        return LogManager.GetLogger(name);
    }
}

