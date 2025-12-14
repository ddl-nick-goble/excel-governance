using System;

namespace DominoGovernanceTracker.Models
{
    /// <summary>
    /// Configuration model for DGT add-in
    /// </summary>
    public class DgtConfig
    {
        /// <summary>
        /// REST API endpoint to send events to
        /// </summary>
        public string ApiEndpoint { get; set; } = "http://localhost:5000/api/events";

        /// <summary>
        /// API key for authentication (optional)
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// Enable or disable event tracking
        /// </summary>
        public bool TrackingEnabled { get; set; } = true;

        /// <summary>
        /// Maximum number of events to buffer in memory before flushing
        /// </summary>
        public int MaxBufferSize { get; set; } = 100;

        /// <summary>
        /// Interval in seconds to flush events to API
        /// </summary>
        public int FlushIntervalSeconds { get; set; } = 10;

        /// <summary>
        /// Path to local buffer file for failed API calls
        /// </summary>
        public string LocalBufferPath { get; set; } = "";  // Will default to %LOCALAPPDATA%\DGT\buffer.jsonl

        /// <summary>
        /// Maximum retry attempts for failed API calls
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>
        /// Timeout in seconds for HTTP requests
        /// </summary>
        public int HttpTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Track cell selection changes (can be noisy)
        /// </summary>
        public bool TrackSelectionChanges { get; set; } = false;

        /// <summary>
        /// Include cell formulas in change events
        /// </summary>
        public bool IncludeFormulas { get; set; } = true;

        /// <summary>
        /// Maximum size of local buffer file in MB before rotation
        /// </summary>
        public int MaxBufferFileSizeMB { get; set; } = 50;

        /// <summary>
        /// Gets the resolved local buffer path
        /// </summary>
        public string GetLocalBufferPath()
        {
            if (!string.IsNullOrWhiteSpace(LocalBufferPath))
                return LocalBufferPath;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return System.IO.Path.Combine(appData, "DominoGovernanceTracker", "buffer.jsonl");
        }
    }
}
