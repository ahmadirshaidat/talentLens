"""POST /ingest — parse, chunk, extract, and index one CV."""

import logging
from typing import Annotated

from fastapi import APIRouter, Depends, File, Form, UploadFile

from app.api.common import SafeId
from app.chunking.section_chunker import chunk_sections, detect_sections
from app.config import Settings, get_settings
from app.embeddings.embedder import Embedder, get_embedder
from app.errors import FileTooLargeError
from app.extraction.extractor import extract_profile
from app.llm.client import LLMClient, get_llm_client
from app.models import CandidateProfile
from app.parsing.parser import parse_document
from app.retrieval.bm25_index import BM25Index
from app.storage.vector_store import VectorStore, get_vector_store

logger = logging.getLogger(__name__)
router = APIRouter(tags=["ingest"])


@router.post("/ingest", response_model=CandidateProfile)
def ingest(
    file: Annotated[UploadFile, File()],
    candidate_id: Annotated[SafeId, Form()],
    workspace_id: Annotated[SafeId, Form()],
    settings: Annotated[Settings, Depends(get_settings)],
    embedder: Annotated[Embedder, Depends(get_embedder)],
    store: Annotated[VectorStore, Depends(get_vector_store)],
    llm: Annotated[LLMClient, Depends(get_llm_client)],
) -> CandidateProfile:
    max_bytes = settings.max_upload_mb * 1024 * 1024
    data = file.file.read(max_bytes + 1)
    if len(data) > max_bytes:
        raise FileTooLargeError(f"File exceeds {settings.max_upload_mb} MB")

    parsed = parse_document(data, file.filename or "upload")

    sections = detect_sections(parsed.text)  # MANUAL
    chunks = chunk_sections(sections, candidate_id, workspace_id)  # MANUAL

    # Extract before indexing so an LLM failure doesn't leave a half-indexed candidate.
    profile = extract_profile(parsed.text, llm)  # MANUAL

    # Re-ingesting the same candidate replaces their previous chunks.
    bm25 = BM25Index(workspace_id)  # MANUAL
    store.delete_candidate(workspace_id, candidate_id)
    bm25.delete_candidate(candidate_id)

    store.add_chunks(chunks, embedder.embed_documents([c.text for c in chunks]))
    bm25.add(chunks)

    logger.info(
        "Ingested candidate %s (workspace %s): %d chunks", candidate_id, workspace_id, len(chunks)
    )
    return profile
