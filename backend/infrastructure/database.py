"""
Database connection management with async SQLAlchemy.
Implements connection pooling, health checks, and graceful lifecycle management.
"""
from contextlib import asynccontextmanager
from typing import AsyncGenerator

from sqlalchemy import event, text
from sqlalchemy.ext.asyncio import (
    AsyncEngine,
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)
from sqlalchemy.pool import NullPool, QueuePool

from config import get_settings
from infrastructure.logger import get_logger

logger = get_logger(__name__)


class DatabaseManager:
    """
    Manages database connections with pooling and health checks.
    Follows the same resilience patterns as the frontend's resource management.
    """

    def __init__(self):
        self._engine: AsyncEngine | None = None
        self._session_factory: async_sessionmaker[AsyncSession] | None = None

    async def initialize(self) -> None:
        """
        Initialize database engine and connection pool.
        Called on application startup.
        """
        if self._engine is not None:
            logger.warning("database_already_initialized")
            return

        settings = get_settings()

        try:
            # Configure connection pool based on database type
            if settings.is_sqlite:
                # SQLite: Single connection, no pooling
                self._engine = create_async_engine(
                    settings.database_url,
                    echo=settings.db_echo,
                    poolclass=NullPool,
                    connect_args={"check_same_thread": False},
                )
                logger.info("database_initialized", db_type="sqlite", url=settings.database_url)
            else:
                # PostgreSQL: Connection pooling for concurrency
                self._engine = create_async_engine(
                    settings.database_url,
                    echo=settings.db_echo,
                    poolclass=QueuePool,
                    pool_size=settings.db_pool_size,
                    max_overflow=settings.db_max_overflow,
                    pool_timeout=settings.db_pool_timeout,
                    pool_recycle=settings.db_pool_recycle,
                    pool_pre_ping=True,  # Verify connections before use
                )
                logger.info(
                    "database_initialized",
                    db_type="postgresql",
                    pool_size=settings.db_pool_size,
                    max_overflow=settings.db_max_overflow,
                )

            # Create session factory
            self._session_factory = async_sessionmaker(
                self._engine,
                class_=AsyncSession,
                expire_on_commit=False,
                autoflush=False,
            )

            # Test connection
            await self.health_check()

        except Exception as e:
            logger.error("database_initialization_failed", error=str(e), exc_info=True)
            raise

    async def shutdown(self) -> None:
        """
        Gracefully shutdown database connections.
        Called on application shutdown.
        """
        if self._engine is None:
            return

        try:
            logger.info("database_shutdown_started")
            await self._engine.dispose()
            self._engine = None
            self._session_factory = None
            logger.info("database_shutdown_completed")
        except Exception as e:
            logger.error("database_shutdown_failed", error=str(e), exc_info=True)

    @asynccontextmanager
    async def session(self) -> AsyncGenerator[AsyncSession, None]:
        """
        Provide a transactional database session.
        Automatically commits on success, rolls back on error.

        Usage:
            async with db_manager.session() as session:
                result = await session.execute(query)
                await session.commit()
        """
        if self._session_factory is None:
            raise RuntimeError("Database not initialized. Call initialize() first.")

        session = self._session_factory()
        try:
            yield session
            await session.commit()
        except Exception:
            await session.rollback()
            raise
        finally:
            await session.close()

    async def health_check(self) -> bool:
        """
        Verify database connectivity.
        Returns True if healthy, raises exception otherwise.
        """
        if self._engine is None:
            raise RuntimeError("Database not initialized")

        try:
            async with self._engine.begin() as conn:
                await conn.execute(text("SELECT 1"))
            logger.debug("database_health_check_passed")
            return True
        except Exception as e:
            logger.error("database_health_check_failed", error=str(e), exc_info=True)
            raise

    @property
    def engine(self) -> AsyncEngine:
        """Get the database engine (for migrations, etc.)."""
        if self._engine is None:
            raise RuntimeError("Database not initialized")
        return self._engine

    @property
    def is_initialized(self) -> bool:
        """Check if database is initialized."""
        return self._engine is not None


# Global database manager instance (singleton)
db_manager = DatabaseManager()


async def get_session() -> AsyncGenerator[AsyncSession, None]:
    """
    FastAPI dependency for getting database sessions.

    Usage:
        @app.post("/events")
        async def create_event(
            event: EventCreate,
            session: AsyncSession = Depends(get_session)
        ):
            ...
    """
    async with db_manager.session() as session:
        yield session
