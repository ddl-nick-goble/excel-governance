# Domino Governance Tracker (DGT) Backend API Specification

**Version:** 1.0
**Last Updated:** 2025-12-14
**Purpose:** Complete specification for building a production-ready backend API that receives audit events from the DGT Excel-DNA add-in

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [OpenAPI 3.0 Specification](#openapi-30-specification)
3. [Data Models](#data-models)
4. [Database Schema](#database-schema)
5. [Implementation Requirements](#implementation-requirements)
6. [Quality Standards](#quality-standards)
7. [Technology Stack Recommendations](#technology-stack-recommendations)
8. [Example Implementations](#example-implementations)
9. [Testing Requirements](#testing-requirements)
10. [Monitoring & Observability](#monitoring--observability)
11. [Security Requirements](#security-requirements)
12. [API Contract Examples](#api-contract-examples)

---

## Executive Summary

The DGT Backend API is a high-reliability REST API that receives audit events from Excel-DNA add-in clients. It must handle:

- **Volume:** Up to 100 events per request, every 10 seconds per client
- **Reliability:** 99.9% uptime target, self-healing client expects eventual consistency
- **Resilience:** Client implements exponential backoff retry (3 attempts) and circuit breaker (50% failure rate)
- **Performance:** Response time <1s for batches of 100 events
- **Data Integrity:** Zero data loss - all events must be stored or client will buffer locally

### Key Characteristics

- **Single Primary Endpoint:** `POST /api/events`
- **Authentication:** API Key via `X-API-Key` header
- **Data Format:** JSON array of AuditEvent objects
- **Success Criteria:** Any HTTP 2xx status code
- **Idempotency:** Required - duplicate eventIds should not create duplicate records
- **Health Check:** `GET /health` endpoint for client monitoring

---

## OpenAPI 3.0 Specification

```yaml
openapi: 3.0.3
info:
  title: Domino Governance Tracker API
  description: |
    REST API for receiving audit events from DGT Excel add-in clients.
    Supports batched event ingestion with idempotency and resilience patterns.
  version: 1.0.0
  contact:
    name: API Support
    email: support@example.com

servers:
  - url: https://api.example.com
    description: Production server
  - url: https://staging-api.example.com
    description: Staging server
  - url: http://localhost:5000
    description: Local development server

security:
  - ApiKeyAuth: []

paths:
  /api/events:
    post:
      summary: Receive batch of audit events
      description: |
        Accepts batches of audit events from Excel add-in clients.
        Events are deduplicated by eventId to ensure idempotency.
        Maximum batch size is 100 events per request.
      operationId: receiveEvents
      tags:
        - Events
      security:
        - ApiKeyAuth: []
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: array
              minItems: 1
              maxItems: 100
              items:
                $ref: '#/components/schemas/AuditEvent'
            examples:
              singleCellChange:
                summary: Single cell change event
                value:
                  - eventId: "f47ac10b-58cc-4372-a567-0e02b2c3d479"
                    timestamp: "2025-12-14T15:30:45.123Z"
                    eventType: "CellChange"
                    userName: "john.doe"
                    machineName: "DESKTOP-ABC123"
                    userDomain: "CORPORATE"
                    sessionId: "abc123def456"
                    workbookName: "Budget.xlsx"
                    workbookPath: "C:\\Users\\john.doe\\Documents\\Budget.xlsx"
                    sheetName: "Sheet1"
                    cellAddress: "$A$1"
                    cellCount: 1
                    oldValue: "100"
                    newValue: "200"
                    formula: "=B1*2"
              bulkOperation:
                summary: Bulk operation (paste/fill)
                value:
                  - eventId: "g58bd21c-69dd-5483-b678-1f13c3d4e580"
                    timestamp: "2025-12-14T15:31:00.456Z"
                    eventType: "CellChange"
                    userName: "john.doe"
                    machineName: "DESKTOP-ABC123"
                    userDomain: "CORPORATE"
                    sessionId: "abc123def456"
                    workbookName: "Budget.xlsx"
                    workbookPath: "C:\\Users\\john.doe\\Documents\\Budget.xlsx"
                    sheetName: "Sheet1"
                    cellAddress: "$A$1:$Z$100"
                    cellCount: 2600
                    details: "BulkOperation:2600 cells changed"
              batchOfEvents:
                summary: Batch of mixed events
                value:
                  - eventId: "event-001"
                    timestamp: "2025-12-14T09:00:00.000Z"
                    eventType: "SessionStart"
                    userName: "john.doe"
                    machineName: "DESKTOP-ABC123"
                    userDomain: "CORPORATE"
                    sessionId: "session-001"
                    details: "DGT tracking started"
                  - eventId: "event-002"
                    timestamp: "2025-12-14T09:00:15.000Z"
                    eventType: "WorkbookOpen"
                    userName: "john.doe"
                    machineName: "DESKTOP-ABC123"
                    userDomain: "CORPORATE"
                    sessionId: "session-001"
                    workbookName: "Budget.xlsx"
                    workbookPath: "C:\\Users\\john.doe\\Documents\\Budget.xlsx"
                  - eventId: "event-003"
                    timestamp: "2025-12-14T09:00:30.000Z"
                    eventType: "CellChange"
                    userName: "john.doe"
                    machineName: "DESKTOP-ABC123"
                    userDomain: "CORPORATE"
                    sessionId: "session-001"
                    workbookName: "Budget.xlsx"
                    workbookPath: "C:\\Users\\john.doe\\Documents\\Budget.xlsx"
                    sheetName: "Sheet1"
                    cellAddress: "$A$1"
                    cellCount: 1
                    oldValue: "100"
                    newValue: "200"
      responses:
        '200':
          description: Events successfully received and stored
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/EventsResponse'
              example:
                received: 42
                stored: 40
                duplicates: 2
        '201':
          description: Events successfully created
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/EventsResponse'
        '202':
          description: Events accepted for processing
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/EventsResponse'
        '400':
          description: Invalid request (malformed JSON, missing required fields, batch too large)
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ErrorResponse'
              examples:
                emptyBatch:
                  summary: Empty batch
                  value:
                    error: "No events provided"
                    code: "EMPTY_BATCH"
                batchTooLarge:
                  summary: Batch exceeds maximum size
                  value:
                    error: "Batch size exceeds maximum (100)"
                    code: "BATCH_TOO_LARGE"
                missingFields:
                  summary: Missing required fields
                  value:
                    error: "Missing required field: eventId"
                    code: "VALIDATION_ERROR"
        '401':
          description: Authentication failed (missing or invalid API key)
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ErrorResponse'
              example:
                error: "Invalid or missing API key"
                code: "UNAUTHORIZED"
        '429':
          description: Rate limit exceeded
          headers:
            X-RateLimit-Limit:
              schema:
                type: integer
              description: Request limit per time window
            X-RateLimit-Remaining:
              schema:
                type: integer
              description: Remaining requests in current window
            X-RateLimit-Reset:
              schema:
                type: integer
              description: Unix timestamp when limit resets
            Retry-After:
              schema:
                type: integer
              description: Seconds to wait before retrying
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ErrorResponse'
              example:
                error: "Rate limit exceeded"
                code: "RATE_LIMIT_EXCEEDED"
        '500':
          description: Internal server error (database failure, etc.)
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ErrorResponse'
              example:
                error: "Internal server error"
                code: "INTERNAL_ERROR"
        '503':
          description: Service unavailable (maintenance, database down)
          headers:
            Retry-After:
              schema:
                type: integer
              description: Seconds to wait before retrying
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/ErrorResponse'
              example:
                error: "Service temporarily unavailable"
                code: "SERVICE_UNAVAILABLE"

    head:
      summary: Check API availability
      description: |
        Lightweight endpoint to check if the API is available.
        Used as fallback when /health returns 404.
      operationId: checkApiAvailability
      tags:
        - Health
      security:
        - ApiKeyAuth: []
      responses:
        '200':
          description: API is available
        '401':
          description: Authentication failed
        '503':
          description: Service unavailable

  /health:
    get:
      summary: Health check endpoint
      description: |
        Returns health status of the API and its dependencies.
        Called by Excel add-in health monitor every 30 seconds.
      operationId: healthCheck
      tags:
        - Health
      security: []  # No authentication required for health checks
      responses:
        '200':
          description: Service is healthy
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/HealthResponse'
              example:
                status: "healthy"
                timestamp: "2025-12-14T15:30:45.123Z"
                checks:
                  database: "healthy"
                  storage: "healthy"
        '503':
          description: Service is unhealthy
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/HealthResponse'
              example:
                status: "unhealthy"
                timestamp: "2025-12-14T15:30:45.123Z"
                checks:
                  database: "unhealthy"
                  storage: "healthy"

components:
  securitySchemes:
    ApiKeyAuth:
      type: apiKey
      in: header
      name: X-API-Key
      description: API key for authentication

  schemas:
    AuditEvent:
      type: object
      required:
        - eventId
        - timestamp
        - eventType
        - userName
        - machineName
        - userDomain
        - sessionId
      properties:
        eventId:
          type: string
          format: uuid
          description: Unique identifier for the event (used for idempotency)
          example: "f47ac10b-58cc-4372-a567-0e02b2c3d479"
        timestamp:
          type: string
          format: date-time
          description: UTC timestamp when event occurred (ISO 8601 format)
          example: "2025-12-14T15:30:45.123Z"
        eventType:
          $ref: '#/components/schemas/AuditEventType'
        userName:
          type: string
          description: Windows username of the user
          example: "john.doe"
          maxLength: 255
        machineName:
          type: string
          description: Name of the machine where event occurred
          example: "DESKTOP-ABC123"
          maxLength: 255
        userDomain:
          type: string
          description: Windows domain of the user
          example: "CORPORATE"
          maxLength: 255
        sessionId:
          type: string
          description: Unique session identifier (generated when Excel starts)
          example: "abc123def456"
          maxLength: 255
        workbookName:
          type: string
          description: Name of the Excel workbook (required for workbook/cell events)
          example: "Budget.xlsx"
          maxLength: 500
          nullable: true
        workbookPath:
          type: string
          description: Full path to workbook (empty if unsaved, required for workbook/cell events)
          example: "C:\\Users\\john.doe\\Documents\\Budget.xlsx"
          maxLength: 1000
          nullable: true
        sheetName:
          type: string
          description: Name of the worksheet (required for cell/sheet events)
          example: "Sheet1"
          maxLength: 255
          nullable: true
        cellAddress:
          type: string
          description: |
            Cell address in Excel format (required for cell events).
            Single cell: $A$1
            Range: $A$1:$B$5
          example: "$A$1"
          maxLength: 255
          nullable: true
        cellCount:
          type: integer
          description: Number of cells affected (required for cell events)
          example: 1
          minimum: 0
          nullable: true
        oldValue:
          type: string
          description: Previous cell value (for CellChange events, may be null)
          example: "100"
          maxLength: 32767  # Excel cell max length
          nullable: true
        newValue:
          type: string
          description: New cell value (for CellChange events, may be null)
          example: "200"
          maxLength: 32767
          nullable: true
        formula:
          type: string
          description: Cell formula (if includeFormulas config is true)
          example: "=B1*2"
          maxLength: 8192  # Excel formula max length
          nullable: true
        details:
          type: string
          description: Additional event-specific information
          example: "BulkOperation:2600 cells changed"
          maxLength: 4000
          nullable: true
        errorMessage:
          type: string
          description: Error message (for Error event type)
          example: null
          maxLength: 4000
          nullable: true
        correlationId:
          type: string
          description: Correlation ID to link related events
          example: null
          maxLength: 255
          nullable: true

    AuditEventType:
      type: string
      description: Type of audit event
      enum:
        # Workbook events
        - WorkbookNew        # New workbook created
        - WorkbookOpen       # Workbook opened
        - WorkbookClose      # Workbook closed
        - WorkbookSave       # Workbook saved
        - WorkbookActivate   # Workbook activated (switched to)
        - WorkbookDeactivate # Workbook deactivated (switched away)
        # Cell/Sheet events
        - CellChange         # Cell value changed (MOST COMMON - 80%+ of events)
        - SelectionChange    # Cell selection changed (if enabled, can be noisy)
        - SheetAdd           # Sheet added to workbook
        - SheetDelete        # Sheet deleted
        - SheetRename        # Sheet renamed
        - SheetActivate      # Sheet activated
        # System events
        - SessionStart       # Excel session started
        - SessionEnd         # Excel session ended
        - AddInLoad          # Add-in loaded
        - AddInUnload        # Add-in unloaded
        - Error              # Error occurred

    EventsResponse:
      type: object
      required:
        - received
        - stored
      properties:
        received:
          type: integer
          description: Number of events received in the request
          example: 42
        stored:
          type: integer
          description: Number of events successfully stored (may be less than received due to duplicates)
          example: 40
        duplicates:
          type: integer
          description: Number of duplicate events detected (by eventId)
          example: 2

    ErrorResponse:
      type: object
      required:
        - error
      properties:
        error:
          type: string
          description: Human-readable error message
          example: "Invalid or missing API key"
        code:
          type: string
          description: Machine-readable error code
          example: "UNAUTHORIZED"
        details:
          type: object
          description: Additional error details (validation errors, etc.)
          nullable: true
        timestamp:
          type: string
          format: date-time
          description: When the error occurred
          example: "2025-12-14T15:30:45.123Z"

    HealthResponse:
      type: object
      required:
        - status
        - timestamp
      properties:
        status:
          type: string
          enum: [healthy, unhealthy, degraded]
          description: Overall health status
          example: "healthy"
        timestamp:
          type: string
          format: date-time
          description: When health check was performed
          example: "2025-12-14T15:30:45.123Z"
        checks:
          type: object
          description: Status of individual components
          properties:
            database:
              type: string
              enum: [healthy, unhealthy]
              example: "healthy"
            storage:
              type: string
              enum: [healthy, unhealthy]
              example: "healthy"
          additionalProperties:
            type: string
```

---

## Data Models

### AuditEvent Field Specifications

| Field | Type | Required | Max Length | Nullable | Description | Event Types |
|-------|------|----------|------------|----------|-------------|-------------|
| `eventId` | UUID | Yes | 36 | No | Unique identifier (for idempotency) | All |
| `timestamp` | DateTime | Yes | - | No | UTC timestamp (ISO 8601) | All |
| `eventType` | Enum | Yes | - | No | Type of event (see enum below) | All |
| `userName` | String | Yes | 255 | No | Windows username | All |
| `machineName` | String | Yes | 255 | No | Machine name | All |
| `userDomain` | String | Yes | 255 | No | User domain | All |
| `sessionId` | String | Yes | 255 | No | Excel session identifier | All |
| `workbookName` | String | Conditional | 500 | Yes | Workbook filename | Workbook/Cell/Sheet events |
| `workbookPath` | String | Conditional | 1000 | Yes | Full workbook path | Workbook/Cell/Sheet events |
| `sheetName` | String | Conditional | 255 | Yes | Worksheet name | Cell/Sheet events |
| `cellAddress` | String | Conditional | 255 | Yes | Cell address ($A$1 or $A$1:$B$5) | Cell events |
| `cellCount` | Integer | Conditional | - | Yes | Number of cells affected | Cell events |
| `oldValue` | String | Optional | 32767 | Yes | Previous cell value | CellChange events |
| `newValue` | String | Optional | 32767 | Yes | New cell value | CellChange events |
| `formula` | String | Optional | 8192 | Yes | Cell formula | CellChange events |
| `details` | String | Optional | 4000 | Yes | Additional information | Any |
| `errorMessage` | String | Optional | 4000 | Yes | Error message | Error events |
| `correlationId` | String | Optional | 255 | Yes | Link related events | Any |

### AuditEventType Enum Values

```
WorkbookNew         - New workbook created
WorkbookOpen        - Workbook opened
WorkbookClose       - Workbook closed
WorkbookSave        - Workbook saved
WorkbookActivate    - Workbook activated (switched to)
WorkbookDeactivate  - Workbook deactivated (switched away)
CellChange          - Cell value changed (MOST COMMON - 80%+ of events)
SelectionChange     - Cell selection changed (if enabled, can be noisy)
SheetAdd            - Sheet added to workbook
SheetDelete         - Sheet deleted
SheetRename         - Sheet renamed
SheetActivate       - Sheet activated
SessionStart        - Excel session started
SessionEnd          - Excel session ended
AddInLoad           - Add-in loaded
AddInUnload         - Add-in unloaded
Error               - Error occurred
```

### Data Volume Estimates

Based on typical usage patterns:

- **CellChange:** 80-90% of all events
- **WorkbookOpen/Close:** 5-10% of events
- **SessionStart/End:** <1% of events
- **Other events:** 5-10% of events

**Expected Volume per User:**
- Light user: 100-500 events/day
- Moderate user: 500-2,000 events/day
- Heavy user: 2,000-10,000 events/day

**Batch Characteristics:**
- Average batch size: 10-50 events
- Maximum batch size: 100 events (enforced by client)
- Batch frequency: Every 10 seconds (configurable)

---

## Database Schema

### Primary Table: `audit_events`

```sql
CREATE TABLE audit_events (
    -- Primary Key
    id BIGSERIAL PRIMARY KEY,

    -- Required Fields (from client)
    event_id UUID NOT NULL UNIQUE,  -- For idempotency
    timestamp TIMESTAMP WITH TIME ZONE NOT NULL,
    event_type VARCHAR(50) NOT NULL,
    user_name VARCHAR(255) NOT NULL,
    machine_name VARCHAR(255) NOT NULL,
    user_domain VARCHAR(255) NOT NULL,
    session_id VARCHAR(255) NOT NULL,

    -- Conditional Fields (workbook/cell events)
    workbook_name VARCHAR(500),
    workbook_path VARCHAR(1000),
    sheet_name VARCHAR(255),
    cell_address VARCHAR(255),
    cell_count INTEGER,

    -- Optional Fields (cell change details)
    old_value TEXT,  -- Can be up to 32KB
    new_value TEXT,
    formula VARCHAR(8192),

    -- Metadata Fields
    details TEXT,
    error_message TEXT,
    correlation_id VARCHAR(255),

    -- Server-side Fields
    received_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    processed BOOLEAN NOT NULL DEFAULT FALSE,

    -- Indexes (defined below)
    CONSTRAINT valid_event_type CHECK (event_type IN (
        'WorkbookNew', 'WorkbookOpen', 'WorkbookClose', 'WorkbookSave',
        'WorkbookActivate', 'WorkbookDeactivate', 'CellChange',
        'SelectionChange', 'SheetAdd', 'SheetDelete', 'SheetRename',
        'SheetActivate', 'SessionStart', 'SessionEnd', 'AddInLoad',
        'AddInUnload', 'Error'
    ))
);

-- Critical Indexes for Performance
CREATE UNIQUE INDEX idx_events_event_id ON audit_events(event_id);
CREATE INDEX idx_events_timestamp ON audit_events(timestamp DESC);
CREATE INDEX idx_events_user_name ON audit_events(user_name);
CREATE INDEX idx_events_event_type ON audit_events(event_type);
CREATE INDEX idx_events_session_id ON audit_events(session_id);
CREATE INDEX idx_events_workbook_path ON audit_events(workbook_path) WHERE workbook_path IS NOT NULL;
CREATE INDEX idx_events_received_at ON audit_events(received_at DESC);

-- Composite Indexes for Common Queries
CREATE INDEX idx_events_user_timestamp ON audit_events(user_name, timestamp DESC);
CREATE INDEX idx_events_workbook_timestamp ON audit_events(workbook_path, timestamp DESC) WHERE workbook_path IS NOT NULL;
CREATE INDEX idx_events_session_timestamp ON audit_events(session_id, timestamp DESC);

-- Partial Index for Unprocessed Events (if async processing used)
CREATE INDEX idx_events_unprocessed ON audit_events(received_at) WHERE processed = FALSE;
```

### Partitioning Strategy (Optional but Recommended)

For high-volume deployments, partition by timestamp:

```sql
-- PostgreSQL 12+ declarative partitioning
CREATE TABLE audit_events (
    -- ... same columns as above ...
) PARTITION BY RANGE (timestamp);

-- Create monthly partitions
CREATE TABLE audit_events_2025_01 PARTITION OF audit_events
    FOR VALUES FROM ('2025-01-01') TO ('2025-02-01');

CREATE TABLE audit_events_2025_02 PARTITION OF audit_events
    FOR VALUES FROM ('2025-02-01') TO ('2025-03-01');

-- Automated partition management recommended (pg_partman, etc.)
```

### Retention Policy

Implement data retention based on business requirements:

```sql
-- Delete events older than 2 years (example)
DELETE FROM audit_events
WHERE timestamp < NOW() - INTERVAL '2 years';

-- Or archive to cold storage
INSERT INTO audit_events_archive
SELECT * FROM audit_events
WHERE timestamp < NOW() - INTERVAL '1 year';

DELETE FROM audit_events
WHERE timestamp < NOW() - INTERVAL '1 year';
```

### Supporting Tables (Optional)

**Session tracking:**
```sql
CREATE TABLE sessions (
    session_id VARCHAR(255) PRIMARY KEY,
    user_name VARCHAR(255) NOT NULL,
    machine_name VARCHAR(255) NOT NULL,
    started_at TIMESTAMP WITH TIME ZONE NOT NULL,
    ended_at TIMESTAMP WITH TIME ZONE,
    event_count INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_sessions_user ON sessions(user_name);
CREATE INDEX idx_sessions_started ON sessions(started_at DESC);
```

**API key management:**
```sql
CREATE TABLE api_keys (
    id SERIAL PRIMARY KEY,
    key_hash VARCHAR(255) NOT NULL UNIQUE,  -- Hashed API key
    description VARCHAR(500),
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMP WITH TIME ZONE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    rate_limit_per_minute INTEGER DEFAULT 600  -- 100 events/10s = 600/min
);

CREATE INDEX idx_api_keys_hash ON api_keys(key_hash) WHERE is_active = TRUE;
```

---

## Implementation Requirements

### 1. Idempotency

**CRITICAL:** The API MUST handle duplicate events gracefully.

**Implementation:**
- Use `eventId` (UUID) as unique constraint
- On duplicate `eventId`, return 200 OK (don't error)
- Track duplicates in response for observability

**Example logic:**
```python
def store_events(events):
    stored = 0
    duplicates = 0

    for event in events:
        try:
            db.insert(event)
            stored += 1
        except UniqueViolationError:  # eventId already exists
            duplicates += 1
            # Don't error - this is expected behavior

    return {
        "received": len(events),
        "stored": stored,
        "duplicates": duplicates
    }
```

**Why it matters:**
- Client retries on transient failures
- Network issues can cause duplicate sends
- Circuit breaker recovery may re-send batches

### 2. Batch Processing

**Requirements:**
- Accept batches of 1-100 events per request
- Process batches transactionally (all-or-nothing preferred, but not required)
- Return success if ANY events were stored (even if some duplicates)

**Performance target:**
- Process 100-event batch in <1 second
- Database insert performance is critical

**Optimization strategies:**
- Use bulk insert (INSERT INTO ... VALUES (...), (...), ...)
- Consider batch size limits per database insert
- Use connection pooling
- Consider async processing for very large batches

### 3. Resilience Expectations

The client implements robust resilience patterns. Your API should:

**Support client retry logic:**
- Return 5xx for transient errors (database down, etc.)
- Return 4xx for permanent errors (bad request, auth failure)
- Client will retry 5xx errors with exponential backoff

**Circuit breaker awareness:**
- After 50% failure rate, client opens circuit breaker
- Circuit stays open for 60 seconds
- Health endpoint helps client detect recovery faster

**Timeout handling:**
- Client timeout: 30 seconds
- Your API should respond faster (<10s for 100 events)
- Consider async processing for slow operations

### 4. Authentication

**API Key Header:** `X-API-Key: <api-key>`

**Implementation:**
- Validate on every request (except /health)
- Use constant-time comparison to prevent timing attacks
- Hash API keys in database (don't store plain text)
- Support key rotation without downtime
- Return 401 Unauthorized if missing/invalid

**Example:**
```python
def validate_api_key(provided_key):
    # Constant-time comparison
    stored_hash = db.get_api_key_hash(provided_key)
    if not stored_hash:
        return False

    return secrets.compare_digest(
        hash_api_key(provided_key),
        stored_hash
    )
```

### 5. Rate Limiting (Optional but Recommended)

**Suggested limits:**
- 600 events/minute per API key (100 events/10s * 6)
- 1000 requests/hour per API key
- Burst allowance: 200 events in 10 seconds

**Headers to include:**
```
X-RateLimit-Limit: 600
X-RateLimit-Remaining: 542
X-RateLimit-Reset: 1702567890
Retry-After: 30
```

**Response when exceeded:**
- HTTP 429 Too Many Requests
- Include Retry-After header
- Client will respect and buffer locally

### 6. Health Check

**Endpoint:** `GET /health`

**Requirements:**
- No authentication required
- Fast response (<100ms)
- Check critical dependencies (database, storage)
- Return 200 if healthy, 503 if unhealthy

**Implementation:**
```python
def health_check():
    checks = {
        "database": check_database_connectivity(),
        "storage": check_storage_available()
    }

    is_healthy = all(checks.values())
    status_code = 200 if is_healthy else 503

    return {
        "status": "healthy" if is_healthy else "unhealthy",
        "timestamp": datetime.utcnow().isoformat(),
        "checks": checks
    }, status_code
```

**Client behavior:**
- Checks every 30 seconds
- 5-second timeout
- Falls back to `HEAD /api/events` if /health returns 404

### 7. Validation

**Request validation:**
- Content-Type must be application/json
- Request body must be valid JSON array
- Batch size 1-100 events
- Each event must have required fields
- Field lengths must not exceed maximums
- eventType must be valid enum value
- timestamp must be valid ISO 8601 datetime

**Error responses:**
```json
{
  "error": "Missing required field: eventId",
  "code": "VALIDATION_ERROR",
  "details": {
    "field": "eventId",
    "index": 3
  },
  "timestamp": "2025-12-14T15:30:45.123Z"
}
```

### 8. Performance Requirements

**Response Times (95th percentile):**
- Single event: <100ms
- 10 events: <200ms
- 50 events: <500ms
- 100 events: <1000ms

**Throughput:**
- Support 100 concurrent clients
- Each client: 100 events/10s = 600 events/min
- Total: 6,000 events/min (360K events/hour)

**Database performance:**
- Use connection pooling (10-50 connections)
- Optimize bulk inserts
- Monitor query performance
- Index all common query patterns

### 9. Error Handling

**Error categories:**

1. **Client errors (4xx):**
   - 400 Bad Request: Invalid JSON, missing fields, batch too large
   - 401 Unauthorized: Missing/invalid API key
   - 429 Too Many Requests: Rate limit exceeded

2. **Server errors (5xx):**
   - 500 Internal Server Error: Unexpected errors
   - 503 Service Unavailable: Database down, maintenance

**Error response format:**
```json
{
  "error": "Human-readable error message",
  "code": "MACHINE_READABLE_CODE",
  "details": {},  // Optional additional context
  "timestamp": "2025-12-14T15:30:45.123Z"
}
```

**Logging requirements:**
- Log all 4xx errors at WARN level
- Log all 5xx errors at ERROR level
- Include request ID for tracing
- Include API key identifier (not the key itself)
- Include event count and sample eventIds

---

## Quality Standards

### Zero Bugs Approach

To achieve "zero bugs ever" reliability:

1. **Comprehensive Testing** (see Testing Requirements section)
2. **Static Analysis:** Use linters, type checkers, security scanners
3. **Code Review:** All changes reviewed by at least one other developer
4. **Monitoring:** Proactive alerts on errors, latency, availability
5. **Graceful Degradation:** Handle all error conditions gracefully
6. **Documentation:** Clear API docs, runbooks, troubleshooting guides

### Reliability Targets

- **Availability:** 99.9% uptime (8.76 hours downtime/year)
- **Data Integrity:** 100% (zero data loss)
- **Error Rate:** <0.1% of requests fail
- **P95 Latency:** <1 second for 100-event batches

### Error Budget

- **Monthly error budget:** 0.1% of requests
- **Example:** 1M requests/month = 1,000 errors allowed
- **Burn rate alert:** Alert if >50% budget consumed in 24 hours

### Data Integrity Guarantees

1. **No Data Loss:**
   - Database backups every 6 hours
   - Point-in-time recovery enabled
   - Replication to standby instance
   - Transaction logs archived

2. **Idempotency:**
   - Duplicate eventIds handled gracefully
   - No duplicate records in database

3. **Data Validation:**
   - All required fields present
   - Field types and lengths validated
   - Enum values validated
   - Timestamps in valid range

### Security Standards

1. **Authentication:** All endpoints except /health require valid API key
2. **Authorization:** API keys have specific permissions/rate limits
3. **Encryption:** HTTPS/TLS 1.2+ required in production
4. **Input Validation:** All inputs sanitized and validated
5. **SQL Injection Prevention:** Use parameterized queries
6. **Secrets Management:** API keys hashed, never logged
7. **Audit Logging:** All access logged with user/timestamp

---

## Technology Stack Recommendations

### Option 1: ASP.NET Core (C#)

**Pros:**
- Excellent performance (Kestrel server)
- Strong typing reduces bugs
- Native async/await support
- Entity Framework Core for database
- Built-in dependency injection
- Familiar to .NET developers (matches add-in)

**Recommended packages:**
- ASP.NET Core 8.0
- Entity Framework Core 8.0
- Serilog (logging)
- Swashbuckle (Swagger/OpenAPI)
- Polly (client-side resilience if calling other services)
- FluentValidation (request validation)

**Estimated lines of code:** 500-800 LOC

### Option 2: Node.js + Express (TypeScript)

**Pros:**
- Fast development
- Large ecosystem (npm)
- Good performance with async I/O
- TypeScript adds type safety
- Easy deployment

**Recommended packages:**
- Express 4.x
- TypeScript 5.x
- Prisma or TypeORM (database)
- Winston (logging)
- express-rate-limit (rate limiting)
- express-validator (validation)

**Estimated lines of code:** 400-600 LOC

### Option 3: Python + FastAPI

**Pros:**
- Extremely fast development
- Auto-generated OpenAPI docs
- Excellent async support
- Type hints with Pydantic
- Great for data processing/analytics

**Recommended packages:**
- FastAPI 0.104+
- SQLAlchemy 2.0 (database)
- Pydantic 2.0 (validation)
- Uvicorn (ASGI server)
- python-jose (API key validation)

**Estimated lines of code:** 300-500 LOC

### Database Recommendations

**Primary recommendation: PostgreSQL 14+**
- Excellent JSON support (for flexible schema)
- Robust ACID guarantees
- Partitioning support
- Great performance
- Free and open source

**Alternative: SQL Server**
- Good fit if already using Microsoft stack
- Excellent .NET integration
- Enterprise features (Always On, etc.)

**Alternative: MongoDB**
- Flexible schema (good for varying event types)
- Good write performance
- Horizontal scaling
- Less suitable for complex queries

### Hosting Recommendations

**Cloud options:**
- **AWS:** ECS/Fargate + RDS PostgreSQL
- **Azure:** App Service + Azure Database for PostgreSQL
- **GCP:** Cloud Run + Cloud SQL
- **Heroku:** Quick deployment, good for prototyping

**Self-hosted:**
- Docker + Docker Compose
- Kubernetes for high availability
- Reverse proxy (nginx, Traefik)

---

## Example Implementations

### ASP.NET Core Implementation

**Program.cs:**
```csharp
using Microsoft.EntityFrameworkCore;
using DgtApi.Services;
using DgtApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
builder.Services.AddDbContext<DgtDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Services
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddSingleton<IApiKeyValidator, ApiKeyValidator>();

// Logging
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.AddDebug();
});

var app = builder.Build();

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
```

**EventsController.cs:**
```csharp
using Microsoft.AspNetCore.Mvc;
using DgtApi.Models;
using DgtApi.Services;

namespace DgtApi.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly IEventRepository _repository;
    private readonly IApiKeyValidator _apiKeyValidator;
    private readonly ILogger<EventsController> _logger;

    public EventsController(
        IEventRepository repository,
        IApiKeyValidator apiKeyValidator,
        ILogger<EventsController> logger)
    {
        _repository = repository;
        _apiKeyValidator = apiKeyValidator;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> ReceiveEvents(
        [FromHeader(Name = "X-API-Key")] string? apiKey,
        [FromBody] List<AuditEvent> events)
    {
        // Validate API key
        if (string.IsNullOrWhiteSpace(apiKey) || !await _apiKeyValidator.ValidateAsync(apiKey))
        {
            _logger.LogWarning("Invalid or missing API key");
            return Unauthorized(new ErrorResponse
            {
                Error = "Invalid or missing API key",
                Code = "UNAUTHORIZED",
                Timestamp = DateTime.UtcNow
            });
        }

        // Validate request
        if (events == null || events.Count == 0)
        {
            _logger.LogWarning("Empty event batch received");
            return BadRequest(new ErrorResponse
            {
                Error = "No events provided",
                Code = "EMPTY_BATCH",
                Timestamp = DateTime.UtcNow
            });
        }

        if (events.Count > 100)
        {
            _logger.LogWarning("Batch size {Count} exceeds maximum (100)", events.Count);
            return BadRequest(new ErrorResponse
            {
                Error = "Batch size exceeds maximum (100)",
                Code = "BATCH_TOO_LARGE",
                Timestamp = DateTime.UtcNow
            });
        }

        try
        {
            // Store events (with deduplication)
            var result = await _repository.StoreEventsAsync(events);

            _logger.LogInformation(
                "Received {Received} events, stored {Stored}, duplicates {Duplicates}",
                result.Received, result.Stored, result.Duplicates);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing events");
            return StatusCode(500, new ErrorResponse
            {
                Error = "Internal server error",
                Code = "INTERNAL_ERROR",
                Timestamp = DateTime.UtcNow
            });
        }
    }

    [HttpHead]
    public IActionResult CheckAvailability(
        [FromHeader(Name = "X-API-Key")] string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !_apiKeyValidator.Validate(apiKey))
        {
            return Unauthorized();
        }

        return Ok();
    }
}
```

**HealthController.cs:**
```csharp
using Microsoft.AspNetCore.Mvc;
using DgtApi.Services;

namespace DgtApi.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly IEventRepository _repository;
    private readonly ILogger<HealthController> _logger;

    public HealthController(IEventRepository repository, ILogger<HealthController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> HealthCheck()
    {
        var checks = new Dictionary<string, string>();

        // Check database
        try
        {
            var dbHealthy = await _repository.IsHealthyAsync();
            checks["database"] = dbHealthy ? "healthy" : "unhealthy";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            checks["database"] = "unhealthy";
        }

        var isHealthy = checks.Values.All(v => v == "healthy");
        var statusCode = isHealthy ? 200 : 503;

        return StatusCode(statusCode, new HealthResponse
        {
            Status = isHealthy ? "healthy" : "unhealthy",
            Timestamp = DateTime.UtcNow,
            Checks = checks
        });
    }
}
```

**EventRepository.cs:**
```csharp
using Microsoft.EntityFrameworkCore;
using DgtApi.Models;

namespace DgtApi.Services;

public interface IEventRepository
{
    Task<EventsResponse> StoreEventsAsync(List<AuditEvent> events);
    Task<bool> IsHealthyAsync();
}

public class EventRepository : IEventRepository
{
    private readonly DgtDbContext _context;
    private readonly ILogger<EventRepository> _logger;

    public EventRepository(DgtDbContext context, ILogger<EventRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<EventsResponse> StoreEventsAsync(List<AuditEvent> events)
    {
        var stored = 0;
        var duplicates = 0;

        // Use transaction for batch insert
        using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            foreach (var evt in events)
            {
                // Set server-side timestamp
                evt.ReceivedAt = DateTime.UtcNow;

                try
                {
                    await _context.AuditEvents.AddAsync(evt);
                    await _context.SaveChangesAsync();
                    stored++;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // Duplicate eventId - not an error
                    duplicates++;
                    _logger.LogDebug("Duplicate event detected: {EventId}", evt.EventId);
                }
            }

            await transaction.CommitAsync();

            return new EventsResponse
            {
                Received = events.Count,
                Stored = stored,
                Duplicates = duplicates
            };
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to store events");
            throw;
        }
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            // Simple query to test database connectivity
            await _context.Database.CanConnectAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsUniqueViolation(DbUpdateException ex)
    {
        // PostgreSQL unique violation error code
        return ex.InnerException?.Message?.Contains("duplicate key") ?? false;
    }
}
```

**Models/AuditEvent.cs:**
```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DgtApi.Models;

[Table("audit_events")]
public class AuditEvent
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required]
    [Column("event_id")]
    public Guid EventId { get; set; }

    [Required]
    [Column("timestamp")]
    public DateTime Timestamp { get; set; }

    [Required]
    [MaxLength(50)]
    [Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("user_name")]
    public string UserName { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("machine_name")]
    public string MachineName { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("user_domain")]
    public string UserDomain { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(500)]
    [Column("workbook_name")]
    public string? WorkbookName { get; set; }

    [MaxLength(1000)]
    [Column("workbook_path")]
    public string? WorkbookPath { get; set; }

    [MaxLength(255)]
    [Column("sheet_name")]
    public string? SheetName { get; set; }

    [MaxLength(255)]
    [Column("cell_address")]
    public string? CellAddress { get; set; }

    [Column("cell_count")]
    public int? CellCount { get; set; }

    [Column("old_value")]
    public string? OldValue { get; set; }

    [Column("new_value")]
    public string? NewValue { get; set; }

    [MaxLength(8192)]
    [Column("formula")]
    public string? Formula { get; set; }

    [Column("details")]
    public string? Details { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [MaxLength(255)]
    [Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [Column("received_at")]
    public DateTime ReceivedAt { get; set; }

    [Column("processed")]
    public bool Processed { get; set; } = false;
}
```

### Python FastAPI Implementation

**main.py:**
```python
from fastapi import FastAPI, Header, HTTPException, status
from fastapi.responses import JSONResponse
from typing import List, Optional
from pydantic import BaseModel, Field, validator
from datetime import datetime
from enum import Enum
import logging

# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = FastAPI(
    title="Domino Governance Tracker API",
    description="REST API for receiving audit events from DGT Excel add-in",
    version="1.0.0"
)

# Models
class AuditEventType(str, Enum):
    WORKBOOK_NEW = "WorkbookNew"
    WORKBOOK_OPEN = "WorkbookOpen"
    WORKBOOK_CLOSE = "WorkbookClose"
    WORKBOOK_SAVE = "WorkbookSave"
    WORKBOOK_ACTIVATE = "WorkbookActivate"
    WORKBOOK_DEACTIVATE = "WorkbookDeactivate"
    CELL_CHANGE = "CellChange"
    SELECTION_CHANGE = "SelectionChange"
    SHEET_ADD = "SheetAdd"
    SHEET_DELETE = "SheetDelete"
    SHEET_RENAME = "SheetRename"
    SHEET_ACTIVATE = "SheetActivate"
    SESSION_START = "SessionStart"
    SESSION_END = "SessionEnd"
    ADDIN_LOAD = "AddInLoad"
    ADDIN_UNLOAD = "AddInUnload"
    ERROR = "Error"

class AuditEvent(BaseModel):
    eventId: str = Field(..., description="Unique event identifier")
    timestamp: datetime = Field(..., description="UTC timestamp")
    eventType: AuditEventType
    userName: str = Field(..., max_length=255)
    machineName: str = Field(..., max_length=255)
    userDomain: str = Field(..., max_length=255)
    sessionId: str = Field(..., max_length=255)
    workbookName: Optional[str] = Field(None, max_length=500)
    workbookPath: Optional[str] = Field(None, max_length=1000)
    sheetName: Optional[str] = Field(None, max_length=255)
    cellAddress: Optional[str] = Field(None, max_length=255)
    cellCount: Optional[int] = Field(None, ge=0)
    oldValue: Optional[str] = Field(None, max_length=32767)
    newValue: Optional[str] = Field(None, max_length=32767)
    formula: Optional[str] = Field(None, max_length=8192)
    details: Optional[str] = Field(None, max_length=4000)
    errorMessage: Optional[str] = Field(None, max_length=4000)
    correlationId: Optional[str] = Field(None, max_length=255)

    class Config:
        use_enum_values = True

class EventsResponse(BaseModel):
    received: int
    stored: int
    duplicates: int = 0

class ErrorResponse(BaseModel):
    error: str
    code: str
    details: Optional[dict] = None
    timestamp: datetime = Field(default_factory=datetime.utcnow)

class HealthResponse(BaseModel):
    status: str
    timestamp: datetime = Field(default_factory=datetime.utcnow)
    checks: dict

# Dependency injection
async def validate_api_key(x_api_key: Optional[str] = Header(None)) -> str:
    """Validate API key from header"""
    if not x_api_key:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail={"error": "Missing API key", "code": "UNAUTHORIZED"}
        )

    # TODO: Implement actual validation against database
    if not is_valid_api_key(x_api_key):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail={"error": "Invalid API key", "code": "UNAUTHORIZED"}
        )

    return x_api_key

def is_valid_api_key(api_key: str) -> bool:
    """Validate API key against database"""
    # TODO: Implement actual database lookup
    return len(api_key) > 0

# Endpoints
@app.post("/api/events", response_model=EventsResponse)
async def receive_events(
    events: List[AuditEvent],
    api_key: str = Depends(validate_api_key)
):
    """Receive batch of audit events"""

    # Validate batch size
    if len(events) == 0:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"error": "No events provided", "code": "EMPTY_BATCH"}
        )

    if len(events) > 100:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={"error": "Batch size exceeds maximum (100)", "code": "BATCH_TOO_LARGE"}
        )

    try:
        # Store events with deduplication
        result = await store_events(events)

        logger.info(
            f"Received {result['received']} events, "
            f"stored {result['stored']}, "
            f"duplicates {result['duplicates']}"
        )

        return result
    except Exception as ex:
        logger.error(f"Error storing events: {ex}")
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail={"error": "Internal server error", "code": "INTERNAL_ERROR"}
        )

@app.head("/api/events")
async def check_availability(api_key: str = Depends(validate_api_key)):
    """Check API availability"""
    return JSONResponse(content={}, status_code=200)

@app.get("/health", response_model=HealthResponse)
async def health_check():
    """Health check endpoint"""
    checks = {
        "database": await check_database_health(),
        "storage": "healthy"  # Add actual storage check if needed
    }

    is_healthy = all(v == "healthy" for v in checks.values())
    status_code = 200 if is_healthy else 503

    return JSONResponse(
        content={
            "status": "healthy" if is_healthy else "unhealthy",
            "timestamp": datetime.utcnow().isoformat(),
            "checks": checks
        },
        status_code=status_code
    )

# Database functions
async def store_events(events: List[AuditEvent]) -> dict:
    """Store events in database with deduplication"""
    # TODO: Implement actual database storage
    stored = 0
    duplicates = 0

    for event in events:
        try:
            # Attempt to insert event
            # If eventId exists, catch unique violation
            stored += 1
        except UniqueViolationError:
            duplicates += 1

    return {
        "received": len(events),
        "stored": stored,
        "duplicates": duplicates
    }

async def check_database_health() -> str:
    """Check database connectivity"""
    try:
        # TODO: Implement actual database ping
        return "healthy"
    except Exception:
        return "unhealthy"

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=5000)
```

---

## Testing Requirements

### Unit Tests

**Required test coverage: >90%**

Test categories:

1. **Request Validation Tests:**
   - Empty batch returns 400
   - Batch >100 events returns 400
   - Missing required fields returns 400
   - Invalid eventType returns 400
   - Field length violations return 400
   - Valid requests return 200

2. **Authentication Tests:**
   - Missing API key returns 401
   - Invalid API key returns 401
   - Valid API key returns 200
   - Expired API key returns 401

3. **Idempotency Tests:**
   - Duplicate eventId doesn't create duplicate records
   - Duplicate eventId returns 200 (not error)
   - Duplicate count is correct in response

4. **Error Handling Tests:**
   - Database connection failure returns 500
   - Transaction rollback on partial failure
   - Proper error response format

5. **Health Check Tests:**
   - Healthy dependencies return 200
   - Unhealthy database returns 503
   - Health check doesn't require auth

### Integration Tests

**Required:**

1. **End-to-End Flow:**
   - Send batch of events
   - Verify all stored in database
   - Verify correct indexes used
   - Verify query performance

2. **Database Tests:**
   - Connection pooling works correctly
   - Transactions commit/rollback properly
   - Indexes improve query performance
   - Partitioning works (if used)

3. **Authentication Tests:**
   - API key validation against real database
   - Key rotation doesn't break existing keys
   - Expired keys are rejected

4. **Resilience Tests:**
   - Retry on transient database errors
   - Timeout handling
   - Connection pool exhaustion recovery

### Load Tests

**Required performance validation:**

1. **Sustained Load:**
   - 100 concurrent clients
   - 100 events per 10 seconds per client
   - Run for 1 hour
   - Verify <1s p95 latency
   - Verify <0.1% error rate

2. **Burst Load:**
   - 500 concurrent clients
   - 100 events each
   - Verify system recovers
   - Verify no data loss

3. **Spike Test:**
   - Gradual ramp from 10 to 500 clients
   - Verify graceful degradation
   - Verify rate limiting works

**Tools:**
- Apache JMeter
- k6
- Locust
- Azure Load Testing

### Example Test (xUnit + C#):

```csharp
[Fact]
public async Task ReceiveEvents_ValidBatch_Returns200()
{
    // Arrange
    var events = new List<AuditEvent>
    {
        new AuditEvent
        {
            EventId = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            EventType = "CellChange",
            UserName = "test.user",
            MachineName = "TEST-PC",
            UserDomain = "TEST",
            SessionId = "test-session"
        }
    };

    // Act
    var response = await _client.PostAsJsonAsync("/api/events", events);

    // Assert
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var result = await response.Content.ReadFromJsonAsync<EventsResponse>();
    Assert.Equal(1, result.Received);
    Assert.Equal(1, result.Stored);
    Assert.Equal(0, result.Duplicates);
}

[Fact]
public async Task ReceiveEvents_DuplicateEventId_ReturnsCorrectCounts()
{
    // Arrange
    var eventId = Guid.NewGuid();
    var events = new List<AuditEvent>
    {
        CreateTestEvent(eventId),
        CreateTestEvent(eventId)  // Duplicate
    };

    // Act
    var response = await _client.PostAsJsonAsync("/api/events", events);

    // Assert
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var result = await response.Content.ReadFromJsonAsync<EventsResponse>();
    Assert.Equal(2, result.Received);
    Assert.Equal(1, result.Stored);
    Assert.Equal(1, result.Duplicates);
}
```

---

## Monitoring & Observability

### Required Metrics

**Application metrics:**
- Request rate (requests/second)
- Error rate (errors/second, % of requests)
- Latency (p50, p95, p99)
- Events received (events/second)
- Events stored (events/second)
- Duplicate rate (% of events)
- Authentication failures (failures/second)

**Infrastructure metrics:**
- CPU utilization (%)
- Memory utilization (%)
- Network I/O (bytes/second)
- Disk I/O (IOPS, latency)

**Database metrics:**
- Connection pool usage (active connections)
- Query latency (p95, p99)
- Slow queries (>1 second)
- Deadlocks/conflicts
- Table size growth
- Index usage

### Logging Requirements

**Log levels:**
- DEBUG: Detailed diagnostic information
- INFO: Normal operations (request received, events stored)
- WARN: Unexpected but handled (duplicate events, auth failures)
- ERROR: Errors requiring investigation (database failures)

**Required log fields:**
- Timestamp (ISO 8601 UTC)
- Level (DEBUG, INFO, WARN, ERROR)
- Message
- Request ID (for tracing)
- API key identifier (not the key!)
- Event count
- Latency (ms)
- Error details (if applicable)

**Example log entry:**
```json
{
  "timestamp": "2025-12-14T15:30:45.123Z",
  "level": "INFO",
  "message": "Events received and stored",
  "requestId": "req-123456",
  "apiKeyId": "key-789",
  "eventsReceived": 42,
  "eventsStored": 40,
  "duplicates": 2,
  "latencyMs": 234
}
```

### Alerting Rules

**Critical alerts (page on-call):**
- Error rate >1% for 5 minutes
- P95 latency >5 seconds for 5 minutes
- API availability <99% for 5 minutes
- Database connection failures

**Warning alerts (email/Slack):**
- Error rate >0.5% for 10 minutes
- P95 latency >2 seconds for 10 minutes
- Duplicate rate >10% for 10 minutes
- CPU >80% for 15 minutes
- Memory >85% for 15 minutes
- Disk >90% full

**Informational alerts:**
- Large batch size (>50 events) received
- High request rate (>100 req/s)
- New API key used

### Dashboards

**Required dashboards:**

1. **Overview Dashboard:**
   - Request rate (1m, 5m, 1h)
   - Error rate
   - P95/P99 latency
   - Availability (uptime)

2. **Performance Dashboard:**
   - Latency percentiles over time
   - Throughput (events/second)
   - Database query performance
   - Resource utilization

3. **Errors Dashboard:**
   - Error breakdown by type (4xx vs 5xx)
   - Top error messages
   - Failed requests over time
   - Authentication failures

4. **Business Metrics:**
   - Total events stored (today, this week, this month)
   - Top users by event count
   - Top workbooks by event count
   - Event type distribution

### Tools

**Recommended:**
- **Prometheus + Grafana:** Metrics and dashboards
- **ELK Stack (Elasticsearch, Logstash, Kibana):** Log aggregation
- **Application Insights (Azure):** All-in-one APM
- **Datadog:** All-in-one monitoring
- **New Relic:** Application performance monitoring

---

## Security Requirements

### 1. HTTPS/TLS

**CRITICAL:** Use HTTPS in production (TLS 1.2 or higher)

**Why:**
- Cell values may contain PII/sensitive data
- API keys transmitted in headers
- Prevent man-in-the-middle attacks

**Configuration:**
- Disable TLS 1.0 and 1.1
- Use strong cipher suites only
- Enable HSTS (HTTP Strict Transport Security)
- Valid SSL certificate (not self-signed in production)

### 2. API Key Security

**Storage:**
- NEVER store API keys in plain text
- Hash with bcrypt, argon2, or similar
- Salt each key individually

**Validation:**
- Use constant-time comparison (prevent timing attacks)
- Rate limit authentication attempts
- Log all authentication failures

**Rotation:**
- Support multiple active keys per client
- Allow key rotation without downtime
- Expire old keys after grace period

**Example:**
```csharp
public bool ValidateApiKey(string providedKey)
{
    var hashedKey = HashApiKey(providedKey);
    var storedHash = _db.GetApiKeyHash(providedKey);

    if (string.IsNullOrEmpty(storedHash))
        return false;

    // Constant-time comparison
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(hashedKey),
        Encoding.UTF8.GetBytes(storedHash)
    );
}
```

### 3. Input Validation

**Validate all inputs:**
- Request body is valid JSON
- Content-Type is application/json
- All required fields present
- Field types correct (string, int, datetime, etc.)
- Field lengths within limits
- Enum values are valid
- No SQL injection attempts

**Sanitization:**
- Escape special characters before logging
- Don't trust client-provided data
- Use parameterized queries (prevent SQL injection)

### 4. Rate Limiting

**Per API key:**
- 600 events/minute (configurable)
- 1000 requests/hour
- Burst allowance: 200 events in 10 seconds

**Per IP address (optional):**
- 1000 requests/hour for unknown IPs
- Prevent brute-force API key guessing

### 5. Secrets Management

**API keys:**
- Generate cryptographically secure random keys (32+ bytes)
- Never log API keys (log key identifier only)
- Store in secure key vault (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault)

**Database credentials:**
- Use environment variables or key vault
- Never commit to source control
- Rotate regularly

### 6. Audit Logging

**Log security events:**
- All authentication attempts (success and failure)
- API key usage (which key, when, how many events)
- Rate limit violations
- Invalid requests
- Suspicious patterns

**Log retention:**
- Security logs: 1 year minimum
- Comply with regulatory requirements (GDPR, etc.)

### 7. CORS (if web clients)

**Configure CORS restrictively:**
```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("DgtPolicy", policy =>
    {
        policy.WithOrigins("https://trusted-domain.com")
              .AllowedHeaders("X-API-Key", "Content-Type")
              .AllowedMethods("POST", "HEAD")
              .DisallowCredentials();
    });
});
```

### 8. Denial of Service (DoS) Protection

**Mitigations:**
- Request size limits (max 1MB per request)
- Timeout all requests (30s max)
- Rate limiting (per API key and per IP)
- Connection limits
- Use reverse proxy (nginx, Cloudflare) for DDoS protection

### 9. Dependency Security

**Keep dependencies up to date:**
- Monitor for security vulnerabilities (Dependabot, Snyk)
- Update libraries regularly
- Scan for known CVEs
- Use only trusted packages

### 10. Data Privacy

**PII handling:**
- Cell values may contain PII (names, SSNs, etc.)
- Comply with GDPR, CCPA, etc.
- Implement data retention policies
- Support data deletion requests
- Encrypt at rest (database encryption)
- Encrypt in transit (HTTPS)

---

## API Contract Examples

### Example 1: Single Cell Change

**Request:**
```http
POST /api/events HTTP/1.1
Host: api.example.com
Content-Type: application/json
X-API-Key: your-api-key-here

[
  {
    "eventId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
    "timestamp": "2025-12-14T15:30:45.123Z",
    "eventType": "CellChange",
    "userName": "john.doe",
    "machineName": "DESKTOP-ABC123",
    "userDomain": "CORPORATE",
    "sessionId": "abc123def456",
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

**Response (Success):**
```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "received": 1,
  "stored": 1,
  "duplicates": 0
}
```

### Example 2: Bulk Operation

**Request:**
```http
POST /api/events HTTP/1.1
Host: api.example.com
Content-Type: application/json
X-API-Key: your-api-key-here

[
  {
    "eventId": "g58bd21c-69dd-5483-b678-1f13c3d4e580",
    "timestamp": "2025-12-14T15:31:00.456Z",
    "eventType": "CellChange",
    "userName": "john.doe",
    "machineName": "DESKTOP-ABC123",
    "userDomain": "CORPORATE",
    "sessionId": "abc123def456",
    "workbookName": "Budget.xlsx",
    "workbookPath": "C:\\Users\\john.doe\\Documents\\Budget.xlsx",
    "sheetName": "Sheet1",
    "cellAddress": "$A$1:$Z$100",
    "cellCount": 2600,
    "details": "BulkOperation:2600 cells changed"
  }
]
```

**Response:**
```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "received": 1,
  "stored": 1,
  "duplicates": 0
}
```

### Example 3: Session Start

**Request:**
```http
POST /api/events HTTP/1.1
Host: api.example.com
Content-Type: application/json
X-API-Key: your-api-key-here

[
  {
    "eventId": "h69ce32d-70ee-6594-c789-2f24d4e5f691",
    "timestamp": "2025-12-14T09:00:00.000Z",
    "eventType": "SessionStart",
    "userName": "john.doe",
    "machineName": "DESKTOP-ABC123",
    "userDomain": "CORPORATE",
    "sessionId": "session-20251214-090000",
    "details": "DGT tracking started"
  }
]
```

**Response:**
```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "received": 1,
  "stored": 1,
  "duplicates": 0
}
```

### Example 4: Mixed Batch

**Request:**
```http
POST /api/events HTTP/1.1
Host: api.example.com
Content-Type: application/json
X-API-Key: your-api-key-here

[
  {
    "eventId": "event-001",
    "timestamp": "2025-12-14T09:00:00.000Z",
    "eventType": "SessionStart",
    "userName": "john.doe",
    "machineName": "DESKTOP-ABC123",
    "userDomain": "CORPORATE",
    "sessionId": "session-001",
    "details": "DGT tracking started"
  },
  {
    "eventId": "event-002",
    "timestamp": "2025-12-14T09:00:15.000Z",
    "eventType": "WorkbookOpen",
    "userName": "john.doe",
    "machineName": "DESKTOP-ABC123",
    "userDomain": "CORPORATE",
    "sessionId": "session-001",
    "workbookName": "Budget.xlsx",
    "workbookPath": "C:\\Users\\john.doe\\Documents\\Budget.xlsx"
  },
  {
    "eventId": "event-003",
    "timestamp": "2025-12-14T09:00:30.000Z",
    "eventType": "CellChange",
    "userName": "john.doe",
    "machineName": "DESKTOP-ABC123",
    "userDomain": "CORPORATE",
    "sessionId": "session-001",
    "workbookName": "Budget.xlsx",
    "workbookPath": "C:\\Users\\john.doe\\Documents\\Budget.xlsx",
    "sheetName": "Sheet1",
    "cellAddress": "$A$1",
    "cellCount": 1,
    "oldValue": "100",
    "newValue": "200"
  }
]
```

**Response:**
```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "received": 3,
  "stored": 3,
  "duplicates": 0
}
```

### Example 5: Error - Missing API Key

**Request:**
```http
POST /api/events HTTP/1.1
Host: api.example.com
Content-Type: application/json

[
  {
    "eventId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
    "timestamp": "2025-12-14T15:30:45.123Z",
    "eventType": "CellChange",
    "userName": "john.doe",
    "machineName": "DESKTOP-ABC123",
    "userDomain": "CORPORATE",
    "sessionId": "abc123def456"
  }
]
```

**Response:**
```http
HTTP/1.1 401 Unauthorized
Content-Type: application/json

{
  "error": "Invalid or missing API key",
  "code": "UNAUTHORIZED",
  "timestamp": "2025-12-14T15:30:45.123Z"
}
```

### Example 6: Error - Batch Too Large

**Request:**
```http
POST /api/events HTTP/1.1
Host: api.example.com
Content-Type: application/json
X-API-Key: your-api-key-here

[
  ... 101 events ...
]
```

**Response:**
```http
HTTP/1.1 400 Bad Request
Content-Type: application/json

{
  "error": "Batch size exceeds maximum (100)",
  "code": "BATCH_TOO_LARGE",
  "timestamp": "2025-12-14T15:30:45.123Z"
}
```

### Example 7: Health Check

**Request:**
```http
GET /health HTTP/1.1
Host: api.example.com
```

**Response (Healthy):**
```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "status": "healthy",
  "timestamp": "2025-12-14T15:30:45.123Z",
  "checks": {
    "database": "healthy",
    "storage": "healthy"
  }
}
```

**Response (Unhealthy):**
```http
HTTP/1.1 503 Service Unavailable
Content-Type: application/json

{
  "status": "unhealthy",
  "timestamp": "2025-12-14T15:30:45.123Z",
  "checks": {
    "database": "unhealthy",
    "storage": "healthy"
  }
}
```

---

## Deployment Checklist

Before deploying to production:

- [ ] HTTPS/TLS configured with valid certificate
- [ ] API keys generated and securely stored
- [ ] Database created with proper indexes
- [ ] Database backups configured (every 6 hours)
- [ ] Connection pooling configured (10-50 connections)
- [ ] Rate limiting enabled
- [ ] Health check endpoint working
- [ ] Logging configured and tested
- [ ] Monitoring/alerting configured
- [ ] Load testing completed successfully
- [ ] Security scan completed (no critical vulnerabilities)
- [ ] CORS configured (if needed)
- [ ] Environment variables set
- [ ] Documentation updated
- [ ] Runbook created for on-call
- [ ] Rollback plan documented

---

## Summary

This specification provides everything needed to build a production-ready backend API for the DGT Excel add-in:

**Core Requirements:**
- Single endpoint: `POST /api/events`
- Accepts JSON array of 1-100 events
- Idempotent by `eventId`
- Authenticated via `X-API-Key` header
- Returns 2xx for success, 4xx/5xx for errors

**Quality Standards:**
- 99.9% uptime
- <1s latency for 100-event batches
- Zero data loss
- Comprehensive testing (unit, integration, load)
- Full monitoring and alerting

**Resilience:**
- Client implements retry with exponential backoff
- Circuit breaker opens after 50% failure rate
- Health check endpoint for proactive monitoring
- Graceful degradation during outages

**Performance:**
- Support 100 concurrent clients
- 6,000 events/minute total throughput
- Database optimized with proper indexes
- Efficient bulk insert operations

Copy this entire document to your agentic orchestrator to build the backend API. The specification is complete, unambiguous, and designed for zero-bug reliability.
