"""
Model registration service with business logic.
Handles registration, re-registration (forking), and lookups.
"""
from uuid import uuid4

from sqlalchemy.ext.asyncio import AsyncSession

from models.database import RegisteredModel
from models.schemas import ModelRegistrationRequest, ModelResponse
from repositories.model_repository import ModelRepository
from infrastructure.logger import get_logger

logger = get_logger(__name__)


class ModelService:
    """Service for model registration operations."""

    def __init__(self, session: AsyncSession):
        self.session = session
        self.repo = ModelRepository(session)

    async def register(self, request: ModelRegistrationRequest) -> ModelResponse:
        """
        Register a new model or re-register (fork) an existing one.

        - If existing_model_id is None: create version 1.
        - If existing_model_id is provided: look up the existing model,
          verify model_name matches, create a new row with version+1 and a new model_id.
        """
        version = 1

        if request.existing_model_id:
            existing = await self.repo.get_by_id(request.existing_model_id)
            if existing is None:
                raise ValueError(
                    f"Existing model ID '{request.existing_model_id}' not found"
                )
            if existing.model_name != request.model_name:
                raise ValueError(
                    f"Model name mismatch: existing is '{existing.model_name}', "
                    f"request has '{request.model_name}'. "
                    "Model name cannot be changed on re-register."
                )
            version = existing.version + 1

            logger.info(
                "model_reregister",
                existing_model_id=request.existing_model_id,
                model_name=request.model_name,
                new_version=version,
            )

        model = RegisteredModel(
            model_id=str(uuid4()),
            model_name=request.model_name,
            description=request.description,
            version=version,
            registered_by=request.registered_by,
            machine_name=request.machine_name,
        )

        created = await self.repo.create(model)

        return ModelResponse(
            model_id=created.model_id,
            model_name=created.model_name,
            description=created.description,
            version=created.version,
            registered_by=created.registered_by,
            machine_name=created.machine_name,
            is_active=created.is_active,
            created_at=created.created_at,
        )

    async def get_model(self, model_id: str) -> ModelResponse | None:
        """Get a registered model by ID. Returns None if not found."""
        model = await self.repo.get_by_id(model_id)
        if model is None:
            return None

        return ModelResponse(
            model_id=model.model_id,
            model_name=model.model_name,
            description=model.description,
            version=model.version,
            registered_by=model.registered_by,
            machine_name=model.machine_name,
            is_active=model.is_active,
            created_at=model.created_at,
        )

    async def list_models(self, active_only: bool = True) -> list[ModelResponse]:
        """List registered models (latest version per model_name)."""
        models = await self.repo.list_models(active_only=active_only)
        return [
            ModelResponse(
                model_id=m.model_id,
                model_name=m.model_name,
                description=m.description,
                version=m.version,
                registered_by=m.registered_by,
                machine_name=m.machine_name,
                is_active=m.is_active,
                created_at=m.created_at,
            )
            for m in models
        ]
