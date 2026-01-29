"""
Pydantic schemas for API request/response validation.
Matches the frontend AuditEvent model exactly for seamless integration.
"""
from datetime import datetime, timezone
from typing import Optional, Union
from uuid import UUID, uuid4

from pydantic import BaseModel, Field, ConfigDict, field_validator

from models.database import AuditEventType


# ============================================================================
# Event Schemas
# ============================================================================

class AuditEventBase(BaseModel):
    """Base schema for audit events - shared fields."""

    # Event classification
    event_type: AuditEventType = Field(..., alias="eventType", description="Type of audit event")

    # Temporal
    timestamp: datetime = Field(
        default_factory=lambda: datetime.now(timezone.utc),
        description="UTC timestamp when event occurred"
    )

    # User context
    user_name: Optional[str] = Field(None, alias="userName", max_length=255, description="Windows username")
    machine_name: Optional[str] = Field(None, alias="machineName", max_length=255, description="Machine name")
    user_domain: Optional[str] = Field(None, alias="userDomain", max_length=255, description="User domain")
    session_id: Optional[str] = Field(None, alias="sessionId", max_length=255, description="Excel session ID")

    # Workbook context
    workbook_name: Optional[str] = Field(None, alias="workbookName", max_length=500, description="Workbook name")
    workbook_path: Optional[str] = Field(None, alias="workbookPath", max_length=1000, description="Full workbook path")
    sheet_name: Optional[str] = Field(None, alias="sheetName", max_length=255, description="Worksheet name")

    # Cell change context
    cell_address: Optional[str] = Field(None, alias="cellAddress", max_length=100, description="Cell address")
    cell_count: Optional[int] = Field(None, alias="cellCount", ge=0, description="Number of cells affected")
    old_value: Optional[str] = Field(None, alias="oldValue", description="Previous cell value")
    new_value: Optional[str] = Field(None, alias="newValue", description="New cell value")
    formula: Optional[str] = Field(None, description="Cell formula")

    # Additional data
    details: Optional[str] = Field(None, description="Event-specific details")
    error_message: Optional[str] = Field(None, alias="errorMessage", description="Error message for error events")
    correlation_id: Optional[str] = Field(None, alias="correlationId", max_length=255, description="Correlation ID")

    model_config = ConfigDict(populate_by_name=True)

    @field_validator('timestamp', mode='after')
    @classmethod
    def ensure_timezone_aware(cls, v: datetime) -> datetime:
        """
        Ensure timestamp is always timezone-aware.
        If naive, assume UTC and add timezone info.
        This prevents timezone comparison errors throughout the system.
        """
        if v is not None and v.tzinfo is None:
            # Timezone-naive datetime - assume it's UTC and make it aware
            return v.replace(tzinfo=timezone.utc)
        return v


class AuditEventCreate(AuditEventBase):
    """
    Schema for creating a new audit event.
    Matches the JSON sent by the .NET frontend exactly.
    """
    event_id: UUID = Field(default_factory=uuid4, alias="eventId", description="Unique event identifier")

    model_config = ConfigDict(
        populate_by_name=True,
        json_schema_extra={
            "example": {
                "eventId": "550e8400-e29b-41d4-a716-446655440000",
                "timestamp": "2025-01-15T10:30:00Z",
                "eventType": "CellChange",
                "userName": "john.doe",
                "machineName": "DESKTOP-ABC123",
                "userDomain": "CORP",
                "sessionId": "sess_123456",
                "workbookName": "FinancialModel.xlsx",
                "workbookPath": "C:\\Users\\john.doe\\Documents\\FinancialModel.xlsx",
                "sheetName": "Q1 Results",
                "cellAddress": "$B$5",
                "cellCount": 1,
                "oldValue": "1000",
                "newValue": "1500",
                "formula": "=SUM(A1:A4)",
                "details": None,
                "errorMessage": None,
                "correlationId": None
            }
        }
    )


class AuditEventResponse(AuditEventBase):
    """Schema for audit event responses."""
    event_id: UUID
    created_at: datetime

    model_config = ConfigDict(from_attributes=True)


