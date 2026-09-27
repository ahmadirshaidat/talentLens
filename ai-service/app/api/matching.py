"""POST /extract/job-requirements and POST /match/job — wiring only.

The logic lives in app/matching/* (MANUAL). Until Ahmad implements it these endpoints return
501 with the "MANUAL: …" message, via the NotImplementedError handler in app.main.
"""

from typing import Annotated

from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field, model_validator

from app.api.common import SafeId
from app.llm.client import LLMClient, get_llm_client
from app.matching import candidate_matcher, requirement_extractor
from app.matching.requirement_models import CandidateJobMatch, JobRequirements

router = APIRouter(tags=["matching"])


class ExtractRequirementsRequest(BaseModel):
    workspace_id: SafeId
    job_text: str = Field(min_length=1, max_length=8000)


class MatchJobRequest(BaseModel):
    workspace_id: SafeId
    job_text: str | None = Field(default=None, min_length=1, max_length=8000)
    requirements: JobRequirements | None = None
    top_k: int = Field(default=10, ge=1, le=100)
    candidate_ids: list[SafeId] | None = None

    @model_validator(mode="after")
    def exactly_one_job_source(self) -> "MatchJobRequest":
        if (self.job_text is None) == (self.requirements is None):
            raise ValueError("Provide exactly one of job_text or requirements")
        return self


@router.post("/extract/job-requirements", response_model=JobRequirements)
def extract_job_requirements(
    request: ExtractRequirementsRequest,
    llm: Annotated[LLMClient, Depends(get_llm_client)],
) -> JobRequirements:
    return requirement_extractor.extract_requirements(request.job_text, llm)


@router.post("/match/job", response_model=list[CandidateJobMatch])
def match_job(
    request: MatchJobRequest,
    llm: Annotated[LLMClient, Depends(get_llm_client)],
) -> list[CandidateJobMatch]:
    requirements = request.requirements or requirement_extractor.extract_requirements(
        request.job_text or "", llm
    )
    return candidate_matcher.match_candidates(
        workspace_id=request.workspace_id,
        requirements=requirements,
        top_k=request.top_k,
        candidate_ids=request.candidate_ids,
    )
