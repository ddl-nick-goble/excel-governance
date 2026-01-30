"""
Model registration API endpoints.
Provides REST API for registering, looking up, and listing workbook models.
"""
from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy.ext.asyncio import AsyncSession

from infrastructure.database import get_session
from models.schemas import ModelRegistrationRequest, ModelResponse
from services.model_service import ModelService
from infrastructure.logger import get_logger

logger = get_logger(__name__)

router = APIRouter(prefix="/api/models", tags=["models"])


@router.post(
    "/register",
    response_model=ModelResponse,
    status_code=status.HTTP_201_CREATED,
    summary="Register or re-register a workbook model",
)
async def register_model(
    request: ModelRegistrationRequest,
    session: AsyncSession = Depends(get_session),
) -> ModelResponse:
    """
    Register a new model or re-register (fork) an existing one.

    - New registration: omit existingModelId → creates version 1
    - Re-registration: provide existingModelId → creates version N+1 with new modelId
      (modelName must match; description can be updated)
    """
    logger.info(
        "model_registration_request",
        model_name=request.model_name,
        existing_model_id=request.existing_model_id,
        registered_by=request.registered_by,
    )

    service = ModelService(session)

    try:
        result = await service.register(request)
    except ValueError as e:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=str(e))

    logger.info(
        "model_registered",
        model_id=result.model_id,
        model_name=result.model_name,
        version=result.version,
    )
    return result


@router.get(
    "/{model_id}",
    response_model=ModelResponse,
    summary="Get a registered model by ID",
)
async def get_model(
    model_id: str,
    session: AsyncSession = Depends(get_session),
) -> ModelResponse:
    """Look up a registered model by its ID. Used by the add-in on workbook open."""
    service = ModelService(session)
    result = await service.get_model(model_id)

    if result is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Model '{model_id}' not found",
        )

    return result


@router.get(
    "",
    response_model=list[ModelResponse],
    summary="List registered models",
)
async def list_models(
    active_only: bool = True,
    session: AsyncSession = Depends(get_session),
) -> list[ModelResponse]:
    """List registered models (latest version per model name)."""
    service = ModelService(session)
    return await service.list_models(active_only=active_only)
