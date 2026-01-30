"""
Model repository for registered model database operations.
"""
from typing import Optional

from sqlalchemy import select, func
from sqlalchemy.ext.asyncio import AsyncSession

from models.database import RegisteredModel
from infrastructure.logger import get_logger

logger = get_logger(__name__)


class ModelRepository:
    """Repository for registered model database operations."""

    def __init__(self, session: AsyncSession):
        self.session = session

    async def get_by_id(self, model_id: str) -> Optional[RegisteredModel]:
        """Get a registered model by its ID."""
        result = await self.session.execute(
            select(RegisteredModel).where(RegisteredModel.model_id == model_id)
        )
        return result.scalar_one_or_none()

    async def get_latest_version(self, model_name: str) -> Optional[RegisteredModel]:
        """Get the latest version of a model by name."""
        result = await self.session.execute(
            select(RegisteredModel)
            .where(RegisteredModel.model_name == model_name)
            .order_by(RegisteredModel.version.desc())
            .limit(1)
        )
        return result.scalar_one_or_none()

    async def create(self, model: RegisteredModel) -> RegisteredModel:
        """Insert a new registered model."""
        try:
            self.session.add(model)
            await self.session.commit()
            await self.session.refresh(model)

            logger.info(
                "model_registered",
                model_id=model.model_id,
                model_name=model.model_name,
                version=model.version,
            )
            return model

        except Exception as e:
            await self.session.rollback()
            logger.error("model_registration_failed", error=str(e), exc_info=True)
            raise

    async def list_models(
        self,
        active_only: bool = True,
    ) -> list[RegisteredModel]:
        """List registered models (latest version per model_name)."""
        # Subquery to get max version per model_name
        max_version = (
            select(
                RegisteredModel.model_name,
                func.max(RegisteredModel.version).label("max_version"),
            )
            .group_by(RegisteredModel.model_name)
        )
        if active_only:
            max_version = max_version.where(RegisteredModel.is_active == True)
        max_version = max_version.subquery()

        stmt = (
            select(RegisteredModel)
            .join(
                max_version,
                (RegisteredModel.model_name == max_version.c.model_name)
                & (RegisteredModel.version == max_version.c.max_version),
            )
            .order_by(RegisteredModel.model_name)
        )

        result = await self.session.execute(stmt)
        return list(result.scalars().all())
