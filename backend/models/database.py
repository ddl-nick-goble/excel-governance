"""
SQLAlchemy ORM models for database tables.
Maps to the AuditEvent structure from the .NET frontend.
"""
from datetime import datetime, timezone
from typing import Optional
from uuid import UUID, uuid4

from sqlalchemy import (
    Index,
    Integer,
    String,
    DateTime,
    Enum,
    Text,
    func,
)
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column
import enum


class Base(DeclarativeBase):
    """Base class for all database models."""
    pass


class AuditEventType(int, enum.Enum):
    """
    Audit event types - matches frontend C# enum exactly with integer values.
    C# sends integers, not strings, so we use int enum.
    """
    # Workbook events
    WORKBOOK_NEW = 0
    WORKBOOK_OPEN = 1
    WORKBOOK_CLOSE = 2
    WORKBOOK_SAVE = 3
    WORKBOOK_ACTIVATE = 4
    WORKBOOK_DEACTIVATE = 5

    # Cell/Sheet events
    CELL_CHANGE = 6
    SELECTION_CHANGE = 7
    SHEET_ADD = 8
    SHEET_DELETE = 9
    SHEET_RENAME = 10
    SHEET_ACTIVATE = 11

    # System events
    SESSION_START = 12
    SESSION_END = 13
    ADDIN_LOAD = 14
    ADDIN_UNLOAD = 15
    ERROR = 16


class AuditEvent(Base):
    """
    Audit event model - matches frontend AuditEvent class.
    Optimized with indexes for common query patterns.
    """
    __tablename__ = "audit_events"

    # Primary key
    event_id: Mapped[UUID] = mapped_column(
        primary_key=True,
        default=uuid4,
        index=True,
        comment="Unique event identifier"
    )

    # Temporal
    timestamp: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        default=lambda: datetime.now(timezone.utc),
        index=True,
        comment="UTC timestamp when event occurred"
    )
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        server_default=func.now(),
        comment="When record was inserted into database"
    )

    # Event classification
    event_type: Mapped[AuditEventType] = mapped_column(
        Enum(AuditEventType, native_enum=False, length=50),
        index=True,
        comment="Type of audit event"
    )

    # User context
    user_name: Mapped[Optional[str]] = mapped_column(
        String(255),
        index=True,
        comment="Windows username"
    )
    machine_name: Mapped[Optional[str]] = mapped_column(
        String(255),
        index=True,
        comment="Machine name"
    )
    user_domain: Mapped[Optional[str]] = mapped_column(
        String(255),
        comment="User domain (if applicable)"
    )
    session_id: Mapped[Optional[str]] = mapped_column(
        String(255),
        index=True,
        comment="Excel session ID"
    )

    # Workbook context
    workbook_name: Mapped[Optional[str]] = mapped_column(
        String(500),
        index=True,
        comment="Workbook name (e.g., 'Book1.xlsx')"
    )
    workbook_path: Mapped[Optional[str]] = mapped_column(
        String(1000),
        comment="Full path to workbook"
    )
    sheet_name: Mapped[Optional[str]] = mapped_column(
        String(255),
        comment="Worksheet name"
    )

    # Cell change context
    cell_address: Mapped[Optional[str]] = mapped_column(
        String(100),
        comment="Cell address (e.g., '$A$1' or '$A$1:$B$5')"
    )
    cell_count: Mapped[Optional[int]] = mapped_column(
        Integer,
        comment="Number of cells affected"
    )
    old_value: Mapped[Optional[str]] = mapped_column(
        Text,
        comment="Previous cell value"
    )
    new_value: Mapped[Optional[str]] = mapped_column(
        Text,
        comment="New cell value"
    )
    formula: Mapped[Optional[str]] = mapped_column(
        Text,
        comment="Cell formula (if applicable)"
    )

    # Additional data
    details: Mapped[Optional[str]] = mapped_column(
        Text,
        comment="Event-specific details"
    )
    error_message: Mapped[Optional[str]] = mapped_column(
        Text,
        comment="Error message (for error events)"
    )
    correlation_id: Mapped[Optional[str]] = mapped_column(
        String(255),
        index=True,
        comment="Correlation ID to link related events"
    )

    # Composite indexes for common query patterns
    __table_args__ = (
        # Time-based queries (most common)
        Index("ix_events_timestamp_type", "timestamp", "event_type"),
        Index("ix_events_timestamp_user", "timestamp", "user_name"),

        # User activity queries
        Index("ix_events_user_timestamp", "user_name", "timestamp"),
        Index("ix_events_session_timestamp", "session_id", "timestamp"),

        # Workbook tracking
        Index("ix_events_workbook_timestamp", "workbook_name", "timestamp"),

        # Correlation tracking
        Index("ix_events_correlation", "correlation_id", "timestamp"),
    )

    def __repr__(self) -> str:
        return (
            f"AuditEvent(event_id={self.event_id}, "
            f"type={self.event_type}, "
            f"user={self.user_name}, "
            f"timestamp={self.timestamp})"
        )


class Session(Base):
    """
    Excel session tracking for analytics and user activity monitoring.
    """
    __tablename__ = "sessions"

    session_id: Mapped[str] = mapped_column(
        String(255),
        primary_key=True,
        comment="Excel session identifier"
    )
    user_name: Mapped[Optional[str]] = mapped_column(
        String(255),
        index=True,
        comment="Windows username"
    )
    machine_name: Mapped[Optional[str]] = mapped_column(
        String(255),
        comment="Machine name"
    )
    start_time: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        index=True,
        comment="Session start time"
    )
    end_time: Mapped[Optional[datetime]] = mapped_column(
        DateTime(timezone=True),
        comment="Session end time"
    )
    event_count: Mapped[int] = mapped_column(
        Integer,
        default=0,
        comment="Total events in this session"
    )
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        server_default=func.now(),
        comment="Record creation timestamp"
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        server_default=func.now(),
        onupdate=func.now(),
        comment="Record update timestamp"
    )

    __table_args__ = (
        Index("ix_sessions_user_start", "user_name", "start_time"),
    )

    def __repr__(self) -> str:
        return f"Session(session_id={self.session_id}, user={self.user_name}, start={self.start_time})"
