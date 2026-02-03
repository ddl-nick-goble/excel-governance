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
        WorkbookNew = 0,
        WorkbookOpen = 1,
        WorkbookClose = 2,
        WorkbookSave = 3,
        WorkbookActivate = 4,
        WorkbookDeactivate = 5,

        // Cell/Sheet events
        CellChange = 6,
        SelectionChange = 7,
        SheetAdd = 8,
        SheetDelete = 9,
        SheetActivate = 11,

        // System events
        SessionStart = 12,
        SessionEnd = 13,
        AddInLoad = 14,
        AddInUnload = 15,
        Error = 16,

        // Model events
        ModelRegistration = 17,

        // Calculated/dependent cell changes
        CalculatedCellChange = 18,

        // Formatting changes
        FormatChange = 19,

        // Input vs formula distinction
        ValueInput = 23,
        FormulaChange = 24,

        // Calculation mode
        CalculationModeChange = 25,

        // External data
        DataRefreshStart = 26,
        DataRefreshEnd = 27,

        // Named ranges / defined names
        DefinedNameAdd = 28,
        DefinedNameChange = 29,
        DefinedNameDelete = 30,

        // Undo/Redo
        UndoPerformed = 31,
        RedoPerformed = 32,

        // Save As (identity change)
        WorkbookSaveAs = 33
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

        /// <summary>
        /// Displayed cell text (formatted value as shown in Excel)
        /// </summary>
        [JsonPropertyName("displayValue")]
        public string DisplayValue { get; set; }

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
        /// Registered model ID (set when workbook is registered)
        /// </summary>
        [JsonPropertyName("modelId")]
        public string ModelId { get; set; }

        /// <summary>
        /// Creates a deep copy of this event
        /// </summary>
        public AuditEvent Clone()
        {
            return (AuditEvent)MemberwiseClone();
        }
    }
}
