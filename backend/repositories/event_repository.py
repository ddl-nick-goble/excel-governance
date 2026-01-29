"""
Event repository with optimized async bulk operations.
Implements high-performance batch inserts and queries matching the frontend's patterns.
"""
from datetime import datetime
from typing import Optional, Sequence
from uuid import UUID

from sqlalchemy import select, func, and_, or_, delete
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.dialects.postgresql import insert as pg_insert

from models.database import AuditEvent, AuditEventType
from models.schemas import AuditEventCreate, EventQueryRequest
from infrastructure.logger import get_logger

logger = get_logger(__name__)


class EventRepository:
    """
    Repository for audit event database operations.
    Optimized for high-throughput batch operations matching frontend's batching strategy.
    """

    def __init__(self, session: AsyncSession):
        self.session = session

    async def bulk_create(
        self,
        events: list[AuditEventCreate],
        batch_size: int = 500
    ) -> int:
        """
        Bulk insert events with batching for optimal performance.
        Matches the frontend's batch-and-forward pattern.

        Args:
            events: List of events to insert
            batch_size: Number of records per batch (default 500)

        Returns:
            Number of events successfully inserted
        """
        if not events:
            return 0

        total_inserted = 0

        try:
            # Convert Pydantic models to dict for SQLAlchemy
            event_dicts = [
                {
                    "event_id": event.event_id,
                    "timestamp": event.timestamp,
                    "event_type": event.event_type,
                    "user_name": event.user_name,
                    "machine_name": event.machine_name,
                    "user_domain": event.user_domain,
                    "session_id": event.session_id,
                    "workbook_name": event.workbook_name,
                    "workbook_path": event.workbook_path,
                    "sheet_name": event.sheet_name,
                    "cell_address": event.cell_address,
                    "cell_count": event.cell_count,
                    "old_value": event.old_value,
                    "new_value": event.new_value,
                    "formula": event.formula,
                    "details": event.details,
                    "error_message": event.error_message,
                    "correlation_id": event.correlation_id,
                }
                for event in events
            ]

            # Process in batches to avoid overwhelming the database
            for i in range(0, len(event_dicts), batch_size):
                batch = event_dicts[i:i + batch_size]

                # Use bulk_insert_mappings for maximum performance
                self.session.add_all([AuditEvent(**event_dict) for event_dict in batch])
                await self.session.flush()

                total_inserted += len(batch)

                logger.debug(
                    "bulk_insert_batch_completed",
                    batch_number=i // batch_size + 1,
                    batch_size=len(batch),
                    total_inserted=total_inserted
                )

            await self.session.commit()

            logger.info(
                "bulk_insert_completed",
                total_events=len(events),
                total_inserted=total_inserted
            )

            return total_inserted

        except Exception as e:
            await self.session.rollback()
            logger.error(
                "bulk_insert_failed",
                error=str(e),
                event_count=len(events),
                exc_info=True
            )
            raise

    async def get_by_id(self, event_id: UUID) -> Optional[AuditEvent]:
        """Get a single event by ID."""
        result = await self.session.execute(
            select(AuditEvent).where(AuditEvent.event_id == event_id)
        )
        return result.scalar_one_or_none()

    async def query_events(
        self,
        query_params: EventQueryRequest
    ) -> tuple[list[AuditEvent], int]:
        """
        Query events with filters, pagination, and sorting.

        Returns:
            Tuple of (events, total_count)
        """
        # Build base query
        stmt = select(AuditEvent)
        count_stmt = select(func.count()).select_from(AuditEvent)

        # Apply filters
        filters = []

        if query_params.event_types:
            filters.append(AuditEvent.event_type.in_(query_params.event_types))

        if query_params.user_names:
            filters.append(AuditEvent.user_name.in_(query_params.user_names))

        if query_params.session_ids:
            filters.append(AuditEvent.session_id.in_(query_params.session_ids))

        if query_params.workbook_names:
            filters.append(AuditEvent.workbook_name.in_(query_params.workbook_names))

        if query_params.correlation_id:
            filters.append(AuditEvent.correlation_id == query_params.correlation_id)

        if query_params.start_time:
            filters.append(AuditEvent.timestamp >= query_params.start_time)

        if query_params.end_time:
            filters.append(AuditEvent.timestamp <= query_params.end_time)

        if filters:
            stmt = stmt.where(and_(*filters))
            count_stmt = count_stmt.where(and_(*filters))

        # Get total count
        total_result = await self.session.execute(count_stmt)
        total_count = total_result.scalar_one()

        # Apply sorting
        order_column = getattr(AuditEvent, query_params.order_by, AuditEvent.timestamp)
        if query_params.order_desc:
            stmt = stmt.order_by(order_column.desc())
        else:
            stmt = stmt.order_by(order_column.asc())

        # Apply pagination
        stmt = stmt.offset(query_params.offset).limit(query_params.limit)

        # Execute query
        result = await self.session.execute(stmt)
        events = result.scalars().all()

        logger.debug(
            "query_events_completed",
            total_count=total_count,
            returned_count=len(events),
            filters=str(query_params)
        )

        return list(events), total_count

    async def get_events_by_session(
        self,
        session_id: str,
        limit: int = 1000
    ) -> list[AuditEvent]:
        """Get all events for a specific session."""
        stmt = (
            select(AuditEvent)
            .where(AuditEvent.session_id == session_id)
            .order_by(AuditEvent.timestamp.desc())
            .limit(limit)
        )
        result = await self.session.execute(stmt)
        return list(result.scalars().all())

    async def get_events_by_workbook(
        self,
        workbook_name: str,
        start_time: Optional[datetime] = None,
        end_time: Optional[datetime] = None,
        limit: int = 1000
    ) -> list[AuditEvent]:
        """Get events for a specific workbook within a time range."""
        filters = [AuditEvent.workbook_name == workbook_name]

        if start_time:
            filters.append(AuditEvent.timestamp >= start_time)
        if end_time:
            filters.append(AuditEvent.timestamp <= end_time)

        stmt = (
            select(AuditEvent)
            .where(and_(*filters))
            .order_by(AuditEvent.timestamp.desc())
            .limit(limit)
        )
        result = await self.session.execute(stmt)
        return list(result.scalars().all())

    async def delete_old_events(
        self,
        before_date: datetime,
        batch_size: int = 1000
    ) -> int:
        """
        Delete events older than specified date in batches.
        Used for data retention/archival.

        Returns:
            Number of events deleted
        """
        total_deleted = 0

        try:
            while True:
                # Delete in batches to avoid long-running transactions
                stmt = (
                    delete(AuditEvent)
                    .where(AuditEvent.timestamp < before_date)
                    .execution_options(synchronize_session=False)
                )

                # For SQLite, we need to use a different approach
                # This is a simplified version that works for both
                result = await self.session.execute(
                    stmt.limit(batch_size) if hasattr(stmt, 'limit') else stmt
                )

                deleted_count = result.rowcount
                total_deleted += deleted_count

                await self.session.commit()

                logger.debug(
                    "delete_batch_completed",
                    batch_deleted=deleted_count,
                    total_deleted=total_deleted
                )

                # Stop when no more rows are deleted
                if deleted_count < batch_size:
                    break

            logger.info(
                "delete_old_events_completed",
                total_deleted=total_deleted,
                before_date=before_date
            )

            return total_deleted

        except Exception as e:
            await self.session.rollback()
            logger.error("delete_old_events_failed", error=str(e), exc_info=True)
            raise

    async def get_statistics(
        self,
        start_time: datetime,
        end_time: datetime
    ) -> dict:
        """
        Get event statistics for a time period.
        Useful for analytics and reporting.
        """
        # Total events in period
        total_stmt = (
            select(func.count())
            .select_from(AuditEvent)
            .where(
                and_(
                    AuditEvent.timestamp >= start_time,
                    AuditEvent.timestamp <= end_time
                )
            )
        )
        total_result = await self.session.execute(total_stmt)
        total_events = total_result.scalar_one()

        # Events by type
        by_type_stmt = (
            select(
                AuditEvent.event_type,
                func.count().label("count")
            )
            .where(
                and_(
                    AuditEvent.timestamp >= start_time,
                    AuditEvent.timestamp <= end_time
                )
            )
            .group_by(AuditEvent.event_type)
        )
        by_type_result = await self.session.execute(by_type_stmt)
        events_by_type = {str(row[0]): row[1] for row in by_type_result}

        # Unique users
        unique_users_stmt = (
            select(func.count(func.distinct(AuditEvent.user_name)))
            .where(
                and_(
                    AuditEvent.timestamp >= start_time,
                    AuditEvent.timestamp <= end_time
                )
            )
        )
        unique_users_result = await self.session.execute(unique_users_stmt)
        unique_users = unique_users_result.scalar_one()

        # Unique sessions
        unique_sessions_stmt = (
            select(func.count(func.distinct(AuditEvent.session_id)))
            .where(
                and_(
                    AuditEvent.timestamp >= start_time,
                    AuditEvent.timestamp <= end_time
                )
            )
        )
        unique_sessions_result = await self.session.execute(unique_sessions_stmt)
        unique_sessions = unique_sessions_result.scalar_one()

        # Unique workbooks
        unique_workbooks_stmt = (
            select(func.count(func.distinct(AuditEvent.workbook_name)))
            .where(
                and_(
                    AuditEvent.timestamp >= start_time,
                    AuditEvent.timestamp <= end_time,
                    AuditEvent.workbook_name.isnot(None)
                )
            )
        )
        unique_workbooks_result = await self.session.execute(unique_workbooks_stmt)
        unique_workbooks = unique_workbooks_result.scalar_one()

        return {
            "period_start": start_time,
            "period_end": end_time,
            "total_events": total_events,
            "events_by_type": events_by_type,
            "unique_users": unique_users,
            "unique_sessions": unique_sessions,
            "unique_workbooks": unique_workbooks,
        }
