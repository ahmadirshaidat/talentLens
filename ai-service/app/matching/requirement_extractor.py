"""🔒 MANUAL — job description → structured requirements."""

from typing import TYPE_CHECKING

from app.matching.requirement_models import JobRequirements

if TYPE_CHECKING:
    from app.llm.client import LLMClient


def extract_requirements(job_text: str, llm: "LLMClient | None" = None) -> JobRequirements:
    """Turn a job description (Arabic, English or mixed) into structured requirements.

    Args:
        job_text: The job title + description + requirements as written by the employer.
        llm: Optional LLM client; a rule-based fallback should work without one.

    Returns:
        JobRequirements. Each requirement has a kind (skill, experience, education, language,
        location, employment_type, seniority), importance ("required" vs "preferred", e.g. from
        "nice to have" / "يفضل"), optional min_years, and the verbatim source_text it came from.
        Nothing may be invented that the job text does not say.
    """
    raise NotImplementedError("MANUAL: job requirement extraction")
