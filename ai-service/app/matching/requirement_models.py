"""Contracts for job-requirement extraction and evidence-based candidate matching.

A job is turned into structured requirements; each candidate is then compared requirement by
requirement. Every comparison says whether evidence was FOUND, NOT FOUND, or NEEDS VERIFICATION —
absence of evidence is not proof of absence, and scores are ranking signals, never a hiring
probability.
"""

from typing import Literal

from pydantic import BaseModel, Field

from app.models import Evidence

RequirementKind = Literal[
    "skill", "experience", "education", "language", "location", "employment_type", "seniority"
]
Importance = Literal["required", "preferred"]
EvidenceStatus = Literal["found", "not_found", "needs_verification"]


class JobRequirement(BaseModel):
    """One thing the job asks for, e.g. skill "ASP.NET Core" (required)."""

    kind: RequirementKind
    value: str = Field(min_length=1, max_length=200)  # "ASP.NET Core", "Bachelor in CS", "Arabic"
    importance: Importance = "required"
    min_years: float | None = Field(default=None, ge=0, le=60)  # for kind == "experience"
    source_text: str | None = None  # verbatim span of the job description it came from


class JobRequirements(BaseModel):
    """A job description in structured form."""

    title: str | None = None
    language: Literal["ar", "en", "mixed"] | None = None
    requirements: list[JobRequirement]


class RequirementMatch(BaseModel):
    """How one requirement compares with one candidate's CV/profile."""

    requirement: JobRequirement
    status: EvidenceStatus
    evidence: list[Evidence] = []  # verbatim quotes; empty when status == "not_found"
    # Neutral wording only, e.g. "Not found in available CV/profile evidence" —
    # never "the candidate does not have X".
    note: str | None = None


class CandidateJobMatch(BaseModel):
    """A candidate ranked against a job, with requirement-level evidence."""

    candidate_id: str
    score: float = Field(ge=0, le=1)  # ranking signal only — NOT a probability of being hired
    summary: str
    requirements: list[RequirementMatch]
