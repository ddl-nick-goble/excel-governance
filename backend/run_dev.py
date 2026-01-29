"""
Development server runner with auto-reload.
Quick start script for local development.
"""
import os
import sys

# Ensure we're in the correct directory
os.chdir(os.path.dirname(os.path.abspath(__file__)))

# Create data directory if it doesn't exist
os.makedirs("data", exist_ok=True)

print("=" * 70)
print(" Starting Domino Governance Tracker Backend")
print("=" * 70)
print()
print(" Environment: Development")
print(" Auto-reload: Enabled")
print(" API Docs: http://localhost:5000/docs")
print(" Health Check: http://localhost:5000/health")
print()
print("Press CTRL+C to stop the server")
print("=" * 70)
print()

# Run uvicorn with auto-reload
if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        "main:app",
        host="0.0.0.0",
        port=5000,
        reload=True,
        log_level="info",
        access_log=True
    )
