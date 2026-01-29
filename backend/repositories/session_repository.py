"""
Session repository for tracking Excel sessions.
Provides session analytics and user activity monitoring.
"""
from datetime import datetime, timezone
from typing import Optional

from sqlalchemy import select, func, and_, update
from sqlalchemy.ext.asyncio import AsyncSession

from models.database import Session
from infrastructure.logger import get_logger

logger = get_logger(__name__)


def ensure_timezone_aware(dt: datetime) -> datetime:
    """
    Ensure a datetime is timezone-aware.
    If naive, assume UTC and add timezone info.
    This prevents timezone comparison errors.
    """
    if dt is not None and dt.tzinfo is None:
        return dt.replace(tzinfo=timezone.utc)
    return dt


class SessionRepository:
    """Repository for Excel session operations."""

    def __init__(self, session: AsyncSession):
        self.session = session

    async def upsert_session(
        self,
        session_id: str,
        user_name: Optional[str],
        machine_name: Optional[str],
        start_time: datetime
    ) -> Session:
        """
        Create or update a session record.
        Uses upsert pattern for idempotency.
        """
        # Check if session exists
        stmt = select(Session).where(Session.session_id == session_id)
        result = await self.session.execute(stmt)
        existing = result.scalar_one_or_none()

        if existing:
            # Update existing session
            existing.user_name = user_name
            existing.machine_name = machine_name

            # Ensure both datetimes are timezone-aware before comparing
            existing_start = ensure_timezone_aware(existing.start_time)
            new_start = ensure_timezone_aware(start_time)

            if existing_start > new_start:
                existing.start_time = new_start

            await self.session.flush()
            return existing
        else:
            # Create new session
            # Ensure start_time is timezone-aware
            start_time = ensure_timezone_aware(start_time)

            new_session = Session(
                session_id=session_id,
                user_name=user_name,
                machine_name=machine_name,
                start_time=start_time,
                event_count=0
            )
            self.session.add(new_session)
            await self.session.flush()
            return new_session

    async def end_session(self, session_id: str, end_time: datetime) -> bool:
        """Mark a session as ended."""
        # Ensure timezone-aware
        end_time = ensure_timezone_aware(end_time)

        stmt = (
            update(Session)
            .where(Session.session_id == session_id)
            .values(end_time=end_time)
        )
        result = await self.session.execute(stmt)
        await self.session.commit()
        return result.rowcount > 0

    async def increment_event_count(self, session_id: str, increment: int = 1) -> None:
        """Increment the event count for a session."""
        stmt = (
            update(Session)
            .where(Session.session_id == session_id)
            .values(event_count=Session.event_count + increment)
        )
        await self.session.execute(stmt)
        await self.session.flush()

    async def get_by_id(self, session_id: str) -> Optional[Session]:
        """Get a session by ID."""
        result = await self.session.execute(
            select(Session).where(Session.session_id == session_id)
        )
        return result.scalar_one_or_none()

    async def get_active_sessions(self) -> list[Session]:
        """Get all active sessions (no end time)."""
        stmt = (
            select(Session)
            .where(Session.end_time.is_(None))
            .order_by(Session.start_time.desc())
        )
        result = await self.session.execute(stmt)
        return list(result.scalars().all())

    async def get_user_sessions(
        self,
        user_name: str,
        start_time: Optional[datetime] = None,
        end_time: Optional[datetime] = None,
        limit: int = 100
    ) -> list[Session]:
        """Get sessions for a specific user."""
        filters = [Session.user_name == user_name]

        if start_time:
            filters.append(Session.start_time >= start_time)
        if end_time:
            filters.append(Session.start_time <= end_time)

        stmt = (
            select(Session)
            .where(and_(*filters))
            .order_by(Session.start_time.desc())
            .limit(limit)
        )
        result = await self.session.execute(stmt)
        return list(result.scalars().all())
