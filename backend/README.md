# Domino Governance Tracker - Backend

FastAPI backend for Excel compliance and governance tracking. Stores and serves audit events captured by the .NET Excel add-in and exposes a live dashboard UI.

## What This Provides

- REST API for event ingestion, querying, and statistics
- WebSocket endpoint for live updates
- Dashboard UI at `/dashboard`
- Async database access with SQLAlchemy (SQLite or PostgreSQL)
- Background retention cleanup (optional)
- Structured logging via structlog

## Quick Start (Local, SQLite)

```bash
cd backend
python -m venv venv
venv\Scripts\activate
pip install -r requirements.txt
python run_dev.py
```

Access:
- API: http://localhost:5000
- Docs: http://localhost:5000/docs
- Dashboard: http://localhost:5000/dashboard
- Health: http://localhost:5000/health

## Core Endpoints

- POST `/api/events` - ingest batch of events (primary add-in endpoint)
- POST `/api/events/query` - query events with filters
- GET `/api/events/statistics` - aggregated stats
- GET `/api/events/{event_id}` - fetch a single event
- GET `/api/dashboard/live-data` - dashboard snapshot
- WS `/api/dashboard/ws` - live dashboard stream

### Event Payload Notes

The add-in sends `eventType` as an integer enum value (see `src/DominoGovernanceTracker/Models/AuditEvent.cs`). Example:

```json
{
  "events": [
    {
      "eventId": "550e8400-e29b-41d4-a716-446655440000",
      "timestamp": "2025-01-15T10:30:00Z",
      "eventType": 6,
      "userName": "john.doe",
      "sessionId": "sess_123456",
      "workbookName": "Report.xlsx",
      "cellAddress": "$A$1",
      "newValue": "100"
    }
  ]
}
```

## Configuration

See `.env.example` for all options. Common settings:

- `DATABASE_URL` (default: `sqlite+aiosqlite:///./data/dgt.db`)
- `MAX_BATCH_SIZE` (default: 1000)
- `EVENT_RETENTION_DAYS` (default: 90)
- `ENABLE_BACKGROUND_TASKS` (default: True)
- `API_KEY` (optional)
- `CORS_ORIGINS` (comma-separated)

## Database Notes

- SQLite: tables are created automatically on startup.
- PostgreSQL: schema management is not wired to Alembic yet. Use your own migration workflow if running Postgres.

## Docker (PostgreSQL)

```bash
docker-compose up -d
docker-compose logs -f backend
docker-compose down
```

## Tests

```bash
pip install pytest pytest-asyncio httpx faker
pytest
```

## Project Structure

```
backend/
  api/                  # FastAPI route handlers
  infrastructure/       # Logging and database setup
  models/               # ORM and Pydantic schemas
  repositories/         # Data access layer
  services/             # Business logic and background jobs
  static/               # Dashboard UI
  main.py               # FastAPI app
  run_dev.py            # Dev server with reload
  run_app.py            # Production-style runner
```

## Notes

- Domino integration is supported via `fastapi-proxy` when running on Domino.
- Use `/dashboard` for the live monitoring UI.
