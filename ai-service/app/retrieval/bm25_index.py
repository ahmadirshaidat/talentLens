"""🔒 MANUAL — BM25 index, one per workspace.

🧸 EXPLAIN LIKE I'M 5
---------------------
Imagine a big box of toy cards. Each card is one piece of a CV (a "chunk").
When the recruiter asks "python developer", we want the cards that SAY those words.

BM25 is a way of giving every card points:
  1. If the card has the word "python" → points! More "python"s → a few more points,
     but not forever (saying "python" 50 times shouldn't win — that's the `k1` knob).
  2. Rare words are worth MORE than common words. If every card says "team", finding
     "team" means little. If only 2 cards say "kubernetes", that's a big clue.
     That's the "IDF" (inverse document frequency) part.
  3. Long cards naturally contain more words, so we shrink their points a bit so they
     don't win just by being long. That's the `b` knob.

The dense (embedding) search understands MEANING ("coder" ≈ "developer"), but it can
miss exact things like "C#" or a company name. BM25 catches exact words. Using both
together (see fusion.py) is called "hybrid search".

Every workspace (company) gets its OWN box of cards saved in its own file, so one
company can never find another company's CVs.
"""

import json
import logging
import math
import re
import threading
from collections import Counter
from pathlib import Path

from app.config import get_settings
from app.models import Chunk
from app.vocabulary import normalize

logger = logging.getLogger(__name__)

_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,64}$")

# 🧸 A "token" is one word. This pattern finds words made of English letters/digits or
# Arabic letters. It keeps "c#" and "c++" together because recruiters search for them.
_TOKEN = re.compile(r"[a-z0-9]+[#+]*|[ء-ي]+")

# 🧸 Tiny words that appear everywhere and tell us nothing ("the", "في" = "in").
_STOPWORDS = {
    "a", "an", "and", "the", "of", "in", "on", "at", "to", "for", "with", "by", "or",
    "is", "are", "was", "be", "as", "from", "that", "this", "it", "i", "my", "we",
    "في", "من", "علي", "الي", "عن", "مع", "و", "او", "ان", "هذا", "هذه", "التي", "الذي",
    "كان", "لدي", "ذات", "ثم",
}

# 🧸 One lock per workspace, so two uploads at the same moment don't scribble over
# each other's file.
_locks: dict[str, threading.Lock] = {}
_locks_guard = threading.Lock()


def _workspace_lock(workspace_id: str) -> threading.Lock:
    with _locks_guard:
        return _locks.setdefault(workspace_id, threading.Lock())


def tokenize(text: str) -> list[str]:
    """Turn text into a list of comparable words.

    🧸 "Senior PYTHON developer, الخبرة" → ["senior", "python", "developer", "خبره"]
    - lowercase + fix Arabic spelling variants (أ/إ/آ → ا, ة → ه, ى → ي)
    - drop tiny useless words
    - drop the Arabic "ال" (= "the") at the start of long words, so "المبيعات" and
      "مبيعات" count as the same word. This is a very small "stemmer".
    """
    tokens: list[str] = []
    for tok in _TOKEN.findall(normalize(text)):
        if len(tok) > 4 and tok.startswith("ال"):
            tok = tok[2:]
        if tok not in _STOPWORDS:
            tokens.append(tok)
    return tokens


