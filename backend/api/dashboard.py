"""
Dashboard API endpoints for live monitoring web interface.
Provides real-time metrics and event streaming for the web dashboard.
"""
import os
import asyncio
from datetime import datetime, timezone, timedelta
from typing import Optional

from fastapi import APIRouter, Depends, Query, WebSocket, WebSocketDisconnect
from sqlalchemy import select, func
from sqlalchemy.ext.asyncio import AsyncSession

from infrastructure.database import get_session, db_manager
from models.database import AuditEvent, AuditEventType, RegisteredModel
from infrastructure.logger import get_logger

logger = get_logger(__name__)

router = APIRouter(prefix="/api/dashboard", tags=["dashboard"])


@router.get("/live-data")
async def get_live_data(
    limit: int = Query(50, ge=1, le=500, description="Number of recent events to return"),
    session: AsyncSession = Depends(get_session)
):
    """
    Get live dashboard data including metrics and recent events.

    Returns:
    - Time since last update (most recent event)
    - Total database size
    - Total cells changed (sum of all cell modifications)
    - Total workbooks tracked
    - Total sheets tracked
    - Total events tracked
    - Recent events list
    """
    try:
        # Get most recent event timestamp
        most_recent_stmt = (
            select(func.max(AuditEvent.timestamp))
        )
        most_recent_result = await session.execute(most_recent_stmt)
        most_recent_timestamp = most_recent_result.scalar_one_or_none()

        # Calculate time since last update
        time_since_last_update = None
        if most_recent_timestamp:
            # Ensure timestamp is timezone-aware
            if most_recent_timestamp.tzinfo is None:
                most_recent_timestamp = most_recent_timestamp.replace(tzinfo=timezone.utc)

            now = datetime.now(timezone.utc)
            delta = now - most_recent_timestamp
            time_since_last_update = {
                "seconds": int(delta.total_seconds()),
                "human_readable": format_time_delta(delta)
            }

        # Get total event count
        total_events_stmt = select(func.count()).select_from(AuditEvent)
        total_events_result = await session.execute(total_events_stmt)
        total_events = total_events_result.scalar_one()

        # Get total cells changed (sum of cell_count)
        total_cells_stmt = (
            select(func.sum(AuditEvent.cell_count))
            .where(AuditEvent.cell_count.isnot(None))
        )
        total_cells_result = await session.execute(total_cells_stmt)
        total_cells_changed = total_cells_result.scalar_one() or 0

        # Get total unique workbooks
        total_workbooks_stmt = (
            select(func.count(func.distinct(AuditEvent.workbook_name)))
            .where(AuditEvent.workbook_name.isnot(None))
        )
        total_workbooks_result = await session.execute(total_workbooks_stmt)
        total_workbooks = total_workbooks_result.scalar_one()

        # Get total unique sheets (need to count distinct workbook_name + sheet_name combinations)
        total_sheets_stmt = (
            select(func.count())
            .select_from(
                select(AuditEvent.workbook_name, AuditEvent.sheet_name)
                .where(AuditEvent.sheet_name.isnot(None))
                .distinct()
                .subquery()
            )
        )
        total_sheets_result = await session.execute(total_sheets_stmt)
        total_sheets = total_sheets_result.scalar_one()

        # Get database size (for SQLite)
        db_size = get_database_size()

        # Get recent events
        recent_events_stmt = (
            select(AuditEvent)
            .order_by(AuditEvent.timestamp.desc())
            .limit(limit)
        )
        recent_events_result = await session.execute(recent_events_stmt)
        recent_events = recent_events_result.scalars().all()

        # Build model_id -> model_name map
        model_name_map = await _get_model_info_map(session, recent_events)

        # Format events for response
        events_list = [
            _serialize_event(event, model_name_map)
            for event in recent_events
        ]

        return {
            "metrics": {
                "time_since_last_update": time_since_last_update,
                "total_events": total_events,
                "total_cells_changed": total_cells_changed,
                "total_workbooks": total_workbooks,
                "total_sheets": total_sheets,
                "db_size": db_size,
            },
            "events": events_list,
            "timestamp": datetime.now(timezone.utc).isoformat()
        }

    except Exception as e:
        logger.error("dashboard_data_error", error=str(e), exc_info=True)
        raise


