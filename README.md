# DominoGovernanceTracker (DGT)

**Compliance-focused Excel Add-in for Financial Services**

DGT is a capture-and-forward telemetry system that automatically tracks all Excel cell changes and workbook events, streaming them to a REST API backend for compliance and audit purposes.

## 🎯 Key Features

- **Automatic Tracking**: Captures ALL cell changes and workbook open/close events
- **Auto-Start**: Begins tracking as soon as Excel starts (when DGT is installed)
- **Non-Intrusive**: Silent operation - users don't need to interact with it
- **Resilient Architecture**: Async HTTP with retry logic and local buffering
- **Compliance-Ready**: Built for Financial Services (SOX, audit trails)
- **Minimal UI**: Simple ribbon showing status and live event counter

## 🏗️ Architecture

```
Excel Events → EventManager → EventQueue → HttpEventPublisher → REST API
                                              ↓ (on failure)
                                         LocalBuffer (.jsonl)
```

**DGT is a capture layer, not a storage layer.** Events are:
1. Captured from Excel COM events
2. Queued in memory
3. Sent to REST API asynchronously
4. Buffered locally only when API is unreachable
5. Deleted after successful delivery

## 🚀 Quick Start

### Prerequisites

- Windows 10/11
- .NET Framework 4.7.2 or higher
- Visual Studio 2022 (or MSBuild tools)
- Microsoft Excel 2016 or later
- PowerShell 5.1 or higher

### Build and Run

1. **Clone the repository**
   ```bash
   cd c:\Users\nick.goble\Downloads\Projects\ExcelAddinProject\excel-governance
   ```

2. **Build and launch Excel with add-in**
   ```powershell
   .\run-debug.ps1
   ```

   This script will:
   - Build the project
   - Create the `.xll` add-in file
   - Launch Excel with DGT loaded
   - Show log file locations

3. **Verify it's working**
   - Look for the **DGT** tab in Excel ribbon
   - Check **Status: ✓ Active**
   - Make some cell changes and watch the **Events** counter increment

## 🛠️ Development Workflow

### VSCode Debugging

1. **Set breakpoints** in your C# code

2. **Build and launch Excel**
   ```powershell
   .\run-debug.ps1
   ```

3. **Attach debugger**
   - Press `F5` in VSCode
   - Select "DGT: Attach to Excel"
   - Choose the EXCEL.EXE process

4. **Make changes in Excel** to trigger breakpoints

### Rebuild After Code Changes

```powershell
# Normal rebuild
.\run-debug.ps1

# Clean rebuild
.\run-debug.ps1 -Clean
```

### View Logs

Logs are written to:
```
%LOCALAPPDATA%\DominoGovernanceTracker\logs\dgt-YYYYMMDD.log
```

View in real-time:
```powershell
Get-Content "$env:LOCALAPPDATA\DominoGovernanceTracker\logs\dgt-*.log" -Wait -Tail 50
```

### View Local Buffer

When the API is unreachable, events are buffered to:
```
%LOCALAPPDATA%\DominoGovernanceTracker\buffer.jsonl
```

View buffered events:
```powershell
Get-Content "$env:LOCALAPPDATA\DominoGovernanceTracker\buffer.jsonl" | Select-Object -First 10
```

## ⚙️ Configuration

Edit `config.json` in the add-in directory:

```json
{
  "apiEndpoint": "http://localhost:5000/api/events",
  "apiKey": "",
  "trackingEnabled": true,
  "maxBufferSize": 100,
  "flushIntervalSeconds": 10,
  "maxRetryAttempts": 3,
  "httpTimeoutSeconds": 30,
  "trackSelectionChanges": false,
  "includeFormulas": true,
  "maxBufferFileSizeMB": 50
}
```

### Key Settings

| Setting | Description | Default |
|---------|-------------|---------|
| `apiEndpoint` | REST API URL to send events to | `http://localhost:5000/api/events` |
| `trackingEnabled` | Master switch for tracking | `true` |
| `flushIntervalSeconds` | How often to send events to API | `10` seconds |
| `maxRetryAttempts` | Retry attempts for failed API calls | `3` |
| `includeFormulas` | Capture cell formulas | `true` |
| `trackSelectionChanges` | Track cell selection (can be noisy) | `false` |

## 📡 REST API Integration

DGT sends events as JSON arrays via HTTP POST:

### Request Format

```http
POST /api/events HTTP/1.1
Content-Type: application/json
X-API-Key: your-api-key-here

[
  {
    "eventId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
    "timestamp": "2025-12-12T15:30:45.123Z",
    "eventType": "CellChange",
    "userName": "john.doe",
    "machineName": "DESKTOP-ABC123",
    "sessionId": "abc123...",
    "workbookName": "Budget.xlsx",
    "workbookPath": "C:\\Users\\john.doe\\Documents\\Budget.xlsx",
    "sheetName": "Sheet1",
    "cellAddress": "$A$1",
    "cellCount": 1,
    "oldValue": "100",
    "newValue": "200",
    "formula": "=B1*2"
  }
]
```

### Event Types

- `WorkbookNew` - New workbook created
- `WorkbookOpen` - Workbook opened
- `WorkbookClose` - Workbook closed
- `WorkbookSave` - Workbook saved
- `CellChange` - Cell value changed
- `SessionStart` - Excel session started
- `SessionEnd` - Excel session ended

### API Response

Success: HTTP 200-299 (any 2xx status code)
Failure: Any other status code triggers retry and local buffering

### Example Backend (Minimal)

