"""🔒 MANUAL — "why this candidate" with quoted evidence."""

from typing import TYPE_CHECKING

from app.models import CandidateResult

if TYPE_CHECKING:
    from app.llm.client import LLMClient


def explain_candidate(
    candidate_id: str, workspace_id: str, query: str, llm: "LLMClient"
) -> CandidateResult:
    """Explain why a candidate matches the query.

    Args:
        candidate_id: Candidate to explain.
        workspace_id: Tenant scope.
        query: Recruiter query.
        llm: LLM client used to write the reason.

    Returns:
        CandidateResult whose evidence quotes come verbatim from the CV.
    """
    raise NotImplementedError("MANUAL: explain candidate with quoted evidence")
