"""🔒 MANUAL — cross-encoder reranking (BAAI/bge-reranker-v2-m3).

🧸 EXPLAIN LIKE I'M 5
---------------------
The first searches (dense + BM25) are FAST but a bit sloppy — like picking books off a
shelf by looking at the covers. They give us ~30 "maybe good" pieces.

The reranker is SLOW but CAREFUL — like actually reading each of those 30 books
with the question in mind. It reads the question and the CV piece TOGETHER, at the
same time ("cross"-encoder), so it notices things like:
    question: "led a team of 5+ engineers"
    piece:    "Managed 7 backend developers"      → high score, even with no shared words
    piece:    "Worked in a team of engineers"     → lower score

Why not use the careful reader for everything? It's too slow to read 10,000 pieces for
every search. So: fast search picks 30, careful reader sorts those 30. ✨

bge-reranker-v2-m3 is multilingual, so it reads Arabic and English (even mixed).

🧸 Making the scores human-friendly ("calibration")
The model's own 0..1 scores are very shy: a PERFECT match for a short query often gets
only 0.04, which would show up as "4% match" on screen. Underneath, the model really
produces a "logit" (any number, higher = better). We measured it on sample CVs:
    good matches     ≈ -3 … +1
    unrelated pieces ≈ -6 … -10
So we slide the scale right by SHIFT and make it gentler with TEMPERATURE:
    score = 1 / (1 + e^(-(logit + SHIFT) / TEMPERATURE))
→ good matches land around 75–98%, unrelated ones below 40%. The ORDER never changes;
only the numbers people read become meaningful.
"""

import logging
import math
import threading
from functools import lru_cache
from typing import Any, Protocol

from app.config import get_settings
from app.models import Chunk

logger = logging.getLogger(__name__)


SCORE_SHIFT = 5.0
SCORE_TEMPERATURE = 1.5


def calibrate(probability: float) -> float:
    """Model probability (sigmoid of the logit) → friendlier 0..1 score, same order."""
    p = min(max(probability, 1e-7), 1 - 1e-7)
    logit = math.log(p / (1 - p))  # 🧸 undo the model's own sigmoid
    return 1.0 / (1.0 + math.exp(-(logit + SCORE_SHIFT) / SCORE_TEMPERATURE))


class _CrossEncoderModel(Protocol):
    def predict(self, sentences: list[tuple[str, str]], **kwargs: Any) -> Any: ...


class Reranker:
    """Cross-encoder that rescores (query, chunk) pairs."""

    def __init__(self, model_name: str, model: _CrossEncoderModel | None = None) -> None:
        """Load the cross-encoder model once.

        🧸 The model is big (~2 GB), so we don't load it until the first search needs it
        ("lazy loading"). Tests can pass a tiny fake `model` instead.
        """
        self.model_name = model_name
        self._model = model
        self._lock = threading.Lock()

    @property
    def is_loaded(self) -> bool:
        return self._model is not None

    @property
    def model(self) -> _CrossEncoderModel:
        if self._model is None:
            with self._lock:  # 🧸 two searches at once must not load it twice
                if self._model is None:
                    from sentence_transformers import CrossEncoder

                    logger.info("Loading reranker model %s", self.model_name)
                    self._model = CrossEncoder(self.model_name, max_length=512)
        return self._model

    def rerank(self, query: str, chunks: list[Chunk], top_k: int) -> list[tuple[Chunk, float]]:
        """Return the `top_k` chunks with reranker scores, best first."""
        if not chunks or top_k <= 0:
            return []
        # 🧸 Make pairs: (question, piece) for every piece, and let the careful reader
        # give each pair a score.
        pairs = [(query, chunk.text) for chunk in chunks]
        raw = self.model.predict(pairs, batch_size=16, show_progress_bar=False)
        scores = [calibrate(float(s)) for s in raw]
        ranked = sorted(zip(chunks, scores, strict=True), key=lambda p: p[1], reverse=True)
        return ranked[:top_k]


@lru_cache
def get_reranker() -> Reranker:
    return Reranker(get_settings().reranker_model)