```csharp
[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    [HttpPost]
    public IActionResult ReceiveEvents([FromBody] List<AuditEvent> events)
    {
        // Store events in your database
        foreach (var evt in events)
        {
            _repository.Store(evt);
        }

        return Ok(new { received = events.Count });
    }
}
```

## 🧪 Testing the Add-in

### Manual Testing Checklist

- [ ] Add-in loads without errors (check logs)
- [ ] No "Book2" phantom workbook created on startup
- [ ] DGT ribbon tab appears
- [ ] Status shows "✓ Active"
- [ ] Cell changes increment event counter
- [ ] Workbook open/close captured (check logs)
- [ ] Events sent to API (check API logs)
- [ ] Local buffer created when API unavailable
- [ ] Buffered events resent when API recovers
- [ ] Excel exits cleanly (no crashes)

### Test Event Capture

1. **Open a new workbook** → Should log `WorkbookNew`
2. **Type in A1** → Should log `CellChange`
3. **Save workbook** → Should log `WorkbookSave`
4. **Close workbook** → Should log `WorkbookClose`

Watch the event counter in the ribbon increment!

### Test Excel Functions (Optional)

DGT includes debugging functions you can use in Excel cells:

- `=DGT_STATUS()` - Returns "Active" or "Inactive"
- `=DGT_EVENT_COUNT()` - Returns current workbook event count
- `=DGT_EVENTS_SENT()` - Returns total events sent to API
- `=DGT_QUEUE_SIZE()` - Returns current queue size

## 📁 Project Structure

```
excel-governance/
├── src/
│   └── DominoGovernanceTracker/
│       ├── AddIn.cs                      # Entry point (IExcelAddIn)
│       ├── DominoGovernanceTracker.csproj
│       ├── config.json                   # Configuration
│       │
│       ├── Core/
│       │   ├── EventManager.cs           # Excel event subscriptions
│       │   └── EventQueue.cs             # Thread-safe queue
│       │
│       ├── Models/
│       │   ├── AuditEvent.cs             # Event data model
│       │   └── DgtConfig.cs              # Configuration model
│       │
│       ├── Publishing/
│       │   ├── HttpEventPublisher.cs     # Async HTTP sender
│       │   └── LocalBuffer.cs            # JSON lines buffer
│       │
│       ├── UI/
│       │   └── DgtRibbon.cs              # Minimal ribbon UI
│       │
│       └── Config/
│           └── ConfigManager.cs          # Config loader
│
├── .vscode/
│   ├── launch.json                       # VSCode debug config
│   └── tasks.json                        # Build tasks
│
├── run-debug.ps1                         # Build & debug script
├── README.md                             # This file
└── DGT_ExcelDNA_Reference.md            # Technical reference
```

## 🔧 Troubleshooting

### Add-in doesn't load

1. Check logs: `%LOCALAPPDATA%\DominoGovernanceTracker\logs\`
2. Verify .NET Framework 4.7.2+ is installed
3. Check if Excel is blocking the XLL (Trust Center settings)

### No events being captured

1. Check ribbon status (should show "✓ Active")
2. Verify `trackingEnabled: true` in config.json
3. Check logs for initialization errors

### Events not reaching API

1. Verify API endpoint is correct in config.json
2. Check if API is running: `curl http://localhost:5000/api/events`
3. Check local buffer file - events should be buffered there
4. Review logs for HTTP errors

### High memory usage

1. Reduce `maxBufferSize` in config.json
2. Decrease `flushIntervalSeconds` to send events more frequently
3. Check if API is available (buffering uses more memory)

### Excel crashes on exit

1. Check logs for COM-related errors
2. Ensure event handlers are being unsubscribed
3. Verify no COM objects are being leaked

## 🚧 Common Pitfalls Avoided

✅ **No "Book2" phantom workbook** - Uses `ExcelAsyncUtil.QueueAsMacro()` to delay COM access
✅ **No threading issues** - All COM access on main thread
✅ **No memory leaks** - Proper event unsubscription and disposal
✅ **No blocking** - Async HTTP on background thread
✅ **Resilient** - Polly retry policies and circuit breaker
✅ **Durable** - Local buffer prevents data loss

## 📊 Performance Characteristics

- **Capture overhead**: ~1-2ms per cell change event
- **Memory usage**: ~10-50 MB (depends on queue size)
- **Network**: Batched sends every 10 seconds (configurable)
- **Disk I/O**: Minimal (only when API unavailable)

## 🔐 Security Considerations

- **API Key**: Store API key securely in config
- **HTTPS**: Use HTTPS endpoints in production
- **PII**: Cell values may contain sensitive data
- **Encryption**: Consider encrypting events before sending
- **Access Control**: Protect API endpoints with authentication

## 📝 Next Steps

1. **Build REST API backend** to receive events
2. **Set up database** for storing audit events
3. **Implement retention policies** (e.g., 7 years for SOX)
4. **Add encryption** for events in transit and at rest
5. **Create reporting dashboard** for compliance team
6. **Package for deployment** (MSI installer, GPO deployment)
7. **Add digital signatures** for tamper detection

## 📚 References

- [ExcelDNA Documentation](https://github.com/Excel-DNA/ExcelDna)
- [DGT_ExcelDNA_Reference.md](./DGT_ExcelDNA_Reference.md) - Technical deep-dive
- [Polly Documentation](https://github.com/App-vNext/Polly)

## 📄 License

[Your License Here]

## 🤝 Contributing

[Your Contribution Guidelines Here]

---

**Built with ExcelDNA** | **Designed for Financial Services Compliance**
