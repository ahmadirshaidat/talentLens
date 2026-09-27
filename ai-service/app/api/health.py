"""GET /health — liveness and model status. No auth, no personal data."""

from typing import Annotated

from fastapi import APIRouter, Depends

from app.config import Settings, get_settings
from app.embeddings.embedder import get_embedder
from app.retrieval.reranker import get_reranker

router = APIRouter(tags=["health"])


@router.get("/health")
def health(settings: Annotated[Settings, Depends(get_settings)]) -> dict:
    return {
        "status": "ok",
        "version": "0.2.0",
        "environment": settings.environment,
        # Models load lazily on first use; False just means "not warmed up yet".
        "embedding_model_loaded": get_embedder().is_loaded,
        "reranker_loaded": get_reranker().is_loaded if settings.use_reranker else None,
        "llm_configured": bool(settings.llm_model),
        "auth_required": bool(settings.service_api_key),
    }
