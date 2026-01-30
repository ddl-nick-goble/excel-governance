# DominoGovernanceTracker (DGT) - ExcelDNA Reference Guide

## Project Overview

**Purpose**: Build a compliance-focused Excel Add-in using ExcelDNA that silently tracks all cell changes and workbook events for audit trail purposes in Financial Services environments.

**Core Requirements**:
- Track ALL cell changes automatically
- Track ALL workbook open/close events
- Auto-start tracking when Excel starts (if DGT installed)
- Non-intrusive to users who don't interact with it
- Compliance-ready for financial services (SOX, audit trails)

---

## 1. ExcelDNA Fundamentals

### What is ExcelDNA?
ExcelDNA is a free, open-source .NET library that allows creating high-performance Excel add-ins using C#, VB.NET, or F#. It creates `.xll` files that Excel loads natively.

### Key Benefits for Compliance Add-ins
- No admin rights required for installation
- No COM registration needed
- Works with .NET Framework 4.7.2+ or .NET 6+
- Supports Ribbon UI, Custom Task Panes, RTD servers
- Can pack all dependencies into single `.xll` file
- Event handling via COM interop

### Current Stable Version
- **ExcelDNA 1.9.0** (Latest as of research date)
- Supports .NET Framework 4.7.2+ and .NET 6+
- SDK-style project files supported
- Built-in async/streaming function support

---

## 2. Project Structure (Actual)

```
excel-governance/
├── src/
│   └── DominoGovernanceTracker/           # Main Add-in Project
│       ├── AddIn.cs                       # IExcelAddIn implementation (entry point)
│       ├── DominoGovernanceTracker.csproj
│       │
│       ├── Core/                          # Core tracking logic
│       │   ├── EventManager.cs            # Excel COM event subscription + enrichment
│       │   ├── EventQueue.cs              # Thread-safe bounded in-memory queue
│       │   └── WatchdogTimer.cs           # Self-healing timer for ribbon updates
│       │
│       ├── Models/                        # Data models
│       │   ├── AuditEvent.cs              # Event model + AuditEventType enum
│       │   ├── AuditEventBatch.cs         # Batch wrapper for HTTP transport
│       │   ├── DgtConfig.cs               # Configuration model
│       │   └── RegisteredModel.cs         # Model registration request/response
│       │
│       ├── Publishing/                    # Event publishing pipeline
│       │   ├── HttpEventPublisher.cs      # Background publisher with Polly retry + circuit breaker
│       │   └── LocalBuffer.cs             # Disk-based buffer for failed sends
│       │
│       ├── Services/                      # Business services
│       │   └── ModelRegistrationService.cs # Model registration + workbook property management
│       │
│       └── UI/                            # UI components
│           ├── DgtRibbon.cs               # Ribbon UI (status, register, buffer controls)
│           └── ModelRegistrationForm.cs   # WinForms dialog for model registration
│
├── backend/                               # Python FastAPI backend
│   ├── main.py                            # FastAPI app entry point
│   ├── api/
│   │   ├── events.py                      # POST /api/events, query, statistics
│   │   ├── models.py                      # Model registration endpoints
│   │   └── health.py                      # Health check endpoint
│   ├── models/
│   │   ├── database.py                    # SQLAlchemy ORM models
│   │   └── schemas.py                     # Pydantic request/response schemas
│   ├── repositories/
│   │   ├── event_repository.py            # Event CRUD with bulk insert
│   │   └── model_repository.py            # Model registration CRUD
│   └── services/
│       ├── event_service.py               # Event ingestion logic
│       └── model_service.py               # Model registration logic
│
├── DGT_Backend_API_SPECIFICATION.md
├── DGT_ExcelDNA_Reference.md
└── rebuild-addin.ps1                      # Build script for the add-in
```

---

## 3. Core Project Configuration