class AuditEventBatch(BaseModel):
    """
    Batch of audit events sent by the frontend.
    Optimized for high-throughput ingestion.
    """
    events: list[AuditEventCreate] = Field(..., min_length=1, max_length=1000)

    model_config = ConfigDict(
        json_schema_extra={
            "example": {
                "events": [
                    {
                        "eventId": "550e8400-e29b-41d4-a716-446655440000",
                        "timestamp": "2025-01-15T10:30:00Z",
                        "eventType": "CellChange",
                        "userName": "john.doe",
                        "sessionId": "sess_123456",
                        "workbookName": "Report.xlsx",
                        "cellAddress": "$A$1",
                        "newValue": "100"
                    }
                ]
            }
        }
    )


# ============================================================================
# Query Schemas
# ============================================================================

class EventQueryRequest(BaseModel):
    """Request schema for querying events with filters."""

    # Filters
    event_types: Optional[list[AuditEventType]] = Field(None, description="Filter by event types")
    user_names: Optional[list[str]] = Field(None, description="Filter by usernames")
    session_ids: Optional[list[str]] = Field(None, description="Filter by session IDs")
    workbook_names: Optional[list[str]] = Field(None, description="Filter by workbook names")
    correlation_id: Optional[str] = Field(None, description="Filter by correlation ID")

    # Time range
    start_time: Optional[datetime] = Field(None, description="Start of time range (UTC)")
    end_time: Optional[datetime] = Field(None, description="End of time range (UTC)")

    # Pagination
    offset: int = Field(0, ge=0, description="Number of records to skip")
    limit: int = Field(100, ge=1, le=1000, description="Max records to return")

    # Sorting
    order_by: str = Field("timestamp", description="Field to sort by")
    order_desc: bool = Field(True, description="Sort in descending order")

    @field_validator('start_time', 'end_time', mode='after')
    @classmethod
    def ensure_query_times_aware(cls, v: Optional[datetime]) -> Optional[datetime]:
        """Ensure query time parameters are timezone-aware."""
        if v is not None and v.tzinfo is None:
            return v.replace(tzinfo=timezone.utc)
        return v

    model_config = ConfigDict(
        json_schema_extra={
            "example": {
                "event_types": ["CellChange", "WorkbookSave"],
                "user_names": ["john.doe"],
                "start_time": "2025-01-01T00:00:00Z",
                "end_time": "2025-01-31T23:59:59Z",
                "offset": 0,
                "limit": 100,
                "order_by": "timestamp",
                "order_desc": True
            }
        }
    )


class EventQueryResponse(BaseModel):
    """Response schema for event queries."""
    total: int = Field(..., description="Total matching records")
    offset: int = Field(..., description="Current offset")
    limit: int = Field(..., description="Current limit")
    events: list[AuditEventResponse] = Field(..., description="Matching events")


# ============================================================================
# Session Schemas
# ============================================================================

class SessionSummary(BaseModel):
    """Summary of an Excel session."""
    session_id: str
    user_name: Optional[str]
    machine_name: Optional[str]
    start_time: datetime
    end_time: Optional[datetime]
    event_count: int
    duration_minutes: Optional[float] = Field(None, description="Session duration in minutes")

    model_config = ConfigDict(from_attributes=True)


# ============================================================================
# Health & Status Schemas
# ============================================================================

class HealthStatus(BaseModel):
    """Health check response."""
    status: str = Field(..., description="Overall status (healthy/degraded/unhealthy)")
    timestamp: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    version: str
    environment: str
    checks: dict[str, bool] = Field(..., description="Individual health checks")
    details: Optional[dict] = Field(None, description="Additional health details")


class BatchIngestResponse(BaseModel):
    """Response for batch event ingestion."""
    accepted: int = Field(..., description="Number of events accepted")
    rejected: int = Field(0, description="Number of events rejected")
    errors: list[str] = Field(default_factory=list, description="Validation errors")
    processing_time_ms: float = Field(..., description="Processing time in milliseconds")


# ============================================================================
# Analytics Schemas
# ============================================================================

class EventStatistics(BaseModel):
    """Event statistics for a time period."""
    period_start: datetime
    period_end: datetime
    total_events: int
    events_by_type: dict[str, int]
    unique_users: int
    unique_sessions: int
    unique_workbooks: int


class UserActivitySummary(BaseModel):
    """Summary of user activity."""
    user_name: str
    total_events: int
    session_count: int
    workbook_count: int
    first_activity: datetime
    last_activity: datetime
    events_by_type: dict[str, int]
