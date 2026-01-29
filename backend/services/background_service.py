"""
Background tasks for data management and maintenance.
Handles cleanup, archival, and scheduled operations.
"""
import asyncio
from datetime import datetime, timedelta, timezone

from apscheduler.schedulers.asyncio import AsyncIOScheduler
from apscheduler.triggers.interval import IntervalTrigger

from infrastructure.database import db_manager
from infrastructure.logger import get_logger
from services.event_service import EventService
from config import get_settings

logger = get_logger(__name__)


class BackgroundService:
    """
    Background service for scheduled tasks.
    Implements self-healing patterns matching the frontend.
    """

    def __init__(self):
        self.scheduler = AsyncIOScheduler()
        self.settings = get_settings()
        self._running = False

    async def start(self) -> None:
        """
        Start background tasks.
        Called on application startup.
        """
        if self._running:
            logger.warning("background_service_already_running")
            return

        logger.info("background_service_starting")

        # Schedule cleanup task
        self.scheduler.add_job(
            self._cleanup_old_events,
            trigger=IntervalTrigger(hours=self.settings.cleanup_interval_hours),
            id="cleanup_old_events",
            name="Cleanup old events based on retention policy",
            replace_existing=True,
            max_instances=1  # Prevent concurrent cleanup runs
        )

        # Schedule session timeout task (close stale sessions)
        self.scheduler.add_job(
            self._close_stale_sessions,
            trigger=IntervalTrigger(hours=1),
            id="close_stale_sessions",
            name="Close sessions inactive for >24 hours",
            replace_existing=True,
            max_instances=1
        )

        # Start scheduler
        self.scheduler.start()
        self._running = True

        logger.info(
            "background_service_started",
            jobs=len(self.scheduler.get_jobs())
        )

    async def stop(self) -> None:
        """
        Stop background tasks gracefully.
        Called on application shutdown.
        """
        if not self._running:
            return

        logger.info("background_service_stopping")

        # Shutdown scheduler (wait for running jobs to complete)
        self.scheduler.shutdown(wait=True)
        self._running = False

        logger.info("background_service_stopped")

    async def _cleanup_old_events(self) -> None:
        """
        Cleanup old events based on retention policy.
        Runs periodically to prevent database bloat.
        """
        logger.info("cleanup_task_started")

        try:
            async with db_manager.session() as session:
                service = EventService(session)
                deleted = await service.cleanup_old_events()

                logger.info(
                    "cleanup_task_completed",
                    deleted_count=deleted,
                    retention_days=self.settings.event_retention_days
                )

        except Exception as e:
            logger.error("cleanup_task_failed", error=str(e), exc_info=True)

    async def _close_stale_sessions(self) -> None:
        """
        Close sessions that have been inactive for >24 hours.
        Helps maintain accurate session state.
        """
        logger.info("close_stale_sessions_task_started")

        try:
            from repositories.session_repository import SessionRepository

            async with db_manager.session() as session:
                repo = SessionRepository(session)

                # Get active sessions
                active_sessions = await repo.get_active_sessions()

                cutoff_time = datetime.now(timezone.utc) - timedelta(hours=24)
                closed_count = 0

                for sess in active_sessions:
                    # If session has no activity for 24+ hours, close it
                    if sess.start_time < cutoff_time:
                        # Estimate end time as 24 hours after start
                        estimated_end = sess.start_time + timedelta(hours=24)
                        await repo.end_session(sess.session_id, estimated_end)
                        closed_count += 1

                await session.commit()

                logger.info(
                    "close_stale_sessions_task_completed",
                    closed_count=closed_count
                )

        except Exception as e:
            logger.error("close_stale_sessions_task_failed", error=str(e), exc_info=True)