async def _get_model_info_map(session: AsyncSession, events: list) -> dict[str, dict]:
    """Build a mapping of model_id -> {model_name, version} for the given events."""
    model_ids = {e.model_id for e in events if e.model_id}
    if not model_ids:
        return {}
    stmt = (
        select(RegisteredModel.model_id, RegisteredModel.model_name, RegisteredModel.version)
        .where(RegisteredModel.model_id.in_(model_ids))
    )
    result = await session.execute(stmt)
    return {
        row.model_id: {"model_name": row.model_name, "version": row.version}
        for row in result
    }


def _serialize_event(event: AuditEvent, model_info_map: dict[str, dict]) -> dict:
    """Serialize an AuditEvent to a dict for the dashboard."""
    model_info = model_info_map.get(event.model_id, {}) if event.model_id else {}
    return {
        "event_id": str(event.event_id),
        "timestamp": (event.timestamp.replace(tzinfo=timezone.utc) if event.timestamp.tzinfo is None else event.timestamp).isoformat(),
        "event_type": event.event_type.name,
        "event_type_display": format_event_type(event.event_type),
        "model_name": model_info.get("model_name"),
        "model_version": model_info.get("version"),
        "user_name": event.user_name,
        "machine_name": event.machine_name,
        "workbook_name": event.workbook_name,
        "sheet_name": event.sheet_name,
        "cell_address": event.cell_address,
        "cell_count": event.cell_count,
        "old_value": truncate_value(event.old_value, 100),
        "new_value": truncate_value(event.new_value, 100),
        "formula": truncate_value(event.formula, 100),
        "details": event.details,
        "error_message": event.error_message,
    }


def get_database_size() -> dict:
    """Get the size of the SQLite database file."""
    try:
        db_path = "backend/data/dgt.db"
        if os.path.exists(db_path):
            size_bytes = os.path.getsize(db_path)
            return {
                "bytes": size_bytes,
                "human_readable": format_bytes(size_bytes)
            }
        return {"bytes": 0, "human_readable": "0 B"}
    except Exception as e:
        logger.error("db_size_error", error=str(e))
        return {"bytes": 0, "human_readable": "Unknown"}


def format_bytes(bytes_val: int) -> str:
    """Format bytes into human-readable string."""
    for unit in ['B', 'KB', 'MB', 'GB']:
        if bytes_val < 1024.0:
            return f"{bytes_val:.2f} {unit}"
        bytes_val /= 1024.0
    return f"{bytes_val:.2f} TB"


def format_time_delta(delta: timedelta) -> str:
    """Format timedelta into human-readable string."""
    seconds = int(delta.total_seconds())

    if seconds < 60:
        return f"{seconds} seconds ago"
    elif seconds < 3600:
        minutes = seconds // 60
        return f"{minutes} minute{'s' if minutes != 1 else ''} ago"
    elif seconds < 86400:
        hours = seconds // 3600
        return f"{hours} hour{'s' if hours != 1 else ''} ago"
    else:
        days = seconds // 86400
        return f"{days} day{'s' if days != 1 else ''} ago"


def format_event_type(event_type: AuditEventType) -> str:
    """Format event type enum to display string."""
    # Convert CELL_CHANGE to "Cell Change"
    name = event_type.name.replace('_', ' ').title()
    return name


def truncate_value(value: Optional[str], max_length: int) -> Optional[str]:
    """Truncate string value to max length."""
    if value is None:
        return None
    if len(value) <= max_length:
        return value
    return value[:max_length] + "..."


async def get_events_since(session: AsyncSession, since_timestamp: Optional[datetime], limit: int = 50) -> list[dict]:
    """Get events that occurred after the given timestamp."""
    stmt = select(AuditEvent).order_by(AuditEvent.timestamp.desc())

    if since_timestamp:
        # Ensure timezone-aware
        if since_timestamp.tzinfo is None:
            since_timestamp = since_timestamp.replace(tzinfo=timezone.utc)
        stmt = stmt.where(AuditEvent.timestamp > since_timestamp)

    stmt = stmt.limit(limit)
    result = await session.execute(stmt)
    events = result.scalars().all()

    # Build model name map and format events
    model_name_map = await _get_model_info_map(session, events)
    return [_serialize_event(event, model_name_map) for event in events]


