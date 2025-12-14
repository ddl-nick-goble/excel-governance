using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DominoGovernanceTracker.Core;
using DominoGovernanceTracker.Models;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Serilog;

namespace DominoGovernanceTracker.Publishing
{
    /// <summary>
    /// Publishes audit events to REST API with retry and circuit breaker patterns
    /// Runs on a background thread to avoid blocking Excel
    /// </summary>
    public class HttpEventPublisher : IDisposable
    {
        private readonly DgtConfig _config;
        private readonly EventQueue _queue;
        private readonly LocalBuffer _buffer;
        private readonly HttpClient _httpClient;
        private readonly ResiliencePipeline _resiliencePipeline;
        private readonly Timer _flushTimer;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private int _isRunning; // 0 = false, 1 = true (thread-safe with Interlocked)
        private long _totalEventsSent;
        private long _totalEventsFailed;
        private int _circuitBreakerIsOpen; // 0 = false, 1 = true (thread-safe with Interlocked - Polly callbacks run on background threads)
        private int _isFlushingEvents = 0; // 0 = not flushing, 1 = flushing

        public HttpEventPublisher(DgtConfig config, EventQueue queue)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _cancellationTokenSource = new CancellationTokenSource();

            // Initialize HTTP client
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.HttpTimeoutSeconds)
            };

            // Add API key header if configured
            if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-API-Key", _config.ApiKey);
            }

            // Initialize local buffer
            _buffer = new LocalBuffer(_config.GetLocalBufferPath(), _config.MaxBufferFileSizeMB);

            // Build resilience pipeline with Polly
            _resiliencePipeline = BuildResiliencePipeline();

            // Set up periodic flush timer
            _flushTimer = new Timer(
                OnFlushTimerElapsed,
                null,
                TimeSpan.FromSeconds(_config.FlushIntervalSeconds),
                TimeSpan.FromSeconds(_config.FlushIntervalSeconds));

            Log.Information("HTTP Event Publisher initialized (API: {Endpoint})", _config.ApiEndpoint);
        }

        /// <summary>
        /// Starts the publisher background processing
        /// </summary>
        public void Start()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
                return; // Already running

            // Try to send any buffered events first
            Task.Run(() => ProcessBufferedEvents(), _cancellationTokenSource.Token);

            Log.Information("HTTP Event Publisher started");
        }

        /// <summary>
        /// Stops the publisher and flushes remaining events
        /// </summary>
        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 0, 1) != 1)
                return; // Already stopped

            _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            // Final flush
            FlushEvents().ConfigureAwait(false).GetAwaiter().GetResult();

            Log.Information("HTTP Event Publisher stopped");
        }

        /// <summary>
        /// Builds Polly resilience pipeline with retry and circuit breaker
        /// </summary>
        private ResiliencePipeline BuildResiliencePipeline()
        {
            return new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = _config.MaxRetryAttempts,
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,  // Add jitter to prevent thundering herd
                    OnRetry = args =>
                    {
                        Log.Warning("Retry attempt {Attempt} after {Delay}ms due to: {Exception}",
                            args.AttemptNumber, args.RetryDelay.TotalMilliseconds, args.Outcome.Exception?.Message);
                        return default;
                    }
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(60),
                    OnOpened = args =>
                    {
                        Interlocked.Exchange(ref _circuitBreakerIsOpen, 1);
                        Log.Error("Circuit breaker opened - API appears to be unavailable");
                        return default;
                    },
                    OnClosed = args =>
                    {
                        Interlocked.Exchange(ref _circuitBreakerIsOpen, 0);
                        Log.Information("Circuit breaker closed - API is available again");
                        return default;
                    },
                    OnHalfOpened = args =>
                    {
                        Log.Information("Circuit breaker half-open - testing API availability");
                        return default;
                    }
                })
                .Build();
        }

        /// <summary>
        /// Timer callback to flush events periodically
        /// </summary>
        private void OnFlushTimerElapsed(object state)
        {
            if (Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1)
            {
                Task.Run(() => FlushEvents(), _cancellationTokenSource.Token);
            }
        }

        /// <summary>
        /// Flushes events from queue to API
        /// </summary>
        private async Task FlushEvents()
        {
            // Prevent concurrent flush operations (timer can fire while previous flush is still running)
            if (Interlocked.CompareExchange(ref _isFlushingEvents, 1, 0) != 0)
            {
                Log.Debug("Skipping flush - another flush is in progress");
                return;
            }

            try
            {
                if (_queue.IsEmpty)
                    return;

                // Dequeue a batch of events
                var batch = _queue.DequeueBatch(_config.MaxBufferSize);
                if (batch.Count == 0)
                    return;

                Log.Debug("Flushing {Count} events to API", batch.Count);

                // Try to send to API with resilience
                var success = await SendEventsToApi(batch);

                if (success)
                {
                    Interlocked.Add(ref _totalEventsSent, batch.Count);
                    Log.Information("Successfully sent {Count} events to API", batch.Count);
                }
                else
                {
                    // Failed to send - buffer locally
                    _buffer.AppendEvents(batch);
                    Interlocked.Add(ref _totalEventsFailed, batch.Count);
                    Log.Warning("Failed to send {Count} events - buffered locally", batch.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error flushing events");
            }
            finally
            {
                // Release the flush lock
                Interlocked.Exchange(ref _isFlushingEvents, 0);
            }
        }

        /// <summary>
        /// Processes any events in the local buffer (including rotated files)
        /// </summary>
        private async Task ProcessBufferedEvents()
        {
            try
            {
                // Process main buffer file
                if (_buffer.HasBufferedEvents())
                {
                    Log.Information("Processing buffered events from main buffer file");

                    var bufferedEvents = _buffer.ReadEvents();
                    if (bufferedEvents.Count > 0)
                    {
                        Log.Information("Found {Count} buffered events in main file", bufferedEvents.Count);

                        // Try to send buffered events
                        var success = await SendEventsToApi(bufferedEvents);

                        if (success)
                        {
                            // Clear buffer on success
                            _buffer.ClearBuffer();
                            Interlocked.Add(ref _totalEventsSent, bufferedEvents.Count);
                            Log.Information("Successfully sent {Count} buffered events", bufferedEvents.Count);
                        }
                        else
                        {
                            Log.Warning("Failed to send buffered events - will retry later");
                        }
                    }
                }

                // Process rotated buffer files
                var rotatedFiles = _buffer.GetRotatedBufferFiles();
                if (rotatedFiles.Count > 0)
                {
                    Log.Information("Found {Count} rotated buffer files to process", rotatedFiles.Count);

                    foreach (var file in rotatedFiles)
                    {
                        try
                        {
                            var events = _buffer.ReadEventsFromFile(file);
                            if (events.Count == 0)
                            {
                                // Empty file, just delete it
                                _buffer.DeleteFile(file);
                                continue;
                            }

                            Log.Information("Processing {Count} events from rotated file: {File}",
                                events.Count, Path.GetFileName(file));

                            // Try to send events
                            var success = await SendEventsToApi(events);

                            if (success)
                            {
                                // Delete rotated file on success
                                _buffer.DeleteFile(file);
                                Interlocked.Add(ref _totalEventsSent, events.Count);
                                Log.Information("Successfully sent {Count} events from rotated file",
                                    events.Count);
                            }
                            else
                            {
                                Log.Warning("Failed to send events from rotated file - will retry later");
                                // Keep the file for next retry
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error processing rotated file: {File}", file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing buffered events");
            }
        }

        /// <summary>
        /// Sends events to the REST API with resilience
        /// </summary>
        private async Task<bool> SendEventsToApi(List<AuditEvent> events)
        {
            if (events == null || events.Count == 0)
                return true;

            try
            {
                // Execute with resilience pipeline
                var result = await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    // Serialize events to JSON
                    var json = JsonSerializer.Serialize(events, new JsonSerializerOptions
                    {
                        WriteIndented = false
                    });

                    using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                    using (var response = await _httpClient.PostAsync(_config.ApiEndpoint, content, ct))
                    {
                        // Throw if not successful (triggers retry)
                        response.EnsureSuccessStatusCode();
                    }

                    return true;
                }, _cancellationTokenSource.Token);

                return result;
            }
            catch (BrokenCircuitException)
            {
                Log.Warning("Circuit breaker is open - skipping API call");
                return false;
            }
            catch (HttpRequestException hex)
            {
                Log.Error(hex, "HTTP error sending events to API");
                return false;
            }
            catch (TaskCanceledException)
            {
                Log.Warning("Request to API timed out");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error sending events to API");
                return false;
            }
        }

        /// <summary>
        /// Manually triggers a retry attempt (useful when API is detected as healthy)
        /// </summary>
        public void TriggerRetry()
        {
            Log.Information("Manual retry triggered - attempting to flush events");
            Task.Run(() => FlushEvents(), _cancellationTokenSource.Token);

            // Also try to process buffered events
            if (_buffer.HasBufferedEvents())
            {
                Task.Run(() => ProcessBufferedEvents(), _cancellationTokenSource.Token);
            }
        }

        /// <summary>
        /// Buffers a single event directly to disk (for queue overflow handling)
        /// </summary>
        public void BufferEventDirectly(AuditEvent evt)
        {
            if (evt == null) return;

            _buffer.AppendEvents(new List<AuditEvent> { evt });
        }

        /// <summary>
        /// Gets whether circuit breaker is currently open (thread-safe)
        /// </summary>
        public bool IsCircuitBreakerOpen => Interlocked.CompareExchange(ref _circuitBreakerIsOpen, 0, 0) == 1;

        /// <summary>
        /// Gets publisher statistics
        /// </summary>
        public PublisherStats GetStats()
        {
            return new PublisherStats
            {
                IsRunning = Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1,
                TotalEventsSent = Interlocked.Read(ref _totalEventsSent),
                TotalEventsFailed = Interlocked.Read(ref _totalEventsFailed),
                QueueSize = _queue.Count,
                BufferSize = _buffer.GetBufferSize(),
                HasBufferedEvents = _buffer.HasBufferedEvents(),
                CircuitBreakerIsOpen = Interlocked.CompareExchange(ref _circuitBreakerIsOpen, 0, 0) == 1
            };
        }

        public void Dispose()
        {
            Stop();
            _flushTimer?.Dispose();
            _httpClient?.Dispose();
            _buffer?.Dispose();
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }
    }

    /// <summary>
    /// Publisher statistics
    /// </summary>
    public class PublisherStats
    {
        public bool IsRunning { get; set; }
        public long TotalEventsSent { get; set; }
        public long TotalEventsFailed { get; set; }
        public int QueueSize { get; set; }
        public long BufferSize { get; set; }
        public bool HasBufferedEvents { get; set; }
        public bool CircuitBreakerIsOpen { get; set; }
    }
}