### SDK-Style .csproj (Recommended)

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <!-- For .NET 6: net6.0-windows -->
    
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    
    <!-- ExcelDNA specific properties -->
    <ExcelAddInName>DominoGovernanceTracker Add-In</ExcelAddInName>
    <ExcelAddInFileName>DominoGovernanceTracker-AddIn</ExcelAddInFileName>
    
    <!-- Packing options -->
    <RunExcelDnaPack>true</RunExcelDnaPack>
    <ExcelDnaPackCompressResources>false</ExcelDnaPackCompressResources>
    <!-- false helps avoid antivirus false positives -->
  </PropertyGroup>

  <ItemGroup>
    <!-- Core ExcelDNA -->
    <PackageReference Include="ExcelDna.AddIn" Version="1.9.0" />
    <PackageReference Include="ExcelDna.Interop" Version="15.0.1" />
    
    <!-- Logging -->
    <PackageReference Include="Serilog" Version="3.1.1" />
    <PackageReference Include="Serilog.Sinks.SQLite" Version="6.0.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
    <PackageReference Include="ExcelDna.Diagnostics.Serilog" Version="1.5.0" />
    <PackageReference Include="Serilog.Enrichers.ExcelDna" Version="1.0.0" />
    
    <!-- Storage -->
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.0" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>

</Project>
```

---

## 4. IExcelAddIn Implementation (Entry Point)

The `IExcelAddIn` interface is the entry point. `AutoOpen()` runs when Excel loads the add-in.

```csharp
using System;
using System.Runtime.InteropServices;
using ExcelDna.Integration;
using Serilog;
using MSExcel = Microsoft.Office.Interop.Excel;

namespace DominoGovernanceTracker
{
    public class AddIn : IExcelAddIn
    {
        private static MSExcel.Application _excelApp;
        private static EventManager _eventManager;
        private static AuditLogger _auditLogger;
        
        public void AutoOpen()
        {
            // Initialize logging first
            InitializeLogging();
            
            // IMPORTANT: Delay COM access to avoid "Book2" problem
            // Schedule initialization after Excel is fully ready
            ExcelAsyncUtil.QueueAsMacro(InitializeTracking);
            
            Log.Information("DGT Add-in loaded successfully");
        }

        public void AutoClose()
        {
            // Cleanup
            _eventManager?.Dispose();
            _auditLogger?.Flush();
            Log.Information("DGT Add-in unloaded");
            Log.CloseAndFlush();
        }

        private void InitializeLogging()
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DominoGovernanceTracker", "logs", "dgt-.log");

            var dbPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DominoGovernanceTracker", "audit.db");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.WithXllPath()  // ExcelDNA enricher
                .WriteTo.File(logPath, 
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 90)  // 90-day retention
                .WriteTo.SQLite(dbPath, 
                    tableName: "AuditLog",
                    storeTimestampInUtc: true)
                .CreateLogger();
        }

        private void InitializeTracking()
        {
            try
            {
                // Get Excel Application object (safe after QueueAsMacro)
                _excelApp = (MSExcel.Application)ExcelDnaUtil.Application;
                
                // Initialize audit logger
                _auditLogger = new AuditLogger();
                
                // Initialize and start event tracking
                _eventManager = new EventManager(_excelApp, _auditLogger);
                _eventManager.StartTracking();
                
                Log.Information("DGT tracking initialized for Excel session");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize DGT tracking");
            }
        }
    }
}
```

---

## 5. Event Tracking Implementation

### EventManager - Central Event Coordinator

```csharp
using System;
using MSExcel = Microsoft.Office.Interop.Excel;
using Serilog;

namespace DominoGovernanceTracker.Core
{
    public class EventManager : IDisposable
    {
        private readonly MSExcel.Application _app;
        private readonly AuditLogger _logger;
        private bool _isTracking;

