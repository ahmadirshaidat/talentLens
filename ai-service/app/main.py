"""FastAPI app: routers, error handlers, /health.

Run: uvicorn app.main:app --reload
"""

import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from app.api import candidates, ingest, search
from app.config import get_settings
from app.embeddings.embedder import get_embedder
from app.errors import TalentLensError
from app.logging_config import setup_logging

logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    settings = get_settings()
    setup_logging(settings.log_level)
    for directory in (settings.data_dir, settings.chroma_dir, settings.bm25_dir):
        directory.mkdir(parents=True, exist_ok=True)
    logger.info("AI service started (embedding model: %s)", settings.embedding_model)
    yield


def create_app() -> FastAPI:
    app = FastAPI(title="TalentLens AI Service", version="0.1.0", lifespan=lifespan)

    app.include_router(ingest.router)
    app.include_router(search.router)
    app.include_router(candidates.router)

    @app.exception_handler(TalentLensError)
    async def handle_talentlens_error(request: Request, exc: TalentLensError) -> JSONResponse:
        logger.warning("%s on %s: %s", type(exc).__name__, request.url.path, exc)
        return JSONResponse(status_code=exc.status_code, content={"detail": str(exc)})

    @app.exception_handler(NotImplementedError)
    async def handle_not_implemented(request: Request, exc: NotImplementedError) -> JSONResponse:
        # Surfaces pending MANUAL stubs clearly instead of a generic 500.
        logger.warning("Not implemented on %s: %s", request.url.path, exc)
        return JSONResponse(status_code=501, content={"detail": str(exc)})

    @app.get("/health", tags=["health"])
    def health() -> dict:
        return {"status": "ok", "embedding_model_loaded": get_embedder().is_loaded}

    return app


app = create_app()
