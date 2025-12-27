using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Track-scoped forensic logger that writes correlation-based logs.
/// Wraps standard ILogger with automatic CorrelationId prefixing and database persistence.
/// </summary>
public class TrackForensicLogger
{
    private readonly ILogger<TrackForensicLogger> _logger;
    private readonly Channel<Data.Entities.ForensicLogEntry> _logChannel;
    
    public TrackForensicLogger(ILogger<TrackForensicLogger> logger)
    {
        _logger = logger;
        
        // Producer-Consumer channel for async log persistence (non-blocking)
        _logChannel = Channel.CreateUnbounded<Data.Entities.ForensicLogEntry>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });
        
        // Start background consumer
        _ = Task.Run(ConsumerAsync);
    }
    
    /// <summary>
    /// Logs a debug message with correlation context
    /// </summary>
    public void Debug(string correlationId, string stage, string message, object? data = null)
    {
        LogInternal(correlationId, stage, Data.Entities.ForensicLevel.Debug, message, data);
    }
    
    /// <summary>
    /// Logs an info message with correlation context
    /// </summary>
    public void Info(string correlationId, string stage, string message, object? data = null)
    {
        LogInternal(correlationId, stage, Data.Entities.ForensicLevel.Info, message, data);
    }
    
    /// <summary>
    /// Logs a warning with correlation context
    /// </summary>
    public void Warning(string correlationId, string stage, string message, object? data = null)
    {
        LogInternal(correlationId, stage, Data.Entities.ForensicLevel.Warning, message, data);
    }
    
    /// <summary>
    /// Logs an error with correlation context
    /// </summary>
    public void Error(string correlationId, string stage, string message, Exception? ex = null, object? data = null)
    {
        var errorData = data != null ? data : (ex != null ? new { Exception = ex.Message, StackTrace = ex.StackTrace } : null);
        LogInternal(correlationId, stage, Data.Entities.ForensicLevel.Error, message, errorData);
    }
    
    /// <summary>
    /// Logs a timed operation (auto-calculates duration)
    /// </summary>
    public IDisposable TimedOperation(string correlationId, string stage, string operation)
    {
        return new TimedLogScope(this, correlationId, stage, operation);
    }
    
    private void LogInternal(string correlationId, string stage, string level, string message, object? data)
    {
        // Standard console log with correlation prefix
        var enrichedMessage = $"[CID: {correlationId[..8]}] [{stage}] {message}";
        
        switch (level)
        {
            case Data.Entities.ForensicLevel.Debug:
                _logger.LogDebug(enrichedMessage);
                break;
            case Data.Entities.ForensicLevel.Info:
                _logger.LogInformation(enrichedMessage);
                break;
            case Data.Entities.ForensicLevel.Warning:
                _logger.LogWarning(enrichedMessage);
                break;
            case Data.Entities.ForensicLevel.Error:
                _logger.LogError(enrichedMessage);
                break;
        }
        
        // Queue for database persistence (non-blocking)
        var entry = new Data.Entities.ForensicLogEntry
        {
            CorrelationId = correlationId,
            Stage = stage,
            Level = level,
            Message = message,
            Data = data != null ? JsonSerializer.Serialize(data) : null,
            Timestamp = DateTime.UtcNow
        };
        
        _logChannel.Writer.TryWrite(entry);
    }
    
    /// <summary>
    /// Background consumer that persists logs to database
    /// </summary>
    private async Task ConsumerAsync()
    {
        await foreach (var entry in _logChannel.Reader.ReadAllAsync())
        {
            try
            {
                // Use Task.Run to avoid blocking the consumer thread
                await Task.Run(async () =>
                {
                    using var db = new Data.AppDbContext();
                    db.ForensicLogs.Add(entry);
                    await db.SaveChangesAsync();
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist forensic log entry");
            }
        }
    }
    
    /// <summary>
    /// Helper for automatic duration tracking
    /// </summary>
    private class TimedLogScope : IDisposable
    {
        private readonly TrackForensicLogger _logger;
        private readonly string _correlationId;
        private readonly string _stage;
        private readonly string _operation;
        private readonly Stopwatch _stopwatch;
        
        public TimedLogScope(TrackForensicLogger logger, string correlationId, string stage, string operation)
        {
            _logger = logger;
            _correlationId = correlationId;
            _stage = stage;
            _operation = operation;
            _stopwatch = Stopwatch.StartNew();
            
            _logger.Debug(correlationId, stage, $"{operation} started");
        }
        
        public void Dispose()
        {
            _stopwatch.Stop();
            _logger.Info(_correlationId, _stage, $"{_operation} completed in {_stopwatch.ElapsedMilliseconds}ms");
        }
    }
}

/// <summary>
/// Extension methods for creating correlation IDs
/// </summary>
public static class CorrelationIdExtensions
{
    /// <summary>
    /// Generates a new correlation ID (short GUID)
    /// </summary>
    public static string NewCorrelationId()
    {
        return Guid.NewGuid().ToString("N"); // 32 chars, no dashes
    }
}
