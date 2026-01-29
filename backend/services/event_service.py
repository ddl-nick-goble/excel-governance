"""
Event service with business logic and orchestration.
Handles event processing, validation, and session tracking.
"""
from datetime import datetime, timedelta, timezone
from typing import Optional
from uuid import UUID

from sqlalchemy.ext.asyncio import AsyncSession
from tenacity import (
    retry,
    stop_after_attempt,
    wait_exponential,
    retry_if_exception_type
)

from models.schemas import (
    AuditEventCreate,
    AuditEventResponse,
    EventQueryRequest,
    EventQueryResponse,
    EventStatistics,
    BatchIngestResponse
)
from repositories.event_repository import EventRepository
from repositories.session_repository import SessionRepository
from infrastructure.logger import get_logger
from config import get_settings

logger = get_logger(__name__)


class EventService:
    """
    Service for event operations with business logic.
    Matches the frontend's event processing patterns.
    """

    def __init__(self, session: AsyncSession):
        self.session = session
        self.event_repo = EventRepository(session)
        self.session_repo = SessionRepository(session)
        self.settings = get_settings()

    async def ingest_batch(
        self,
        events: list[AuditEventCreate],
        track_sessions: bool = True
    ) -> BatchIngestResponse:
        """
        Ingest a batch of events with session tracking.
        Implements the same batch processing as the frontend.

        Args:
            events: List of events to ingest
            track_sessions: Whether to update session records

        Returns:
            Batch ingestion response with statistics
        """
        start_time = datetime.now(timezone.utc)

        try:
            # Validate batch size
            if len(events) > self.settings.max_batch_size:
                logger.warning(
                    "batch_size_exceeded",
                    requested=len(events),
                    max_allowed=self.settings.max_batch_size
                )
                return BatchIngestResponse(
                    accepted=0,
                    rejected=len(events),
                    errors=[f"Batch size {len(events)} exceeds maximum {self.settings.max_batch_size}"],
                    processing_time_ms=0
                )

            # Track sessions if enabled
            if track_sessions:
                await self._update_sessions(events)

            # Bulk insert events
            inserted = await self.event_repo.bulk_create(
                events,
                batch_size=self.settings.max_batch_size
            )

            # Calculate processing time
            processing_time = (datetime.now(timezone.utc) - start_time).total_seconds() * 1000

            logger.info(
                "batch_ingestion_completed",
                accepted=inserted,
                processing_time_ms=processing_time,
                events_per_second=inserted / (processing_time / 1000) if processing_time > 0 else 0
            )

            return BatchIngestResponse(
                accepted=inserted,
                rejected=0,
                errors=[],
                processing_time_ms=processing_time
            )

        except Exception as e:
            processing_time = (datetime.now(timezone.utc) - start_time).total_seconds() * 1000
            logger.error(
                "batch_ingestion_failed",
                error=str(e),
                event_count=len(events),
                exc_info=True
            )

            return BatchIngestResponse(
                accepted=0,
                rejected=len(events),
                errors=[str(e)],
                processing_time_ms=processing_time
            )

    async def _update_sessions(self, events: list[AuditEventCreate]) -> None:
        """Update session records from event batch."""
        session_data: dict[str, tuple[Optional[str], Optional[str], datetime]] = {}

        # Collect unique sessions from events
        for event in events:
            if event.session_id:
                if event.session_id not in session_data:
                    session_data[event.session_id] = (
                        event.user_name,
                        event.machine_name,
                        event.timestamp
                    )
                else:
                    # Keep earliest timestamp
                    existing_time = session_data[event.session_id][2]
                    if event.timestamp < existing_time:
                        session_data[event.session_id] = (
                            event.user_name,
                            event.machine_name,
                            event.timestamp
                        )

        # Upsert sessions and increment counts
        for session_id, (user_name, machine_name, start_time) in session_data.items():
            await self.session_repo.upsert_session(
                session_id=session_id,
                user_name=user_name,
                machine_name=machine_name,
                start_time=start_time
            )

            # Count events for this session
            event_count = sum(1 for e in events if e.session_id == session_id)
            await self.session_repo.increment_event_count(session_id, event_count)

    async def get_event(self, event_id: UUID) -> Optional[AuditEventResponse]:
        """Get a single event by ID."""
        event = await self.event_repo.get_by_id(event_id)
        if event:
            return AuditEventResponse.model_validate(event)
        return None

    async def query_events(
        self,
        query_params: EventQueryRequest
    ) -> EventQueryResponse:
        """
        Query events with filters and pagination.
        Optimized for analytics and reporting.
        """
        events, total = await self.event_repo.query_events(query_params)

        return EventQueryResponse(
            total=total,
            offset=query_params.offset,
            limit=query_params.limit,
            events=[AuditEventResponse.model_validate(e) for e in events]
        )

    async def get_statistics(
        self,
        start_time: Optional[datetime] = None,
        end_time: Optional[datetime] = None
    ) -> EventStatistics:
        """
        Get event statistics for a time period.
        Defaults to last 24 hours if not specified.
        """
        if not end_time:
            end_time = datetime.now(timezone.utc)
        if not start_time:
            start_time = end_time - timedelta(hours=24)

        stats = await self.event_repo.get_statistics(start_time, end_time)

        return EventStatistics(
            period_start=stats["period_start"],
            period_end=stats["period_end"],
            total_events=stats["total_events"],
            events_by_type=stats["events_by_type"],
            unique_users=stats["unique_users"],
            unique_sessions=stats["unique_sessions"],
            unique_workbooks=stats["unique_workbooks"]
        )

    async def cleanup_old_events(self, retention_days: Optional[int] = None) -> int:
        """
        Delete events older than retention period.
        Used by background tasks for data management.
        """
        if retention_days is None:
            retention_days = self.settings.event_retention_days

        cutoff_date = datetime.now(timezone.utc) - timedelta(days=retention_days)

        logger.info(
            "cleanup_started",
            retention_days=retention_days,
            cutoff_date=cutoff_date
        )

        deleted = await self.event_repo.delete_old_events(cutoff_date)

        logger.info("cleanup_completed", deleted_count=deleted)

        return deleted
