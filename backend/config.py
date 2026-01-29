"""
Configuration management for DGT backend.
Uses pydantic-settings for validation and environment variable loading.
"""
from functools import lru_cache
from typing import Optional
from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Application settings with validation and environment variable support."""

    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        case_sensitive=False,
        extra="ignore"
    )

    # Application
    app_name: str = Field(default="domino-governance-backend", description="Application name")
    app_version: str = Field(default="1.0.0", description="Application version")
    environment: str = Field(default="development", description="Environment (development/production)")
    debug: bool = Field(default=False, description="Debug mode")
    log_level: str = Field(default="INFO", description="Logging level")

    # Server
    host: str = Field(default="0.0.0.0", description="Server host")
    port: int = Field(default=5000, description="Server port")
    workers: int = Field(default=4, description="Number of worker processes")

    # Database
    database_url: str = Field(
        default="sqlite+aiosqlite:///./data/dgt.db",
        description="Database connection URL (async driver required)"
    )
    db_pool_size: int = Field(default=20, description="Database connection pool size")
    db_max_overflow: int = Field(default=40, description="Max connections beyond pool size")
    db_pool_timeout: int = Field(default=30, description="Pool checkout timeout (seconds)")
    db_pool_recycle: int = Field(default=3600, description="Recycle connections after N seconds")
    db_echo: bool = Field(default=False, description="Echo SQL statements (debug)")

    # Security
    api_key: Optional[str] = Field(default=None, description="API key for authentication")
    cors_origins: str = Field(default="*", description="CORS allowed origins (comma-separated)")

    # Event Processing
    max_batch_size: int = Field(default=1000, description="Max events per batch insert")
    event_retention_days: int = Field(default=90, description="Days to retain events before archival")
    enable_background_tasks: bool = Field(default=True, description="Enable background cleanup tasks")
    cleanup_interval_hours: int = Field(default=24, description="Hours between cleanup runs")

    # Performance
    query_timeout_seconds: int = Field(default=30, description="Query timeout")
    max_query_results: int = Field(default=10000, description="Max results per query")

    # Domino Integration
    fastapi_host: str = Field(default="127.0.0.1", description="FastAPI host for Domino proxy")
    fastapi_port: int = Field(default=8000, description="FastAPI port for Domino proxy")

    @field_validator("log_level")
    @classmethod
    def validate_log_level(cls, v: str) -> str:
        """Ensure log level is valid."""
        valid_levels = {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"}
        v = v.upper()
        if v not in valid_levels:
            raise ValueError(f"Invalid log level. Must be one of: {valid_levels}")
        return v

    @field_validator("environment")
    @classmethod
    def validate_environment(cls, v: str) -> str:
        """Ensure environment is valid."""
        valid_envs = {"development", "staging", "production"}
        v = v.lower()
        if v not in valid_envs:
            raise ValueError(f"Invalid environment. Must be one of: {valid_envs}")
        return v

    @property
    def is_production(self) -> bool:
        """Check if running in production."""
        return self.environment == "production"

    @property
    def is_sqlite(self) -> bool:
        """Check if using SQLite database."""
        return "sqlite" in self.database_url.lower()

    @property
    def cors_origins_list(self) -> list[str]:
        """Get CORS origins as a list."""
        if self.cors_origins == "*":
            return ["*"]
        return [origin.strip() for origin in self.cors_origins.split(",")]


@lru_cache
def get_settings() -> Settings:
    """
    Get cached settings instance.
    Uses lru_cache to ensure singleton pattern.
    """
    return Settings()
