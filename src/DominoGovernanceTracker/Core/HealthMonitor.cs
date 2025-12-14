using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace DominoGovernanceTracker.Core
{
    /// <summary>
    /// Monitors API health and provides recovery detection
    /// Helps with self-healing by detecting when API becomes available again
    /// </summary>
    public class HealthMonitor : IDisposable
    {
        private readonly string _healthCheckUrl;
        private readonly HttpClient _httpClient;
        private readonly Timer _healthCheckTimer;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private int _isRunning; // 0 = false, 1 = true (thread-safe with Interlocked)
        private int _lastHealthStatus; // 0 = false/unhealthy, 1 = true/healthy (thread-safe with Interlocked)
        private long _lastSuccessfulCheckUtc; // DateTime.Ticks for thread-safe reads/writes
        private long _lastFailedCheckUtc; // DateTime.Ticks for thread-safe reads/writes
        private long _consecutiveFailures;
        private long _consecutiveSuccesses;

        public event EventHandler<bool> HealthStatusChanged;

        public HealthMonitor(string apiEndpoint, int checkIntervalSeconds = 30)
        {
            _healthCheckUrl = GetHealthCheckUrl(apiEndpoint);
            _cancellationTokenSource = new CancellationTokenSource();

            // Separate HTTP client for health checks (shorter timeout)
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            // Set up periodic health check timer
            _healthCheckTimer = new Timer(
                OnHealthCheckTimerElapsed,
                null,
                TimeSpan.FromSeconds(checkIntervalSeconds),
                TimeSpan.FromSeconds(checkIntervalSeconds));

            Interlocked.Exchange(ref _lastHealthStatus, 0); // Initialize to unhealthy (0)
            Interlocked.Exchange(ref _lastSuccessfulCheckUtc, DateTime.MinValue.Ticks);
            Interlocked.Exchange(ref _lastFailedCheckUtc, DateTime.MinValue.Ticks);

            Log.Information("Health Monitor initialized (checking: {Url}, interval: {Interval}s)",
                _healthCheckUrl, checkIntervalSeconds);
        }

        /// <summary>
        /// Converts API endpoint to health check URL
        /// </summary>
        private string GetHealthCheckUrl(string apiEndpoint)
        {
            try
            {
                var uri = new Uri(apiEndpoint);
                // Try /health endpoint, fallback to base URL HEAD request
                return $"{uri.Scheme}://{uri.Authority}/health";
            }
            catch
            {
                return apiEndpoint;
            }
        }

        /// <summary>
        /// Starts health monitoring
        /// </summary>
        public void Start()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
                return; // Already running

            // Do an immediate health check
            Task.Run(() => CheckHealthAsync(), _cancellationTokenSource.Token);

            Log.Information("Health Monitor started");
        }

        /// <summary>
        /// Stops health monitoring
        /// </summary>
        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _isRunning, 0, 1) != 1)
                return; // Already stopped

            _healthCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            Log.Information("Health Monitor stopped");
        }

        /// <summary>
        /// Timer callback for periodic health checks
        /// </summary>
        private void OnHealthCheckTimerElapsed(object state)
        {
            if (Interlocked.CompareExchange(ref _isRunning, 0, 0) == 1)
            {
                Task.Run(() => CheckHealthAsync(), _cancellationTokenSource.Token);
            }
        }

        /// <summary>
        /// Performs a health check against the API
        /// </summary>
        private async Task CheckHealthAsync()
        {
            try
            {
                bool isHealthy = await PerformHealthCheckRequest();

                // Update statistics (thread-safe)
                if (isHealthy)
                {
                    Interlocked.Exchange(ref _lastSuccessfulCheckUtc, DateTime.UtcNow.Ticks);
                    Interlocked.Increment(ref _consecutiveSuccesses);
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                }
                else
                {
                    Interlocked.Exchange(ref _lastFailedCheckUtc, DateTime.UtcNow.Ticks);
                    Interlocked.Increment(ref _consecutiveFailures);
                    Interlocked.Exchange(ref _consecutiveSuccesses, 0);
                }

                // Detect status change (thread-safe)
                int newStatus = isHealthy ? 1 : 0;
                int oldStatus = Interlocked.Exchange(ref _lastHealthStatus, newStatus);

                if (newStatus != oldStatus)
                {
                    if (isHealthy)
                    {
                        Log.Information("API health check: API is now HEALTHY (recovered)");
                    }
                    else
                    {
                        Log.Warning("API health check: API is now UNHEALTHY");
                    }

                    // Raise event for listeners
                    OnHealthStatusChanged(isHealthy);
                }
                else
                {
                    Log.Debug("API health check: Status unchanged ({Status})",
                        isHealthy ? "Healthy" : "Unhealthy");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during health check");
            }
        }

        /// <summary>
        /// Performs the actual HTTP health check
        /// </summary>
        private async Task<bool> PerformHealthCheckRequest()
        {
            try
            {
                // Try GET /health first
                using (var response = await _httpClient.GetAsync(_healthCheckUrl, _cancellationTokenSource.Token))
                {
                    // Any 2xx response is considered healthy
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }

                    // 404 on /health? Try HEAD request to base URL instead
                    if ((int)response.StatusCode == 404)
                    {
                        var baseUrl = _healthCheckUrl.Replace("/health", "/api/events");
                        using (var headRequest = new HttpRequestMessage(HttpMethod.Head, baseUrl))
                        using (var headResponse = await _httpClient.SendAsync(headRequest, _cancellationTokenSource.Token))
                        {
                            return headResponse.IsSuccessStatusCode;
                        }
                    }

                    return false;
                }
            }
            catch (HttpRequestException)
            {
                // Connection refused, timeout, etc. = unhealthy
                return false;
            }
            catch (TaskCanceledException)
            {
                // Timeout = unhealthy
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Health check request failed");
                return false;
            }
        }

        /// <summary>
        /// Raises the HealthStatusChanged event
        /// </summary>
        protected virtual void OnHealthStatusChanged(bool isHealthy)
        {
            HealthStatusChanged?.Invoke(this, isHealthy);
        }

        /// <summary>
        /// Gets current health status (thread-safe)
        /// </summary>
        public bool IsHealthy => Interlocked.CompareExchange(ref _lastHealthStatus, 0, 0) == 1;

        /// <summary>
        /// Gets time of last successful check
        /// </summary>
        public DateTime LastSuccessfulCheck
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastSuccessfulCheckUtc);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Gets time of last failed check
        /// </summary>
        public DateTime LastFailedCheck
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastFailedCheckUtc);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Gets consecutive failure count
        /// </summary>
        public long ConsecutiveFailures => Interlocked.Read(ref _consecutiveFailures);

        /// <summary>
        /// Gets consecutive success count
        /// </summary>
        public long ConsecutiveSuccesses => Interlocked.Read(ref _consecutiveSuccesses);

        public void Dispose()
        {
            Stop();
            _healthCheckTimer?.Dispose();
            _httpClient?.Dispose();
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }
    }
}
