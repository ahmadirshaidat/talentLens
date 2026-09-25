"""Thin wrapper around BAAI/bge-m3 (sentence-transformers), loaded once and cached."""

import logging
import threading
from functools import lru_cache
from typing import Any, Protocol

from app.config import get_settings

logger = logging.getLogger(__name__)


class _EncoderModel(Protocol):
    def encode(self, sentences: list[str], **kwargs: Any) -> Any: ...


class Embedder:
    """Dense embeddings for chunks and queries.

    bge-m3 is multilingual (Arabic + English) and needs no query instruction prefix,
    so documents and queries are encoded the same way. Vectors are L2-normalized,
    so cosine similarity == dot product.
    """

    def __init__(self, model_name: str, model: _EncoderModel | None = None) -> None:
        self.model_name = model_name
        self._model = model
        self._lock = threading.Lock()

    @property
    def is_loaded(self) -> bool:
        return self._model is not None

    @property
    def model(self) -> _EncoderModel:
        if self._model is None:
            with self._lock:
                if self._model is None:
                    # Imported lazily: pulling in torch is slow and not needed for tests.
                    from sentence_transformers import SentenceTransformer

                    logger.info("Loading embedding model %s", self.model_name)
                    self._model = SentenceTransformer(self.model_name)
        return self._model

    def embed_documents(self, texts: list[str], batch_size: int = 16) -> list[list[float]]:
        if not texts:
            return []
        vectors = self.model.encode(
            texts,
            batch_size=batch_size,
            normalize_embeddings=True,
            convert_to_numpy=True,
            show_progress_bar=False,
        )
        return [[float(x) for x in v] for v in vectors]

    def embed_query(self, text: str) -> list[float]:
        return self.embed_documents([text])[0]


@lru_cache
def get_embedder() -> Embedder:
    return Embedder(get_settings().embedding_model)
