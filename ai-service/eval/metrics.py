"""🔒 MANUAL — retrieval metrics."""


def precision_at_k(retrieved: list[str], relevant: set[str], k: int) -> float:
    """Fraction of the top-k retrieved ids that are relevant."""
    raise NotImplementedError("MANUAL: Precision@k")


def ndcg_at_k(retrieved: list[str], relevance: dict[str, float], k: int) -> float:
    """Normalized DCG of the top-k retrieved ids given graded relevance."""
    raise NotImplementedError("MANUAL: nDCG@k")
