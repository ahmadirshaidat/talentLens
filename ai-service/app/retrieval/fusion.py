"""🔒 MANUAL — Reciprocal Rank Fusion."""


def reciprocal_rank_fusion(rankings: list[list[str]], k: int = 60) -> list[tuple[str, float]]:
    """Fuse several ranked lists of ids into one.

    Args:
        rankings: Each list is ids ordered best-first (e.g. dense and BM25 results).
        k: RRF smoothing constant.

    Returns:
        (id, fused_score) pairs sorted by score descending.
    """
    raise NotImplementedError("MANUAL: reciprocal rank fusion")
