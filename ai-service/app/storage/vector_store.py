"""Thin ChromaDB wrapper: one collection per workspace.

Workspace isolation is structural: every method takes a workspace_id and only
ever touches that workspace's collection.
"""

import logging
import re
from functools import lru_cache
from pathlib import Path

import chromadb

from app.config import get_settings
from app.models import Chunk

logger = logging.getLogger(__name__)

_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,64}$")


def _collection_name(workspace_id: str) -> str:
    if not _ID_PATTERN.fullmatch(workspace_id):
        raise ValueError(f"Invalid workspace_id: {workspace_id!r}")
    return f"ws_{workspace_id}"


class VectorStore:
    def __init__(self, persist_dir: Path) -> None:
        persist_dir.mkdir(parents=True, exist_ok=True)
        self._client = chromadb.PersistentClient(path=str(persist_dir))

    def _collection(self, workspace_id: str):
        return self._client.get_or_create_collection(
            name=_collection_name(workspace_id),
            metadata={"hnsw:space": "cosine"},
        )

    def add_chunks(self, chunks: list[Chunk], embeddings: list[list[float]]) -> None:
        """Upsert chunks (all from the same workspace) with their embeddings."""
        if not chunks:
            return
        if len(chunks) != len(embeddings):
            raise ValueError("chunks and embeddings must have the same length")
        workspace_ids = {c.workspace_id for c in chunks}
        if len(workspace_ids) != 1:
            raise ValueError("All chunks in one call must belong to the same workspace")

        self._collection(workspace_ids.pop()).upsert(
            ids=[c.chunk_id for c in chunks],
            embeddings=embeddings,
            documents=[c.text for c in chunks],
            metadatas=[
                {
                    "candidate_id": c.candidate_id,
                    "workspace_id": c.workspace_id,
                    "section": c.section,
                }
                for c in chunks
            ],
        )

    def query(
        self,
        workspace_id: str,
        embedding: list[float],
        top_k: int,
        candidate_ids: list[str] | None = None,
    ) -> list[tuple[Chunk, float]]:
        """Nearest chunks in the workspace as (chunk, cosine_similarity), best first."""
        collection = self._collection(workspace_id)
        count = collection.count()
        if count == 0 or top_k <= 0:
            return []

        where = {"candidate_id": {"$in": candidate_ids}} if candidate_ids else None
        result = collection.query(
            query_embeddings=[embedding],
            n_results=min(top_k, count),
            where=where,
            include=["documents", "metadatas", "distances"],
        )
        return [
            (self._to_chunk(chunk_id, doc, meta), 1.0 - dist)
            for chunk_id, doc, meta, dist in zip(
                result["ids"][0],
                result["documents"][0],
                result["metadatas"][0],
                result["distances"][0],
                strict=True,
            )
        ]

    def get_candidate_chunks(self, workspace_id: str, candidate_id: str) -> list[Chunk]:
        result = self._collection(workspace_id).get(
            where={"candidate_id": candidate_id}, include=["documents", "metadatas"]
        )
        return [
            self._to_chunk(chunk_id, doc, meta)
            for chunk_id, doc, meta in zip(
                result["ids"], result["documents"], result["metadatas"], strict=True
            )
        ]

    def delete_candidate(self, workspace_id: str, candidate_id: str) -> None:
        self._collection(workspace_id).delete(where={"candidate_id": candidate_id})
        logger.info("Deleted vectors for candidate %s in workspace %s", candidate_id, workspace_id)

    def count(self, workspace_id: str) -> int:
        return self._collection(workspace_id).count()

    @staticmethod
    def _to_chunk(chunk_id: str, text: str, meta: dict) -> Chunk:
        return Chunk(
            chunk_id=chunk_id,
            candidate_id=meta["candidate_id"],
            workspace_id=meta["workspace_id"],
            section=meta["section"],
            text=text,
        )


@lru_cache
def get_vector_store() -> VectorStore:
    return VectorStore(get_settings().chroma_dir)
