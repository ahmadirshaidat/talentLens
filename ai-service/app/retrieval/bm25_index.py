"""🔒 MANUAL — BM25 index, one per workspace."""

from app.models import Chunk


class BM25Index:
    """Keyword index over the chunks of a single workspace."""

    def __init__(self, workspace_id: str) -> None:
        """Create or load the BM25 index for `workspace_id`."""
        raise NotImplementedError("MANUAL: BM25 index init/load")

    def add(self, chunks: list[Chunk]) -> None:
        """Add chunks to the index and persist it."""
        raise NotImplementedError("MANUAL: BM25 add chunks")

    def delete_candidate(self, candidate_id: str) -> None:
        """Remove all chunks belonging to `candidate_id` and persist."""
        raise NotImplementedError("MANUAL: BM25 delete candidate")

    def search(self, query: str, top_k: int) -> list[tuple[str, float]]:
        """Return up to `top_k` (chunk_id, bm25_score) pairs, best first."""
        raise NotImplementedError("MANUAL: BM25 search")
