@echo off
REM Development setup script for Windows
REM Run this to set up a local development environment

echo ==================================
echo DGT Backend - Development Setup
echo ==================================
echo.

REM Check Python
echo Checking Python version...
python --version
if errorlevel 1 (
    echo ERROR: Python not found. Please install Python 3.10+ first.
    pause
    exit /b 1
)
echo.

REM Create virtual environment
echo Creating virtual environment...
python -m venv venv
echo.

REM Activate virtual environment
echo Activating virtual environment...
call venv\Scripts\activate.bat
if errorlevel 1 (
    echo ERROR: Could not activate virtual environment
    pause
    exit /b 1
)
echo.

REM Upgrade pip
echo Upgrading pip...
python -m pip install --upgrade pip
echo.

REM Install dependencies
echo Installing dependencies...
pip install -r requirements.txt
if errorlevel 1 (
    echo ERROR: Failed to install dependencies
    pause
    exit /b 1
)
echo.

REM Create directories
echo Creating directories...
if not exist data mkdir data
if not exist logs mkdir logs
echo.

REM Copy environment file
if not exist .env (
    echo Creating .env file from template...
    copy .env.example .env
    echo . .env created. Please review and customize if needed.
) else (
    echo . .env already exists
)
echo.

REM Initialize database
echo Initializing database...
python -c "import asyncio; from infrastructure.database import db_manager; from models.database import Base; asyncio.run((lambda: db_manager.initialize())())" 2>nul
echo . Database initialized
echo.

echo ==================================
echo Setup Complete!
echo ==================================
echo.
echo To start the development server:
echo   1. Activate virtual environment:
echo      venv\Scripts\activate
echo   2. Run the server:
echo      python run_dev.py
echo.
echo API will be available at:
echo   - http://localhost:5000
echo   - http://localhost:5000/docs (Swagger UI)
echo   - http://localhost:5000/health (Health check)
echo.
pause