        public EventManager(MSExcel.Application app, AuditLogger logger)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void StartTracking()
        {
            if (_isTracking) return;

            // Workbook lifecycle events
            _app.WorkbookOpen += OnWorkbookOpen;
            _app.WorkbookBeforeClose += OnWorkbookBeforeClose;
            _app.WorkbookAfterSave += OnWorkbookAfterSave;
            _app.WorkbookActivate += OnWorkbookActivate;
            _app.WorkbookDeactivate += OnWorkbookDeactivate;
            
            // Sheet events (application-level)
            _app.SheetChange += OnSheetChange;
            _app.SheetSelectionChange += OnSheetSelectionChange;
            
            // New workbook
            _app.NewWorkbook += OnNewWorkbook;
            
            _isTracking = true;
            Log.Debug("Event tracking started");
        }

        public void StopTracking()
        {
            if (!_isTracking) return;

            _app.WorkbookOpen -= OnWorkbookOpen;
            _app.WorkbookBeforeClose -= OnWorkbookBeforeClose;
            _app.WorkbookAfterSave -= OnWorkbookAfterSave;
            _app.WorkbookActivate -= OnWorkbookActivate;
            _app.WorkbookDeactivate -= OnWorkbookDeactivate;
            _app.SheetChange -= OnSheetChange;
            _app.SheetSelectionChange -= OnSheetSelectionChange;
            _app.NewWorkbook -= OnNewWorkbook;
            
            _isTracking = false;
            Log.Debug("Event tracking stopped");
        }

        // === EVENT HANDLERS ===

        private void OnWorkbookOpen(MSExcel.Workbook wb)
        {
            _logger.LogEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookOpen,
                WorkbookName = wb.Name,
                WorkbookPath = GetSafePath(wb),
                Timestamp = DateTime.UtcNow,
                UserName = Environment.UserName,
                MachineName = Environment.MachineName
            });
        }

        private void OnWorkbookBeforeClose(MSExcel.Workbook wb, ref bool cancel)
        {
            _logger.LogEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookClose,
                WorkbookName = wb.Name,
                WorkbookPath = GetSafePath(wb),
                Timestamp = DateTime.UtcNow,
                UserName = Environment.UserName,
                MachineName = Environment.MachineName
            });
        }

        private void OnWorkbookAfterSave(MSExcel.Workbook wb, bool success)
        {
            _logger.LogEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookSave,
                WorkbookName = wb.Name,
                WorkbookPath = GetSafePath(wb),
                Timestamp = DateTime.UtcNow,
                UserName = Environment.UserName,
                MachineName = Environment.MachineName,
                Details = $"SaveSuccess={success}"
            });
        }

        private void OnSheetChange(object sheet, MSExcel.Range target)
        {
            try
            {
                var ws = sheet as MSExcel.Worksheet;
                if (ws == null) return;

                var wb = ws.Parent as MSExcel.Workbook;
                
                _logger.LogEvent(new AuditEvent
                {
                    EventType = AuditEventType.CellChange,
                    WorkbookName = wb?.Name ?? "Unknown",
                    WorkbookPath = GetSafePath(wb),
                    SheetName = ws.Name,
                    CellAddress = target.Address,
                    CellCount = target.Cells.Count,
                    NewValue = GetCellValue(target),
                    Timestamp = DateTime.UtcNow,
                    UserName = Environment.UserName,
                    MachineName = Environment.MachineName
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error capturing cell change event");
            }
        }

        private void OnNewWorkbook(MSExcel.Workbook wb)
        {
            _logger.LogEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookNew,
                WorkbookName = wb.Name,
                Timestamp = DateTime.UtcNow,
                UserName = Environment.UserName,
                MachineName = Environment.MachineName
            });
        }

        private void OnWorkbookActivate(MSExcel.Workbook wb)
        {
            _logger.LogEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookActivate,
                WorkbookName = wb.Name,
                WorkbookPath = GetSafePath(wb),
                Timestamp = DateTime.UtcNow,
                UserName = Environment.UserName
            });
        }

        private void OnWorkbookDeactivate(MSExcel.Workbook wb)
        {
            _logger.LogEvent(new AuditEvent
            {
                EventType = AuditEventType.WorkbookDeactivate,
                WorkbookName = wb.Name,
                WorkbookPath = GetSafePath(wb),
                Timestamp = DateTime.UtcNow,
                UserName = Environment.UserName
            });
        }

        private void OnSheetSelectionChange(object sheet, MSExcel.Range target)
        {
            // Optional: track selection changes for compliance
            // Can be noisy - consider making configurable
        }

        // === HELPERS ===

        private string GetSafePath(MSExcel.Workbook wb)
        {
            try { return wb?.FullName; }
            catch { return ""; }
        }

        private string GetCellValue(MSExcel.Range target)
        {
            try
            {
                if (target.Cells.Count == 1)
                    return target.Value?.ToString() ?? "";
                return $"[{target.Cells.Count} cells]";
            }
            catch { return "[Error reading value]"; }
        }

        public void Dispose()
        {
            StopTracking();
        }
    }
}
```

---

## 6. Audit Event Model

```csharp
using System;
using System.Text.Json.Serialization;

