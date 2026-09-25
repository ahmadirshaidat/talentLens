"""🔒 MANUAL — cross-encoder reranking (BAAI/bge-reranker-v2-m3)."""

from app.models import Chunk


class Reranker:
    """Cross-encoder that rescores (query, chunk) pairs."""

    def __init__(self, model_name: str) -> None:
        """Load the cross-encoder model once."""
        raise NotImplementedError("MANUAL: load reranker model")

    def rerank(self, query: str, chunks: list[Chunk], top_k: int) -> list[tuple[Chunk, float]]:
        """Return the `top_k` chunks with reranker scores, best first."""
        raise NotImplementedError("MANUAL: cross-encoder reranking")
