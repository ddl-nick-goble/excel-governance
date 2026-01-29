"""
Basic tests for event API endpoints.
Run with: pytest tests/
"""
import pytest
from datetime import datetime, timezone
from uuid import uuid4

from httpx import AsyncClient
from fastapi import status

from main import app
from models.database import AuditEventType


@pytest.fixture
def sample_event():
    """Create a sample audit event for testing."""
    return {
        "eventId": str(uuid4()),
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "eventType": "CellChange",
        "userName": "test.user",
        "machineName": "TEST-PC",
        "sessionId": "test_session_123",
        "workbookName": "TestWorkbook.xlsx",
        "sheetName": "Sheet1",
        "cellAddress": "$A$1",
        "cellCount": 1,
        "newValue": "100"
    }


@pytest.mark.asyncio
async def test_health_check():
    """Test health check endpoint."""
    async with AsyncClient(app=app, base_url="http://test") as client:
        response = await client.get("/health")
        assert response.status_code == status.HTTP_200_OK
        data = response.json()
        assert data["status"] in ["healthy", "degraded", "unhealthy"]
        assert "version" in data
        assert "checks" in data


@pytest.mark.asyncio
async def test_readiness_probe():
    """Test readiness probe."""
    async with AsyncClient(app=app, base_url="http://test") as client:
        response = await client.get("/ready")
        assert response.status_code == status.HTTP_200_OK
        data = response.json()
        assert "ready" in data


@pytest.mark.asyncio
async def test_liveness_probe():
    """Test liveness probe."""
    async with AsyncClient(app=app, base_url="http://test") as client:
        response = await client.get("/live")
        assert response.status_code == status.HTTP_200_OK
        data = response.json()
        assert data["alive"] is True


@pytest.mark.asyncio
async def test_ingest_single_event(sample_event):
    """Test ingesting a single event."""
    async with AsyncClient(app=app, base_url="http://test") as client:
        response = await client.post(
            "/api/events",
            json={"events": [sample_event]}
        )
        assert response.status_code == status.HTTP_202_ACCEPTED
        data = response.json()
        assert data["accepted"] >= 0
        assert "processing_time_ms" in data


@pytest.mark.asyncio
async def test_ingest_batch_events(sample_event):
    """Test ingesting multiple events in a batch."""
    # Create 10 events
    events = []
    for i in range(10):
        event = sample_event.copy()
        event["eventId"] = str(uuid4())
        event["cellAddress"] = f"$A${i+1}"
        events.append(event)

    async with AsyncClient(app=app, base_url="http://test") as client:
        response = await client.post(
            "/api/events",
            json={"events": events}
        )
        assert response.status_code == status.HTTP_202_ACCEPTED
        data = response.json()
        assert data["accepted"] >= 0


@pytest.mark.asyncio
async def test_query_events():
    """Test querying events."""
    async with AsyncClient(app=app, base_url="http://test") as client:
        response = await client.post(
            "/api/events/query",
            json={
                "offset": 0,
                "limit": 10,
                "order_by": "timestamp",
                "order_desc": True
            }
        )
        assert response.status_code == status.HTTP_200_OK
        data = response.json()
        assert "total" in data
        assert "events" in data
        assert isinstance(data["events"], list)


@pytest.mark.asyncio
async def test_get_statistics():
    """Test statistics endpoint."""
    async with AsyncClient(app=app, base_url="http://test") as client:
        response = await client.get("/api/events/statistics")
        assert response.status_code == status.HTTP_200_OK
        data = response.json()
        assert "total_events" in data
        assert "events_by_type" in data
        assert "unique_users" in data


@pytest.mark.asyncio
async def test_invalid_event_type():
    """Test that invalid event types are rejected."""
    invalid_event = {
        "eventId": str(uuid4()),
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "eventType": "InvalidEventType",  # Not a valid AuditEventType
        "userName": "test.user"
    }

    async with AsyncClient(app=app, base_url="http://test") as client:
        response = await client.post(
            "/api/events",
            json={"events": [invalid_event]}
        )
        # Should fail validation
        assert response.status_code == status.HTTP_422_UNPROCESSABLE_ENTITY
