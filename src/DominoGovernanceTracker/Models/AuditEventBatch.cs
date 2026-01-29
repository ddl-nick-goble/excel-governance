using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DominoGovernanceTracker.Models
{
    /// <summary>
    /// Batch wrapper for sending multiple audit events to the API.
    /// Matches the backend AuditEventBatch schema (models/schemas.py).
    /// </summary>
    public class AuditEventBatch
    {
        /// <summary>
        /// List of audit events to send.
        /// Backend expects 1-1000 events per batch.
        /// </summary>
        [JsonPropertyName("events")]
        public List<AuditEvent> Events { get; set; }

        /// <summary>
        /// Creates a new event batch
        /// </summary>
        public AuditEventBatch()
        {
            Events = new List<AuditEvent>();
        }

        /// <summary>
        /// Creates a new event batch with the specified events
        /// </summary>
        public AuditEventBatch(List<AuditEvent> events)
        {
            Events = events ?? new List<AuditEvent>();
        }
    }
}
