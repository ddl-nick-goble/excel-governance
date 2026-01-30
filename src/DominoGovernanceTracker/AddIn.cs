using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using DominoGovernanceTracker.Config;
using DominoGovernanceTracker.Core;
using DominoGovernanceTracker.Models;
using DominoGovernanceTracker.Publishing;
using DominoGovernanceTracker.Services;
using DominoGovernanceTracker.UI;
using ExcelDna.Integration;
using Serilog;
using MSExcel = Microsoft.Office.Interop.Excel;

namespace DominoGovernanceTracker
{
    /// <summary>
    /// Main entry point for DominoGovernanceTracker Excel Add-in
    /// Implements capture-and-forward architecture for compliance tracking
    /// </summary>
    public class AddIn : IExcelAddIn
    {
        private static AddIn _instance;
        private static MSExcel.Application _excelApp;
        private static EventManager _eventManager;
        private static EventQueue _eventQueue;
        private static HttpEventPublisher _publisher;
        private static DgtConfig _config;
        private static HealthMonitor _healthMonitor;
        private static SystemEventMonitor _systemEventMonitor;
        private static ModelRegistrationService _modelService;

        public static AddIn Instance => _instance;

        /// <summary>
        /// Called when Excel loads the add-in
        /// </summary>
        public void AutoOpen()
        {
            _instance = this;

            // Initialize logging first
            InitializeLogging();

            Log.Information("=== DominoGovernanceTracker Add-in Starting ===");
            Log.Information("Version: 1.0.0");
            Log.Information("Architecture: Capture & Forward");

            // Load configuration
            _config = ConfigManager.LoadConfig();
            Log.Information("Configuration loaded (API: {Endpoint})", _config.ApiEndpoint);

            // IMPORTANT: Delay COM access to avoid "Book2" phantom workbook problem
            // Queue initialization to run after Excel is fully ready
            ExcelAsyncUtil.QueueAsMacro(InitializeTracking);

            Log.Information("DGT Add-in AutoOpen completed - initialization queued");
        }

