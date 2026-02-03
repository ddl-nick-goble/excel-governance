"""
Production application runner.
Runs the backend on port 8888 without auto-reload.
"""
import os

# Ensure we're in the correct directory
os.chdir(os.path.dirname(os.path.abspath(__file__)))

# Create data directory if it doesn't exist
os.makedirs("data", exist_ok=True)

print("=" * 70)
print(" Starting Domino Governance Tracker Backend")
print("=" * 70)
print()
print(" Environment: Production")
print(" Auto-reload: Disabled")
print(" API Docs: http://localhost:8888/docs")
print(" Health Check: http://localhost:8888/health")
print()
print("Press CTRL+C to stop the server")
print("=" * 70)
print()

if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        "main:app",
        host="0.0.0.0",
        port=8888,
        reload=False,
        log_level="info",
        access_log=True
    )