namespace DominoGovernanceTracker.Models
{
    public enum AuditEventType
    {
        // Workbook events (0-5)
        WorkbookNew,
        WorkbookOpen,
        WorkbookClose,
        WorkbookSave,
        WorkbookActivate,
        WorkbookDeactivate,

        // Cell/Sheet events (6-11)
        CellChange,
        SelectionChange,
        SheetAdd,
        SheetDelete,
        SheetRename,
        SheetActivate,

        // System events (12-16)
        SessionStart,
        SessionEnd,
        AddInLoad,
        AddInUnload,
        Error,

        // Model events (17)
        ModelRegistration
    }

    public class AuditEvent
    {
        [JsonPropertyName("eventId")]
        public Guid EventId { get; set; } = Guid.NewGuid();

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("eventType")]
        public AuditEventType EventType { get; set; }

        // Context
        [JsonPropertyName("userName")]
        public string UserName { get; set; }
        [JsonPropertyName("machineName")]
        public string MachineName { get; set; }
        [JsonPropertyName("userDomain")]
        public string UserDomain { get; set; }
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }

        // Workbook context
        [JsonPropertyName("workbookName")]
        public string WorkbookName { get; set; }
        [JsonPropertyName("workbookPath")]
        public string WorkbookPath { get; set; }
        [JsonPropertyName("sheetName")]
        public string SheetName { get; set; }

        // Cell context
        [JsonPropertyName("cellAddress")]
        public string CellAddress { get; set; }
        [JsonPropertyName("cellCount")]
        public int CellCount { get; set; }
        [JsonPropertyName("oldValue")]
        public string OldValue { get; set; }
        [JsonPropertyName("newValue")]
        public string NewValue { get; set; }
        [JsonPropertyName("formula")]
        public string Formula { get; set; }

        // Additional
        [JsonPropertyName("details")]
        public string Details { get; set; }
        [JsonPropertyName("errorMessage")]
        public string ErrorMessage { get; set; }
        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; }
        [JsonPropertyName("modelId")]
        public string ModelId { get; set; }         // Registered model ID
    }
}
```

### Event Publishing Pipeline

Events flow through a multi-stage pipeline:

1. **EventManager** captures Excel COM events, enriches with user/session context, gates on workbook registration, and enqueues to the EventQueue
2. **EventQueue** is a thread-safe bounded in-memory queue (default 1000 events). Overflow is handled by the LocalBuffer
3. **HttpEventPublisher** runs on a background thread with a periodic flush timer. It dequeues batches and sends to the API using Polly retry (exponential backoff) and circuit breaker (50% failure rate over 30s). Failed sends go to the LocalBuffer
4. **LocalBuffer** persists failed batches to disk as JSON files. On startup, buffered events are re-sent. Duplicate `event_id`s are handled server-side via `INSERT ... ON CONFLICT DO NOTHING`

### Model Registration

Workbooks must be registered before events are tracked. Registration stores a `model_id` (UUID) in the workbook's custom document properties. The EventManager checks for this property and only enqueues events for registered workbooks. A `ModelRegistration` event is emitted when registration succeeds.

---

## 7. SQLite Storage Implementation

```csharp
using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Serilog;