class BM25Index:
    """Keyword index over the chunks of a single workspace."""

    # 🧸 The two knobs explained at the top. These values are the textbook defaults.
    k1: float = 1.5
    b: float = 0.75

    def __init__(self, workspace_id: str, directory: Path | None = None) -> None:
        """Create or load the BM25 index for `workspace_id`.

        🧸 We open this workspace's box of cards from disk (or start an empty box).
        On disk we only store each card's words; the scores are computed at search time,
        so adding/removing a card never needs a big "rebuild".
        """
        if not _ID_PATTERN.fullmatch(workspace_id):
            raise ValueError(f"Invalid workspace_id: {workspace_id!r}")
        self.workspace_id = workspace_id
        directory = directory or get_settings().bm25_dir
        directory.mkdir(parents=True, exist_ok=True)
        self._path = directory / f"{workspace_id}.json"
        self._lock = _workspace_lock(workspace_id)
        # chunk_id -> {"candidate_id": str, "tokens": list[str]}
        self._docs: dict[str, dict] = self._load()

    # ---------- storage ----------

    def _load(self) -> dict[str, dict]:
        if not self._path.exists():
            return {}
        return json.loads(self._path.read_text(encoding="utf-8"))["docs"]

    def _save(self) -> None:
        # 🧸 Write to a temporary file first, then swap it in. If the computer crashes
        # halfway, the old file is still fine instead of half-written.
        tmp = self._path.with_suffix(".tmp")
        tmp.write_text(json.dumps({"docs": self._docs}, ensure_ascii=False), encoding="utf-8")
        tmp.replace(self._path)

    def _reload(self) -> None:
        """Pick up changes another request may have saved since we were created."""
        self._docs = self._load()

    # ---------- writing ----------

    def add(self, chunks: list[Chunk]) -> None:
        """Add chunks to the index and persist it."""
        if not chunks:
            return
        if any(c.workspace_id != self.workspace_id for c in chunks):
            raise ValueError("Chunk belongs to a different workspace")  # 🧸 never mix boxes!
        with self._lock:
            self._reload()
            for chunk in chunks:
                self._docs[chunk.chunk_id] = {
                    "candidate_id": chunk.candidate_id,
                    "tokens": tokenize(chunk.text),
                }
            self._save()

    def delete_candidate(self, candidate_id: str) -> None:
        """Remove all chunks belonging to `candidate_id` and persist."""
        with self._lock:
            self._reload()
            before = len(self._docs)
            self._docs = {
                cid: doc for cid, doc in self._docs.items() if doc["candidate_id"] != candidate_id
            }
            if len(self._docs) != before:
                self._save()
                logger.info(
                    "Deleted %d BM25 docs for candidate %s in workspace %s",
                    before - len(self._docs), candidate_id, self.workspace_id,
                )

    def __len__(self) -> int:
        return len(self._docs)

    # ---------- searching ----------

    def search(
        self, query: str, top_k: int, candidate_ids: list[str] | None = None
    ) -> list[tuple[str, float]]:
        """Return up to `top_k` (chunk_id, bm25_score) pairs, best first.

        Args:
            query: Recruiter query (Arabic or English).
            top_k: How many chunks to return.
            candidate_ids: If given, only these candidates' chunks can be returned.
        """
        query_tokens = tokenize(query)
        if not query_tokens or not self._docs or top_k <= 0:
            return []

        # 🧸 Step 1: count, for each word, how many cards contain it ("document frequency").
        n_docs = len(self._docs)
        doc_freq: Counter[str] = Counter()
        total_len = 0
        for doc in self._docs.values():
            doc_freq.update(set(doc["tokens"]))
            total_len += len(doc["tokens"])
        avg_len = total_len / n_docs or 1.0

        # 🧸 Step 2: how valuable is each query word? Rare → big number, common → small.
        idf = {
            t: math.log((n_docs - doc_freq[t] + 0.5) / (doc_freq[t] + 0.5) + 1.0)
            for t in set(query_tokens)
        }

        allowed = set(candidate_ids) if candidate_ids else None
        scores: list[tuple[str, float]] = []
        for chunk_id, doc in self._docs.items():
            if allowed is not None and doc["candidate_id"] not in allowed:
                continue
            tf = Counter(doc["tokens"])
            length_norm = self.k1 * (1 - self.b + self.b * len(doc["tokens"]) / avg_len)
            score = 0.0
            # 🧸 Step 3: for each query word on this card, add its points.
            # tf * (k1 + 1) / (tf + length_norm) grows fast at first then flattens out,
            # so the 10th "python" adds much less than the 1st one.
            for t in query_tokens:
                if tf[t]:
                    score += idf[t] * tf[t] * (self.k1 + 1) / (tf[t] + length_norm)
            if score > 0:
                scores.append((chunk_id, score))

        # 🧸 Step 4: biggest score first, keep the best few.
        scores.sort(key=lambda pair: pair[1], reverse=True)
        return scores[:top_k]
