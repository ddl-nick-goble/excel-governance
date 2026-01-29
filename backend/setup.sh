#!/bin/bash

# Development setup script for Domino Governance Tracker Backend
# Run this to set up a local development environment

set -e  # Exit on error

echo "=================================="
echo "DGT Backend - Development Setup"
echo "=================================="
echo ""

# Check Python version
echo "Checking Python version..."
python_version=$(python3 --version 2>&1 | awk '{print $2}')
echo "Found Python $python_version"

# Create virtual environment
echo ""
echo "Creating virtual environment..."
python3 -m venv venv

# Activate virtual environment
echo "Activating virtual environment..."
source venv/bin/activate 2>/dev/null || source venv/Scripts/activate 2>/dev/null || {
    echo "Could not activate virtual environment."
    echo "Please activate manually:"
    echo "  Linux/Mac: source venv/bin/activate"
    echo "  Windows: venv\\Scripts\\activate"
    exit 1
}

# Upgrade pip
echo ""
echo "Upgrading pip..."
pip install --upgrade pip

# Install dependencies
echo ""
echo "Installing dependencies..."
pip install -r requirements.txt

# Create directories
echo ""
echo "Creating directories..."
mkdir -p data
mkdir -p logs

# Copy environment file
echo ""
if [ ! -f .env ]; then
    echo "Creating .env file from template..."
    cp .env.example .env
    echo "✓ .env created. Please review and customize if needed."
else
    echo "✓ .env already exists"
fi

# Initialize database (SQLite)
echo ""
echo "Initializing database..."
python3 << EOF
import asyncio
from infrastructure.database import db_manager
from models.database import Base

async def init():
    await db_manager.initialize()
    async with db_manager.engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)
    print("✓ Database initialized")
    await db_manager.shutdown()

asyncio.run(init())
EOF

echo ""
echo "=================================="
echo "Setup Complete!"
echo "=================================="
echo ""
echo "To start the development server:"
echo "  1. Activate virtual environment (if not already active):"
echo "     source venv/bin/activate"
echo "  2. Run the server:"
echo "     python run_dev.py"
echo ""
echo "API will be available at:"
echo "  - http://localhost:5000"
echo "  - http://localhost:5000/docs (Swagger UI)"
echo "  - http://localhost:5000/health (Health check)"
echo ""