namespace DominoGovernanceTracker.Storage
{
    public class SqliteAuditStore : IAuditStore
    {
        private readonly string _connectionString;
        private readonly object _lock = new object();

        public SqliteAuditStore(string dbPath = null)
        {
            dbPath ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DominoGovernanceTracker", "audit.db");

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath));
            _connectionString = $"Data Source={dbPath}";
            
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS AuditEvents (
                    EventId TEXT PRIMARY KEY,
                    Timestamp TEXT NOT NULL,
                    EventType TEXT NOT NULL,
                    UserName TEXT,
                    MachineName TEXT,
                    UserDomain TEXT,
                    WorkbookName TEXT,
                    WorkbookPath TEXT,
                    SheetName TEXT,
                    CellAddress TEXT,
                    CellCount INTEGER,
                    OldValue TEXT,
                    NewValue TEXT,
                    Formula TEXT,
                    Details TEXT,
                    ErrorMessage TEXT,
                    CorrelationId TEXT,
                    SessionId TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_timestamp ON AuditEvents(Timestamp);
                CREATE INDEX IF NOT EXISTS idx_eventtype ON AuditEvents(EventType);
                CREATE INDEX IF NOT EXISTS idx_workbook ON AuditEvents(WorkbookName);
                CREATE INDEX IF NOT EXISTS idx_user ON AuditEvents(UserName);
            ";
            cmd.ExecuteNonQuery();
        }

        public void Store(AuditEvent evt)
        {
            lock (_lock)
            {
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();

                    var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO AuditEvents 
                        (EventId, Timestamp, EventType, UserName, MachineName, UserDomain,
                         WorkbookName, WorkbookPath, SheetName, CellAddress, CellCount,
                         OldValue, NewValue, Formula, Details, ErrorMessage, CorrelationId, SessionId)
                        VALUES 
                        (@EventId, @Timestamp, @EventType, @UserName, @MachineName, @UserDomain,
                         @WorkbookName, @WorkbookPath, @SheetName, @CellAddress, @CellCount,
                         @OldValue, @NewValue, @Formula, @Details, @ErrorMessage, @CorrelationId, @SessionId)
                    ";

                    cmd.Parameters.AddWithValue("@EventId", evt.EventId.ToString());
                    cmd.Parameters.AddWithValue("@Timestamp", evt.Timestamp.ToString("O"));
                    cmd.Parameters.AddWithValue("@EventType", evt.EventType.ToString());
                    cmd.Parameters.AddWithValue("@UserName", evt.UserName ?? "");
                    cmd.Parameters.AddWithValue("@MachineName", evt.MachineName ?? "");
                    cmd.Parameters.AddWithValue("@UserDomain", evt.UserDomain ?? "");
                    cmd.Parameters.AddWithValue("@WorkbookName", evt.WorkbookName ?? "");
                    cmd.Parameters.AddWithValue("@WorkbookPath", evt.WorkbookPath ?? "");
                    cmd.Parameters.AddWithValue("@SheetName", evt.SheetName ?? "");
                    cmd.Parameters.AddWithValue("@CellAddress", evt.CellAddress ?? "");
                    cmd.Parameters.AddWithValue("@CellCount", evt.CellCount);
                    cmd.Parameters.AddWithValue("@OldValue", evt.OldValue ?? "");
                    cmd.Parameters.AddWithValue("@NewValue", evt.NewValue ?? "");
                    cmd.Parameters.AddWithValue("@Formula", evt.Formula ?? "");
                    cmd.Parameters.AddWithValue("@Details", evt.Details ?? "");
                    cmd.Parameters.AddWithValue("@ErrorMessage", evt.ErrorMessage ?? "");
                    cmd.Parameters.AddWithValue("@CorrelationId", evt.CorrelationId ?? "");
                    cmd.Parameters.AddWithValue("@SessionId", evt.SessionId ?? "");

                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to store audit event");
                }
            }
        }
    }
}
```

---

## 8. Optional Ribbon UI

```csharp
using System.Runtime.InteropServices;
using ExcelDna.Integration;
using ExcelDna.Integration.CustomUI;

