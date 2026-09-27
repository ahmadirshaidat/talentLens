"""FastAPI app: routers, service auth, request context, error handlers.

Run: uvicorn app.main:app --reload
"""

import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Request
from fastapi.responses import JSONResponse

from app.api import candidates, health, ingest, matching, search
from app.config import get_settings
from app.errors import TalentLensError
from app.logging_config import setup_logging
from app.observability import RequestContextMiddleware
from app.security import require_service_key

logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    settings = get_settings()
    setup_logging(settings.log_level)
    if not settings.service_api_key:
        if settings.environment.lower() == "production":
            raise RuntimeError("SERVICE_API_KEY must be set in production")
        logger.warning("SERVICE_API_KEY is not set: API calls are not authenticated (dev only)")
    for directory in (
        settings.data_dir,
        settings.chroma_dir,
        settings.bm25_dir,
        settings.profiles_dir,
    ):
        directory.mkdir(parents=True, exist_ok=True)
    logger.info("AI service started (embedding model: %s)", settings.embedding_model)
    yield


def create_app() -> FastAPI:
    app = FastAPI(title="TalentLens AI Service", version="0.2.0", lifespan=lifespan)
    app.add_middleware(RequestContextMiddleware)

    # Everything except /health is for the web backend only.
    protected = [Depends(require_service_key)]
    app.include_router(health.router)
    app.include_router(ingest.router, dependencies=protected)
    app.include_router(search.router, dependencies=protected)
    app.include_router(candidates.router, dependencies=protected)
    app.include_router(matching.router, dependencies=protected)

    @app.exception_handler(TalentLensError)
    async def handle_talentlens_error(request: Request, exc: TalentLensError) -> JSONResponse:
        logger.warning("%s on %s: %s", type(exc).__name__, request.url.path, exc)
        return JSONResponse(status_code=exc.status_code, content={"detail": str(exc)})

    @app.exception_handler(NotImplementedError)
    async def handle_not_implemented(request: Request, exc: NotImplementedError) -> JSONResponse:
        # Surfaces pending MANUAL stubs clearly instead of a generic 500.
        logger.warning("Not implemented on %s: %s", request.url.path, exc)
        return JSONResponse(status_code=501, content={"detail": str(exc)})

    @app.exception_handler(Exception)
    async def handle_unexpected(request: Request, exc: Exception) -> JSONResponse:
        # Safe error message: details go to the log (with the request id), not to the caller.
        logger.exception("Unhandled error on %s", request.url.path)
        return JSONResponse(status_code=500, content={"detail": "Internal error"})

    return app


app = create_app()
