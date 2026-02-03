# DominoGovernanceTracker (DGT)

Compliance-focused Excel add-in and backend for capturing and auditing Excel activity.

This repo includes:
- .NET Excel add-in (event capture and batching)
- FastAPI backend (storage, queries, and live dashboard)

## Key Features

- Captures cell value edits, formula changes, format changes, and workbook/sheet events
- Resilient delivery with async HTTP, retries, and local buffering when offline
- Minimal Excel UI with status and event counters
- Backend API for ingestion, queries, and statistics
- Live dashboard UI at `/dashboard`

## Architecture (High-Level)

Excel events -> EventManager -> EventQueue -> HttpEventPublisher -> REST API
                                      (fallback: LocalBuffer .jsonl)

## Quick Start

### Add-in (Excel)

```powershell
.\run-debug.ps1
```

This builds the add-in, launches Excel, and loads DGT. Look for the DGT ribbon tab and verify the status is Active.

### Backend (FastAPI)

```powershell
cd backend
python -m venv venv
venv\Scripts\activate
pip install -r requirements.txt
python run_dev.py
```

Open:
- API: http://localhost:5000
- Dashboard: http://localhost:5000/dashboard

## Configuration

Edit `src/DominoGovernanceTracker/config.json`:

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

## Event Types

The add-in sends `eventType` as an integer enum value. The full list is in:
- `src/DominoGovernanceTracker/Models/AuditEvent.cs`

Common categories include:
- Workbook events (open/close/save/activate/deactivate)
- Cell events (value input, formula change, calculated change)
- Format changes
- Sheet events (add/delete/rename/activate)
- Session start/end
- Selection changes (optional)

## REST API Integration

DGT sends events as JSON batches via HTTP POST:

```http
POST /api/events HTTP/1.1
Content-Type: application/json
X-API-Key: your-api-key-here
```

```json
{
  "events": [
    {
      "eventId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
      "timestamp": "2025-12-12T15:30:45.123Z",
      "eventType": 6,
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
}
```

## Logs and Local Buffer

Logs:
```
%LOCALAPPDATA%\DominoGovernanceTracker\logs\dgt-YYYYMMDD.log
```

Local buffer (when API is unreachable):
```
%LOCALAPPDATA%\DominoGovernanceTracker\buffer.jsonl
```

## Project Structure

```
excel-governance/
  src/DominoGovernanceTracker/   # Excel add-in (.NET)
  backend/                       # FastAPI backend
  .vscode/                       # VSCode configs
  run-debug.ps1                  # Build and launch Excel
  rebuild-addin.ps1              # Rebuild without launch
  check-addin.ps1                # Add-in health checks
  diagnose-loading.ps1           # Debug add-in loading issues
  view-logs.ps1                  # Tail logs
```

## Troubleshooting

- Add-in not loading: check `%LOCALAPPDATA%\DominoGovernanceTracker\logs`
- No events captured: verify `trackingEnabled: true` in config.json
- Events not reaching API: confirm backend is running and `apiEndpoint` is correct

## License

[Your License Here]

## Contributing

[Your Contribution Guidelines Here]
