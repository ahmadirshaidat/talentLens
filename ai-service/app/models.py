"""Pydantic contracts shared across the AI service."""

from typing import Literal

from pydantic import BaseModel


class ParsedDocument(BaseModel):
    filename: str
    file_type: Literal["pdf", "docx"]
    text: str
    page_count: int
    char_count: int


class CVSection(BaseModel):
    name: str  # "experience" | "skills" | "education" | "projects" | "summary" | "other"
    text: str


class Chunk(BaseModel):
    chunk_id: str
    candidate_id: str
    workspace_id: str
    section: str
    text: str


class CandidateProfile(BaseModel):
    """Output of structured extraction."""

    full_name: str | None
    email: str | None
    phone: str | None
    location: str | None
    years_of_experience: float | None
    skills: list[str]
    languages: list[str]
    job_titles: list[str]
    education: list[dict]


class Evidence(BaseModel):
    section: str
    quote: str


class CandidateResult(BaseModel):
    candidate_id: str
    score: float
    reason: str
    evidence: list[Evidence]
