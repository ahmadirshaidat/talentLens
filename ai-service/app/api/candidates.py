"""DELETE /candidates/{candidate_id} — remove a candidate from vectors + BM25."""

from typing import Annotated

from fastapi import APIRouter, Depends, Path, Query, Response, status

from app.api.common import SafeId
from app.retrieval.bm25_index import BM25Index
from app.storage.vector_store import VectorStore, get_vector_store

router = APIRouter(tags=["candidates"])


@router.delete("/candidates/{candidate_id}", status_code=status.HTTP_204_NO_CONTENT)
def delete_candidate(
    candidate_id: Annotated[SafeId, Path()],
    workspace_id: Annotated[SafeId, Query()],
    store: Annotated[VectorStore, Depends(get_vector_store)],
) -> Response:
    store.delete_candidate(workspace_id, candidate_id)
    BM25Index(workspace_id).delete_candidate(candidate_id)  # MANUAL
    return Response(status_code=status.HTTP_204_NO_CONTENT)
