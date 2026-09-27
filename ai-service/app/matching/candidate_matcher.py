"""🔒 MANUAL — rank candidates against structured job requirements."""

from app.matching.requirement_models import CandidateJobMatch, JobRequirements


def match_candidates(
    workspace_id: str,
    requirements: JobRequirements,
    top_k: int = 10,
    candidate_ids: list[str] | None = None,
) -> list[CandidateJobMatch]:
    """Find and rank the best candidates for a job, requirement by requirement.

    Suggested flow: retrieve candidates (hybrid search with the requirements as the query,
    scoped to `workspace_id` and the optional `candidate_ids` allow-list) → gather each
    candidate's evidence chunks → compare every requirement (see match_explainer) → rerank →
    return candidates, best first.

    Args:
        workspace_id: Tenant scope; never read outside it.
        requirements: Output of extract_requirements (or built by the backend).
        top_k: Number of candidates to return.
        candidate_ids: Optional allow-list (e.g. the job's applicants).

    Returns:
        CandidateJobMatch list. `score` is a ranking signal, not a hiring probability.
    """
    raise NotImplementedError("MANUAL: evidence-based candidate matching")
