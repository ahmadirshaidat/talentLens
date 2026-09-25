"""POST /search and POST /explain."""

from typing import Annotated

from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field

from app.api.common import SafeId
from app.explain.explainer import explain_candidate
from app.llm.client import LLMClient, get_llm_client
from app.models import CandidateResult
from app.retrieval import pipeline

router = APIRouter(tags=["search"])


class SearchRequest(BaseModel):
    workspace_id: SafeId
    query: str = Field(min_length=1, max_length=2000)
    top_k: int = Field(default=10, ge=1, le=100)
    candidate_ids: list[SafeId] | None = None


class ExplainRequest(BaseModel):
    candidate_id: SafeId
    workspace_id: SafeId
    query: str = Field(min_length=1, max_length=2000)


@router.post("/search", response_model=list[CandidateResult])
def search(request: SearchRequest) -> list[CandidateResult]:
    return pipeline.search(  # MANUAL
        workspace_id=request.workspace_id,
        query=request.query,
        top_k=request.top_k,
        candidate_ids=request.candidate_ids,
    )


@router.post("/explain", response_model=CandidateResult)
def explain(
    request: ExplainRequest,
    llm: Annotated[LLMClient, Depends(get_llm_client)],
) -> CandidateResult:
    return explain_candidate(  # MANUAL
        candidate_id=request.candidate_id,
        workspace_id=request.workspace_id,
        query=request.query,
        llm=llm,
    )
