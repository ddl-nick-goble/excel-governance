"""
Event API endpoints.
Provides REST API for event ingestion, querying, and analytics.
"""
from datetime import datetime
from typing import Optional
from uuid import UUID

from fastapi import APIRouter, Depends, HTTPException, status, Query
from sqlalchemy.ext.asyncio import AsyncSession

from infrastructure.database import get_session
from models.schemas import (
    AuditEventBatch,
    AuditEventResponse,
    EventQueryRequest,
    EventQueryResponse,
    EventStatistics,
    BatchIngestResponse
)
from services.event_service import EventService
from infrastructure.logger import get_logger

logger = get_logger(__name__)

router = APIRouter(prefix="/api/events", tags=["events"])


@router.post(
    "",
    response_model=BatchIngestResponse,
    status_code=status.HTTP_202_ACCEPTED,
    summary="Ingest batch of audit events",
    description="Accepts a batch of audit events from Excel add-in for processing and storage"
)
async def ingest_events(
    batch: AuditEventBatch,
    session: AsyncSession = Depends(get_session)
) -> BatchIngestResponse:
    """
    Ingest a batch of audit events.
    This is the primary endpoint called by the Excel add-in.

    The endpoint:
    - Accepts up to 1000 events per request
    - Validates all events before insertion
    - Updates session tracking
    - Returns processing statistics

    Matches the frontend's batch-and-forward pattern.
    """
    logger.info("batch_ingestion_request", event_count=len(batch.events))

    service = EventService(session)
    result = await service.ingest_batch(batch.events)

    if result.rejected > 0:
        logger.warning(
            "batch_ingestion_partial_failure",
            accepted=result.accepted,
            rejected=result.rejected,
            errors=result.errors
        )

    return result


@router.post(
    "/query",
    response_model=EventQueryResponse,
    summary="Query events with filters",
    description="Query audit events with flexible filters, pagination, and sorting"
)
async def query_events(
    query: EventQueryRequest,
    session: AsyncSession = Depends(get_session)
) -> EventQueryResponse:
    """
    Query events with filters and pagination.

    Supports filtering by:
    - Event types
    - Users
    - Sessions
    - Workbooks
    - Time ranges
    - Correlation IDs

    Returns paginated results with total count.
    """
    logger.debug("event_query_request", filters=str(query))

    service = EventService(session)
    result = await service.query_events(query)

    logger.info(
        "event_query_completed",
        total=result.total,
        returned=len(result.events),
        offset=result.offset
    )

    return result


@router.get(
    "/statistics",
    response_model=EventStatistics,
    summary="Get event statistics",
    description="Retrieve aggregated statistics for a time period"
)
async def get_statistics(
    start_time: Optional[datetime] = Query(
        None,
        description="Start of time range (UTC). Defaults to 24 hours ago."
    ),
    end_time: Optional[datetime] = Query(
        None,
        description="End of time range (UTC). Defaults to now."
    ),
    session: AsyncSession = Depends(get_session)
) -> EventStatistics:
    """
    Get event statistics for a time period.

    Returns:
    - Total event count
    - Events broken down by type
    - Unique user count
    - Unique session count
    - Unique workbook count

    Useful for analytics dashboards and reporting.
    """
    service = EventService(session)
    stats = await service.get_statistics(start_time, end_time)

    logger.info(
        "statistics_request",
        period_start=stats.period_start,
        period_end=stats.period_end,
        total_events=stats.total_events
    )

    return stats


@router.get(
    "/{event_id}",
    response_model=AuditEventResponse,
    summary="Get event by ID",
    description="Retrieve a single audit event by its unique identifier"
)
async def get_event(
    event_id: UUID,
    session: AsyncSession = Depends(get_session)
) -> AuditEventResponse:
    """Get a single event by ID."""
    service = EventService(session)
    event = await service.get_event(event_id)

    if not event:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Event {event_id} not found"
        )

    return event
