using System;
using System.Text.Json.Serialization;

namespace DominoGovernanceTracker.Models
{
    /// <summary>
    /// Types of audit events tracked by DGT
    /// </summary>
    public enum AuditEventType
    {
        // Workbook events
        WorkbookNew,
        WorkbookOpen,
        WorkbookClose,
        WorkbookSave,
        WorkbookActivate,
        WorkbookDeactivate,

        // Cell/Sheet events
        CellChange,
        SelectionChange,
        SheetAdd,
        SheetDelete,
        SheetRename,
        SheetActivate,

        // System events
        SessionStart,
        SessionEnd,
        AddInLoad,
        AddInUnload,
        Error
    }

    /// <summary>
    /// Represents a single audit event captured from Excel
    /// </summary>
    public class AuditEvent
    {
        /// <summary>
        /// Unique identifier for this event
        /// </summary>
        [JsonPropertyName("eventId")]
        public Guid EventId { get; set; } = Guid.NewGuid();

        /// <summary>
        /// UTC timestamp when event occurred
        /// </summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Type of event
        /// </summary>
        [JsonPropertyName("eventType")]
        public AuditEventType EventType { get; set; }

        // === Context Information ===

        /// <summary>
        /// Windows username
        /// </summary>
        [JsonPropertyName("userName")]
        public string UserName { get; set; }

        /// <summary>
        /// Machine name
        /// </summary>
        [JsonPropertyName("machineName")]
        public string MachineName { get; set; }

        /// <summary>
        /// User domain (if applicable)
        /// </summary>
        [JsonPropertyName("userDomain")]
        public string UserDomain { get; set; }

        /// <summary>
        /// Excel session ID (generated on startup)
        /// </summary>
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }

        // === Workbook Context ===

        /// <summary>
        /// Workbook name (e.g., "Book1.xlsx")
        /// </summary>
        [JsonPropertyName("workbookName")]
        public string WorkbookName { get; set; }

        /// <summary>
        /// Full path to workbook (if saved)
        /// </summary>
        [JsonPropertyName("workbookPath")]
        public string WorkbookPath { get; set; }

        /// <summary>
        /// Worksheet name
        /// </summary>
        [JsonPropertyName("sheetName")]
        public string SheetName { get; set; }

        // === Cell Change Context ===

        /// <summary>
        /// Cell address (e.g., "$A$1" or "$A$1:$B$5")
        /// </summary>
        [JsonPropertyName("cellAddress")]
        public string CellAddress { get; set; }

        /// <summary>
        /// Number of cells affected
        /// </summary>
        [JsonPropertyName("cellCount")]
        public int CellCount { get; set; }

        /// <summary>
        /// Previous cell value (for changes)
        /// </summary>
        [JsonPropertyName("oldValue")]
        public string OldValue { get; set; }

        /// <summary>
        /// New cell value (for changes)
        /// </summary>
        [JsonPropertyName("newValue")]
        public string NewValue { get; set; }

        /// <summary>
        /// Cell formula (if applicable)
        /// </summary>
        [JsonPropertyName("formula")]
        public string Formula { get; set; }

        // === Additional Data ===

        /// <summary>
        /// Additional event-specific details
        /// </summary>
        [JsonPropertyName("details")]
        public string Details { get; set; }

        /// <summary>
        /// Error message (for error events)
        /// </summary>
        [JsonPropertyName("errorMessage")]
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Correlation ID to link related events
        /// </summary>
        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; }

        /// <summary>
        /// Creates a deep copy of this event
        /// </summary>
        public AuditEvent Clone()
        {
            return (AuditEvent)MemberwiseClone();
        }
    }
}