        /// <summary>
        /// Called when Excel unloads the add-in
        /// </summary>
        public void AutoClose()
        {
            Log.Information("=== DominoGovernanceTracker Add-in Shutting Down ===");

            try
            {
                // Stop self-healing monitors
                _healthMonitor?.Stop();
                _healthMonitor?.Dispose();

                _systemEventMonitor?.Stop();
                _systemEventMonitor?.Dispose();

                // Stop tracking first
                _eventManager?.StopTracking();
                _eventManager?.Dispose();

                // Stop publisher (this will flush remaining events)
                _publisher?.Stop();
                _publisher?.Dispose();

                // Dispose queue
                _eventQueue?.Dispose();

                // Dispose model registration service
                _modelService?.Dispose();

                // Dispose ribbon UI
                DgtRibbon.Instance?.Dispose();

                Log.Information("DGT Add-in shutdown complete");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during add-in shutdown");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        // === SELF-HEALING EVENT HANDLERS ===

        /// <summary>
        /// Called when API health status changes
        /// </summary>
        private void OnApiHealthStatusChanged(object sender, bool isHealthy)
        {
            try
            {
                if (isHealthy)
                {
                    Log.Information("=== SELF-HEALING: API is healthy - triggering recovery ===");

                    // Trigger retry on publisher (will attempt to send buffered events)
                    _publisher?.TriggerRetry();

                    // Invalidate ribbon to update UI
                    DgtRibbon.Instance?.InvalidateRibbon();
                }
                else
                {
                    Log.Warning("API health degraded - buffering events locally");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling API health status change");
            }
        }

        /// <summary>
        /// Called when system resumes from sleep/hibernate
        /// </summary>
        private void OnSystemResumed(object sender, EventArgs e)
        {
            try
            {
                Log.Information("=== SELF-HEALING: System resumed from sleep - recovering components ===");

                // Recover ribbon update timer
                DgtRibbon.Instance?.RecoverUpdateTimer();

                // Trigger immediate health check
                Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(2000); // Wait 2 seconds for network to stabilize
                    _healthMonitor?.Start(); // Restart triggers immediate check
                });

                Log.Information("Component recovery complete");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling system resume");
            }
        }

        /// <summary>
        /// Initializes logging subsystem
        /// </summary>
        private void InitializeLogging()
        {
            try
            {
                var logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DominoGovernanceTracker", "logs");

                if (!Directory.Exists(logDirectory))
                    Directory.CreateDirectory(logDirectory);

                var logPath = Path.Combine(logDirectory, "dgt-.log");

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();

                Log.Information("Logging initialized at {Path}", logPath);
            }
            catch (Exception ex)
            {
                // Logging failed - but we can't log this!
                MessageBox.Show(
                    $"Failed to initialize logging: {ex.Message}",
                    "DGT Initialization Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Initializes tracking system (runs after Excel is ready)
        /// </summary>
        private void InitializeTracking()
        {
            try
            {
                Log.Information("Initializing tracking system...");

                // Get Excel Application object (safe after QueueAsMacro)
                _excelApp = (MSExcel.Application)ExcelDnaUtil.Application;
                Log.Debug("Excel Application object obtained");

                // Initialize event queue
                _eventQueue = new EventQueue(_config.MaxBufferSize);
                Log.Debug("Event queue initialized (max size: {MaxSize})", _config.MaxBufferSize);

                // Initialize HTTP publisher
                _publisher = new HttpEventPublisher(_config, _eventQueue);

                // Set overflow handler to buffer events to disk instead of dropping
                _eventQueue.SetOverflowHandler(evt =>
                {
                    try
                    {
                        // Check if publisher is still available (could be disposed during shutdown)
                        var publisher = _publisher;
                        if (publisher != null)
                        {
                            publisher.BufferEventDirectly(evt);
                            Log.Information("Overflowed event buffered to disk: {EventType}", evt.EventType);
                        }
                        else
                        {
                            Log.Warning("Cannot buffer overflowed event - publisher is disposed");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to buffer overflowed event");
                    }
                });

                _publisher.Start();
                Log.Debug("HTTP publisher initialized and started");

                // Initialize model registration service
                _modelService = new ModelRegistrationService(_config);
                Log.Debug("Model registration service initialized");

                // Initialize event manager
                _eventManager = new EventManager(_excelApp, _eventQueue, _config, _modelService);
                _eventManager.StartTracking();
                Log.Information("Event tracking started");

                // === SELF-HEALING COMPONENTS ===

                // Initialize health monitor
                _healthMonitor = new HealthMonitor(_config.ApiEndpoint, checkIntervalSeconds: 30);
                _healthMonitor.HealthStatusChanged += OnApiHealthStatusChanged;
                _healthMonitor.Start();
                Log.Information("Health monitor started");

                // Initialize system event monitor (sleep/wake detection)
                _systemEventMonitor = new SystemEventMonitor();
                _systemEventMonitor.SystemResumed += OnSystemResumed;
                _systemEventMonitor.Start();
                Log.Information("System event monitor started");

                Log.Information("=== DGT Tracking System Initialized Successfully ===");
                Log.Information("Mode: {Mode}", _config.TrackingEnabled ? "Active" : "Disabled");
                Log.Information("API Endpoint: {Endpoint}", _config.ApiEndpoint);
                Log.Information("Flush Interval: {Seconds}s", _config.FlushIntervalSeconds);
                Log.Information("Self-healing: ENABLED");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize tracking system");

                // Show error to user
                MessageBox.Show(
                    $"DGT failed to initialize:\n\n{ex.Message}\n\nTracking will not be active.",
                    "DGT Initialization Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        // === PUBLIC API FOR RIBBON ===

        /// <summary>
        /// Gets the HTTP event publisher (for buffer management UI)
        /// </summary>
        public HttpEventPublisher Publisher => _publisher;

        /// <summary>
        /// Gets the model registration service (for ribbon UI)
        /// </summary>
        public ModelRegistrationService ModelService => _modelService;

        /// <summary>
        /// Gets the event queue (for emitting events from UI)
        /// </summary>
        public EventQueue EventQueue => _eventQueue;

        /// <summary>
        /// Gets whether tracking is currently active
        /// </summary>
        public bool IsTracking()
        {
            return _eventManager?.IsTracking ?? false;
        }

        /// <summary>
        /// Gets the event count for the current workbook
        /// </summary>
        public int GetCurrentWorkbookEventCount()
        {
            return _eventManager?.GetCurrentWorkbookEventCount() ?? 0;
        }

        /// <summary>
        /// Gets publisher statistics
        /// </summary>
        public PublisherStats GetPublisherStats()
        {
            return _publisher?.GetStats() ?? new PublisherStats();
        }

        /// <summary>
        /// Gets queue statistics
        /// </summary>
        public QueueStats GetQueueStats()
        {
            return _eventQueue?.GetStats() ?? new QueueStats();
        }

        /// <summary>
        /// Gets the current health status of the tracking system
        /// </summary>
        public TrackingHealthStatus GetHealthStatus()
        {
            // If tracking is not enabled, return Inactive
            if (!IsTracking())
                return TrackingHealthStatus.Inactive;

            // Use live health monitor status — not buffered event count,
            // since stale buffer entries from a past outage don't mean the API is down now
            if (_healthMonitor != null && !_healthMonitor.IsHealthy)
                return TrackingHealthStatus.Degraded;

            // Circuit breaker open means active send failures
            var stats = GetPublisherStats();
            if (stats.CircuitBreakerIsOpen)
                return TrackingHealthStatus.Degraded;

            return TrackingHealthStatus.Healthy;
        }
    }

    /// <summary>
    /// Health status of the tracking system
    /// </summary>
    public enum TrackingHealthStatus
    {
        /// <summary>Tracking is disabled</summary>
        Inactive,
        /// <summary>Tracking is active and working normally</summary>
        Healthy,
        /// <summary>Tracking is active but experiencing issues (API down, buffering events)</summary>
        Degraded
    }

    // === OPTIONAL: Excel UDFs for debugging ===

    /// <summary>
    /// Excel User Defined Functions for DGT (optional - for debugging/testing)
    /// </summary>
    public static class DgtFunctions
    {
        [ExcelFunction(Description = "Returns DGT tracking status")]
        public static object DGT_STATUS()
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn == null)
                    return "Not initialized";

                return addIn.IsTracking() ? "Active" : "Inactive";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [ExcelFunction(Description = "Returns current workbook event count")]
        public static object DGT_EVENT_COUNT()
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn == null)
                    return "N/A";

                return addIn.GetCurrentWorkbookEventCount();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [ExcelFunction(Description = "Returns total events sent to API")]
        public static object DGT_EVENTS_SENT()
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn == null)
                    return "N/A";

                var stats = addIn.GetPublisherStats();
                return stats.TotalEventsSent;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [ExcelFunction(Description = "Returns queue size")]
        public static object DGT_QUEUE_SIZE()
        {
            try
            {
                var addIn = AddIn.Instance;
                if (addIn == null)
                    return "N/A";

                var stats = addIn.GetQueueStats();
                return stats.CurrentSize;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}