async def get_metrics_data(session: AsyncSession) -> dict:
    """Get current metrics for the dashboard."""
    # Get total event count
    total_events_stmt = select(func.count()).select_from(AuditEvent)
    total_events_result = await session.execute(total_events_stmt)
    total_events = total_events_result.scalar_one()

    # Get total cells changed (sum of cell_count)
    total_cells_stmt = (
        select(func.sum(AuditEvent.cell_count))
        .where(AuditEvent.cell_count.isnot(None))
    )
    total_cells_result = await session.execute(total_cells_stmt)
    total_cells_changed = total_cells_result.scalar_one() or 0

    # Get total unique workbooks
    total_workbooks_stmt = (
        select(func.count(func.distinct(AuditEvent.workbook_name)))
        .where(AuditEvent.workbook_name.isnot(None))
    )
    total_workbooks_result = await session.execute(total_workbooks_stmt)
    total_workbooks = total_workbooks_result.scalar_one()

    # Get total unique sheets (need to count distinct workbook_name + sheet_name combinations)
    total_sheets_stmt = (
        select(func.count())
        .select_from(
            select(AuditEvent.workbook_name, AuditEvent.sheet_name)
            .where(AuditEvent.sheet_name.isnot(None))
            .distinct()
            .subquery()
        )
    )
    total_sheets_result = await session.execute(total_sheets_stmt)
    total_sheets = total_sheets_result.scalar_one()

    # Get total registered models (unique model names)
    total_models_stmt = select(func.count(func.distinct(RegisteredModel.model_name)))
    total_models_result = await session.execute(total_models_stmt)
    total_registered_models = total_models_result.scalar_one()

    # Get database size
    db_size = get_database_size()

    return {
        "total_registered_models": total_registered_models,
        "total_events": total_events,
        "total_cells_changed": total_cells_changed,
        "total_workbooks": total_workbooks,
        "total_sheets": total_sheets,
        "db_size": db_size,
    }


@router.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    """
    WebSocket endpoint for real-time dashboard updates.
    Pushes new events and metrics as they arrive without polling.
    """
    await websocket.accept()
    logger.info("websocket_connection_opened", client=websocket.client)

    try:
        # Get a database session
        async with db_manager.session() as session:
            # Send initial data
            initial_events = await get_events_since(session, None, limit=50)
            initial_metrics = await get_metrics_data(session)

            await websocket.send_json({
                "type": "initial",
                "metrics": initial_metrics,
                "events": initial_events,
                "timestamp": datetime.now(timezone.utc).isoformat()
            })

            # Track the most recent event timestamp we've sent
            last_sent_timestamp = None
            if initial_events:
                last_sent_timestamp = datetime.fromisoformat(initial_events[0]["timestamp"])

            # Keep connection alive and check for new events periodically
            while True:
                await asyncio.sleep(2)  # Check every 2 seconds

                async with db_manager.session() as check_session:
                    # Get new events since last check
                    new_events = await get_events_since(check_session, last_sent_timestamp, limit=50)

                    if new_events:
                        # Update last sent timestamp
                        last_sent_timestamp = datetime.fromisoformat(new_events[0]["timestamp"])

                        # Get updated metrics
                        updated_metrics = await get_metrics_data(check_session)

                        # Send update
                        await websocket.send_json({
                            "type": "update",
                            "metrics": updated_metrics,
                            "events": new_events,
                            "timestamp": datetime.now(timezone.utc).isoformat()
                        })

                        logger.debug("websocket_update_sent", event_count=len(new_events))

    except WebSocketDisconnect:
        logger.info("websocket_connection_closed", client=websocket.client)
    except Exception as e:
        logger.error("websocket_error", error=str(e), exc_info=True)
        try:
            await websocket.close()
        except:
            pass