namespace DominoGovernanceTracker.UI
{
    [ComVisible(true)]
    public class DgtRibbon : ExcelRibbon
    {
        private IRibbonUI _ribbon;

        public override string GetCustomUI(string ribbonId)
        {
            return @"
            <customUI xmlns='http://schemas.microsoft.com/office/2009/07/customui' onLoad='OnRibbonLoad'>
              <ribbon>
                <tabs>
                  <tab id='dgtTab' label='Governance' insertAfterMso='TabView'>
                    <group id='dgtStatus' label='DGT Status'>
                      <button id='btnStatus' 
                              label='Tracking Status' 
                              imageMso='Info' 
                              size='large'
                              onAction='OnStatusClick'
                              screentip='View DGT tracking status'/>
                      <button id='btnViewLog' 
                              label='View Audit Log' 
                              imageMso='ShowTimelineView' 
                              size='large'
                              onAction='OnViewLogClick'
                              screentip='Open audit log viewer'/>
                    </group>
                    <group id='dgtExport' label='Export'>
                      <button id='btnExport' 
                              label='Export Log' 
                              imageMso='ExportSavedExports' 
                              size='normal'
                              onAction='OnExportClick'/>
                    </group>
                  </tab>
                </tabs>
              </ribbon>
            </customUI>";
        }

        public void OnRibbonLoad(IRibbonUI ribbon)
        {
            _ribbon = ribbon;
        }

