"""
Domino entrypoint script.
Launches FastAPI with uvicorn on Domino platform.

This script is called by Domino's model wrapper.
It imports fastapi_proxy to enable FastAPI/uvicorn support on Domino.
"""
import os
import sys

# CRITICAL: Import fastapi_proxy first to patch Domino's model wrapper
# This enables FastAPI/uvicorn support on Domino platform
try:
    import fastapi_proxy
    print("✓ FastAPI proxy loaded successfully")
except ImportError:
    print("⚠ Warning: fastapi_proxy not available. Running in standalone mode.")
    print("  This is expected in local development.")

# Add current directory to Python path
sys.path.insert(0, os.path.dirname(__file__))

# Import and configure application
from main import app
from config import get_settings

settings = get_settings()

# Log startup information
print(f"Starting {settings.app_name} v{settings.app_version}")
print(f"Environment: {settings.environment}")
print(f"Database: {settings.database_url.split('@')[0] if '@' in settings.database_url else 'sqlite'}")
print(f"Port: {settings.fastapi_port}")

# The app object is now available for Domino's model wrapper
# Domino will automatically start uvicorn with this app
if __name__ == "__main__":
    import uvicorn

    # When running directly (not through Domino)
    uvicorn.run(
        app,
        host=settings.fastapi_host,
        port=settings.fastapi_port,
        log_level=settings.log_level.lower()
    )
