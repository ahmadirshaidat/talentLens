"""🔒 MANUAL — aggregate chunk scores into candidate scores."""

from app.models import Chunk


def aggregate_candidates(
    scored_chunks: list[tuple[Chunk, float]],
) -> list[tuple[str, float, list[Chunk]]]:
    """Group scored chunks by candidate and compute one score per candidate.

    Args:
        scored_chunks: (chunk, score) pairs from retrieval/reranking.

    Returns:
        (candidate_id, candidate_score, supporting_chunks) sorted by score descending.
    """
    raise NotImplementedError("MANUAL: aggregate chunk scores per candidate")
