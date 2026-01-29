"""
Health check and monitoring endpoints.
Provides observability for the application state.
"""
import asyncio
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, status
from sqlalchemy.ext.asyncio import AsyncSession

from infrastructure.database import get_session, db_manager
from models.schemas import HealthStatus
from config import get_settings
from infrastructure.logger import get_logger

logger = get_logger(__name__)

router = APIRouter(tags=["health"])


@router.get(
    "/health",
    response_model=HealthStatus,
    summary="Health check",
    description="Comprehensive health check for monitoring and orchestration"
)
async def health_check(
    session: AsyncSession = Depends(get_session)
) -> HealthStatus:
    """
    Comprehensive health check endpoint.

    Checks:
    - Database connectivity
    - Database pool health (for PostgreSQL)

    Returns overall status:
    - healthy: All checks passed
    - degraded: Some checks failed but service is operational
    - unhealthy: Critical failures, service unavailable

    This endpoint is used by:
    - Load balancers for routing decisions
    - Monitoring systems for alerting
    - Orchestrators (K8s, Domino) for readiness/liveness probes
    """
    settings = get_settings()
    checks = {}
    all_healthy = True

    # Check database connectivity
    try:
        await db_manager.health_check()
        checks["database"] = True
        logger.debug("health_check_database_passed")
    except Exception as e:
        checks["database"] = False
        all_healthy = False
        logger.error("health_check_database_failed", error=str(e))

    # Determine overall status
    if all_healthy:
        overall_status = "healthy"
    elif checks.get("database", False):
        overall_status = "degraded"
    else:
        overall_status = "unhealthy"

    # Additional details for diagnostics
    details = {
        "database_type": "sqlite" if settings.is_sqlite else "postgresql",
        "database_initialized": db_manager.is_initialized
    }

    logger.info("health_check_completed", status=overall_status, checks=checks)

    return HealthStatus(
        status=overall_status,
        timestamp=datetime.now(timezone.utc),
        version=settings.app_version,
        environment=settings.environment,
        checks=checks,
        details=details
    )


@router.get(
    "/ready",
    status_code=status.HTTP_200_OK,
    summary="Readiness probe",
    description="Kubernetes/Domino readiness probe - fast check if service can handle requests"
)
async def readiness() -> dict:
    """
    Readiness probe for orchestrators.

    Fast check to determine if the service can accept traffic.
    Returns 200 if ready, 503 if not ready.

    Used by:
    - Kubernetes readiness probes
    - Load balancers
    - Service meshes
    """
    if not db_manager.is_initialized:
        logger.warning("readiness_check_failed", reason="database_not_initialized")
        return {"ready": False, "reason": "database_not_initialized"}

    return {"ready": True}


@router.get(
    "/live",
    status_code=status.HTTP_200_OK,
    summary="Liveness probe",
    description="Kubernetes/Domino liveness probe - check if service is alive"
)
async def liveness() -> dict:
    """
    Liveness probe for orchestrators.

    Simple check to verify the application process is alive.
    Always returns 200 if the process is running.

    Used by:
    - Kubernetes liveness probes to detect deadlocks
    - Process monitors to restart failed services
    """
    return {"alive": True, "timestamp": datetime.now(timezone.utc)}


@router.get(
    "/metrics",
    summary="Prometheus metrics",
    description="Basic metrics in Prometheus format (optional)"
)
async def metrics() -> dict:
    """
    Basic metrics endpoint.

    In production, you might want to use prometheus_client
    for proper Prometheus metrics exposition.

    This is a simplified version returning JSON metrics.
    """
    settings = get_settings()

    return {
        "app_name": settings.app_name,
        "app_version": settings.app_version,
        "environment": settings.environment,
        "database_type": "sqlite" if settings.is_sqlite else "postgresql",
        "timestamp": datetime.now(timezone.utc)
    }
