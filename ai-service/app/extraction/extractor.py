"""🔒 MANUAL — LLM structured extraction (prompt + JSON schema + validation/retry)."""

from typing import TYPE_CHECKING

from app.models import CandidateProfile

if TYPE_CHECKING:
    from app.llm.client import LLMClient


def extract_profile(cv_text: str, llm: "LLMClient") -> CandidateProfile:
    """Extract a structured candidate profile from CV text.

    Args:
        cv_text: Full parsed CV text.
        llm: LLM client used for the extraction call.

    Returns:
        A validated CandidateProfile. Should retry on invalid JSON / schema errors.
    """
    raise NotImplementedError("MANUAL: LLM structured extraction with validation/retry")
