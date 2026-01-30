"""
Main FastAPI application with lifecycle management and middleware.
Entry point for the Domino Governance Tracker backend.
"""
from contextlib import asynccontextmanager
from typing import AsyncIterator

from fastapi import FastAPI, Request, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.middleware.gzip import GZipMiddleware
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles
import time
import os

from api import events, health, dashboard, models
from infrastructure.database import db_manager
from infrastructure.logger import configure_logging, get_logger
from services.background_service import BackgroundService
from config import get_settings

# Configure logging first
configure_logging()
logger = get_logger(__name__)

# Global background service instance
background_service: BackgroundService | None = None


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    """
    Application lifecycle manager.
    Handles startup and shutdown with proper resource management.

    Mirrors the frontend's lifecycle patterns:
    - Initialize resources on startup
    - Gracefully shutdown on exit
    - Never lose data
    """
    settings = get_settings()

    # ========== STARTUP ==========
    logger.info(
        "application_starting",
        app_name=settings.app_name,
        version=settings.app_version,
        environment=settings.environment
    )

    try:
        # Initialize database
        logger.info("initializing_database")
        await db_manager.initialize()

        # Create tables if they don't exist (for SQLite)
        if settings.is_sqlite:
            from models.database import Base
            async with db_manager.engine.begin() as conn:
                await conn.run_sync(Base.metadata.create_all)
            logger.info("database_tables_created")

        # Start background tasks
        global background_service
        if settings.enable_background_tasks:
            logger.info("starting_background_tasks")
            background_service = BackgroundService()
            await background_service.start()

        logger.info("application_started")

    except Exception as e:
        logger.error("application_startup_failed", error=str(e), exc_info=True)
        raise

    # Application is running
    yield

    # ========== SHUTDOWN ==========
    logger.info("application_shutting_down")

    try:
        # Stop background tasks
        if background_service:
            logger.info("stopping_background_tasks")
            await background_service.stop()

        # Close database connections
        logger.info("closing_database")
        await db_manager.shutdown()

        logger.info("application_shutdown_completed")

    except Exception as e:
        logger.error("application_shutdown_failed", error=str(e), exc_info=True)


# ============================================================================
# FastAPI Application
# ============================================================================

settings = get_settings()

app = FastAPI(
    title=settings.app_name,
    version=settings.app_version,
    description="High-performance backend for Excel compliance and governance tracking",
    lifespan=lifespan,
    docs_url="/docs",
    redoc_url="/redoc",
    openapi_url="/openapi.json"
)


# ============================================================================
# Middleware
# ============================================================================

# CORS - Allow Excel add-in to make requests
app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.cors_origins_list,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
    expose_headers=["*"]
)

# GZip compression for responses
app.add_middleware(GZipMiddleware, minimum_size=1000)


# Request logging middleware
@app.middleware("http")
async def log_requests(request: Request, call_next):
    """
    Log all requests with timing information.
    Provides observability for performance monitoring.
    """
    start_time = time.time()
    request_id = id(request)

    logger.info(
        "request_started",
        request_id=request_id,
        method=request.method,
        path=request.url.path,
        client=request.client.host if request.client else None
    )

    try:
        response = await call_next(request)
        processing_time = (time.time() - start_time) * 1000

        logger.info(
            "request_completed",
            request_id=request_id,
            method=request.method,
            path=request.url.path,
            status_code=response.status_code,
            processing_time_ms=processing_time
        )

        # Add timing header
        response.headers["X-Processing-Time"] = f"{processing_time:.2f}ms"

        return response

    except Exception as e:
        processing_time = (time.time() - start_time) * 1000

        logger.error(
            "request_failed",
            request_id=request_id,
            method=request.method,
            path=request.url.path,
            error=str(e),
            processing_time_ms=processing_time,
            exc_info=True
        )
        raise


# Global exception handler
@app.exception_handler(Exception)
async def global_exception_handler(request: Request, exc: Exception):
    """
    Global exception handler for unhandled errors.
    Ensures consistent error responses and logging.
    """
    logger.error(
        "unhandled_exception",
        method=request.method,
        path=request.url.path,
        error=str(exc),
        exc_info=True
    )

    return JSONResponse(
        status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
        content={
            "error": "Internal server error",
            "message": "An unexpected error occurred. Please try again later.",
            "path": request.url.path
        }
    )


# ============================================================================
# Routers
# ============================================================================

app.include_router(events.router)
app.include_router(health.router)
app.include_router(dashboard.router)
app.include_router(models.router)


# ============================================================================
# Static Files (Dashboard UI)
# ============================================================================

# Mount static files for the web dashboard
static_dir = os.path.join(os.path.dirname(__file__), "static")
if os.path.exists(static_dir):
    app.mount("/dashboard", StaticFiles(directory=static_dir, html=True), name="static")
    logger.info("dashboard_ui_mounted", path="/dashboard")
else:
    logger.warning("static_directory_not_found", path=static_dir)


# ============================================================================
# Root Endpoint
# ============================================================================

@app.get("/", tags=["root"])
async def root():
    """
    Root endpoint with service information.
    Provides basic service metadata and links to documentation.
    """
    return {
        "service": settings.app_name,
        "version": settings.app_version,
        "environment": settings.environment,
        "status": "operational",
        "docs": "/docs",
        "health": "/health",
        "dashboard": "/dashboard"
    }


# ============================================================================
# Domino Integration
# ============================================================================

# When running on Domino, import the fastapi_proxy module
# This patches the Domino model wrapper to support FastAPI/uvicorn
try:
    import fastapi_proxy
    logger.info("domino_fastapi_proxy_loaded")
except ImportError:
    logger.debug("domino_fastapi_proxy_not_available", reason="not_running_on_domino")


# ============================================================================
# Entry Point
# ============================================================================

if __name__ == "__main__":
    import uvicorn

    # Development server configuration
    uvicorn.run(
        "main:app",
        host=settings.host,
        port=settings.port,
        reload=settings.debug,
        log_level=settings.log_level.lower(),
        access_log=True
    )