        public void OnStatusClick(IRibbonControl control)
        {
            System.Windows.Forms.MessageBox.Show(
                "DGT Tracking: Active\nEvents logged today: [count]",
                "DGT Status",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }

        public void OnViewLogClick(IRibbonControl control)
        {
            // Open Custom Task Pane with audit viewer
        }

        public void OnExportClick(IRibbonControl control)
        {
            // Export audit log to CSV/Excel
        }
    }
}
```

---

## 9. Key ExcelDNA Patterns & Gotchas

### The "Book2" Problem
When accessing `ExcelDnaUtil.Application` in `AutoOpen()`, Excel may create a phantom workbook. **Solution**: Delay COM access using `ExcelAsyncUtil.QueueAsMacro()`:

```csharp
public void AutoOpen()
{
    // Don't access ExcelDnaUtil.Application here!
    ExcelAsyncUtil.QueueAsMacro(InitializeTracking);
}

private void InitializeTracking()
{
    // Safe to access COM here
    var app = (MSExcel.Application)ExcelDnaUtil.Application;
}
```

### Thread Safety
Excel COM is single-threaded (STA). **Never** access COM from background threads:

```csharp
// WRONG - will cause issues
Task.Run(() => {
    var app = ExcelDnaUtil.Application; // BAD!
});

// RIGHT - marshal to main thread
ExcelAsyncUtil.QueueAsMacro(() => {
    var app = ExcelDnaUtil.Application; // OK
});
```

### Never Use Marshal.ReleaseComObject
Do NOT call `Marshal.ReleaseComObject()` on Excel COM objects. Let the GC handle it.

### Event Handler Memory Leaks
Always unsubscribe from events in `AutoClose()` to prevent memory leaks.

---

## 10. Compliance Considerations

### Audit Trail Requirements (SOX, Financial Services)
- **Timestamp**: UTC, ISO 8601 format
- **User identification**: Username, domain, machine
- **What changed**: Before/after values
- **Non-repudiation**: Tamper-evident logs
- **Retention**: Typically 7 years for financial data
- **Encryption**: At rest for sensitive data

### Data to Capture (Minimum)
| Event | Required Fields |
|-------|-----------------|
| Workbook Open | Path, User, Timestamp, MachineName |
| Workbook Close | Path, User, Timestamp, Duration |
| Workbook Save | Path, User, Timestamp, Success |
| Cell Change | Address, OldValue, NewValue, User, Timestamp |

### Log Protection
- Store logs outside of workbooks
- Use SQLite with WAL mode for durability
- Consider signing/hashing log entries
- Backup logs to network location

---

## 11. Testing Strategy

### Unit Tests
- Test `AuditEvent` serialization
- Test `SqliteAuditStore` CRUD operations
- Test configuration loading

### Integration Tests
- Mock Excel COM objects
- Test event capture with sample workbooks

### Manual Testing Checklist
- [ ] Add-in loads without errors
- [ ] No "Book2" created on startup
- [ ] Cell changes captured
- [ ] Workbook open/close captured
- [ ] Logs persisted to SQLite
- [ ] Ribbon UI displays (if enabled)
- [ ] Export functions work
- [ ] Excel exits cleanly

---

## 12. Deployment

### Packed XLL Distribution
ExcelDNA can pack all dependencies into a single `.xll` file:

```xml
<PropertyGroup>
  <RunExcelDnaPack>true</RunExcelDnaPack>
  <ExcelDnaPackCompressResources>false</ExcelDnaPackCompressResources>
</PropertyGroup>
```

### Installation Locations
- Per-user: `%APPDATA%\Microsoft\AddIns\`
- Trust settings: `File > Options > Trust Center > Trusted Locations`

### Silent Installation
Add to registry for auto-load:
```
HKCU\Software\Microsoft\Office\16.0\Excel\Options\OPEN
```

---

## 13. Dependencies Summary

| Package | Purpose |
|---------|---------|
| ExcelDna.AddIn | Core ExcelDNA framework |
| ExcelDna.Interop | Excel COM type definitions |
| Serilog | Structured logging |
| Serilog.Sinks.SQLite | SQLite log sink |
| Serilog.Sinks.File | File log sink |
| Microsoft.Data.Sqlite | SQLite database access |
| ExcelDna.Diagnostics.Serilog | Bridge ExcelDNA to Serilog |
| Serilog.Enrichers.ExcelDna | Add XLL context to logs |

---

## 14. Quick Reference Commands

```bash
# Create new project
dotnet new classlib -n DominoGovernanceTracker
cd DominoGovernanceTracker
dotnet add package ExcelDna.AddIn

# Build
dotnet build

# Debug (set in launchSettings.json)
# Launches Excel with add-in loaded

# Pack for distribution
# Automatic with RunExcelDnaPack=true

# Output locations
# bin/Debug/net472/DominoGovernanceTracker-AddIn.xll (unpacked)
# bin/Debug/net472/publish/DominoGovernanceTracker-AddIn-packed.xll (packed)
```

---

## 15. Next Steps

1. **Create solution structure** as outlined above
2. **Implement core tracking** (EventManager, AuditLogger)
3. **Set up SQLite storage** with schema
4. **Add basic Ribbon UI** (optional but recommended)
5. **Create configuration system** for enable/disable tracking
6. **Add export functionality** (CSV, Excel)
7. **Implement log rotation/archival**
8. **Add tamper-detection** (hash chains)
9. **Package and test deployment**
10. **Documentation for compliance team**

---

*This document serves as the technical reference for the DGT project. Refer to it when implementing features or debugging issues.*
