"""🔒 MANUAL — end-to-end search pipeline."""

from app.models import CandidateResult


def search(
    workspace_id: str,
    query: str,
    top_k: int = 10,
    candidate_ids: list[str] | None = None,
) -> list[CandidateResult]:
    """Run the full search: parse query -> dense + BM25 -> RRF -> rerank -> aggregate.

    Args:
        workspace_id: Tenant scope; never search outside it.
        query: Recruiter query.
        top_k: Number of candidates to return.
        candidate_ids: Optional allow-list restricting the search.

    Returns:
        Ranked candidates with score, reason, and evidence.
    """
    raise NotImplementedError("MANUAL: end-to-end search pipeline")
