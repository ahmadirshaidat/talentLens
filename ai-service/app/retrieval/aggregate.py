"""🔒 MANUAL — aggregate chunk scores into candidate scores.

🧸 EXPLAIN LIKE I'M 5
---------------------
Search finds CV *pieces* (chunks), but the recruiter wants *people*.
One person can have many pieces that match: their Skills piece, their Experience piece…

So we put every piece in its owner's bucket, then give each bucket ONE score:

    score = 0.8 × (best piece)  +  0.2 × (average of the top 3 pieces)

- The BEST piece matters most: one strong, clear match ("5 years of Python at X") is
  what a recruiter really cares about.
- The top-3 average is a small bonus for people who match in SEVERAL places
  (skills AND experience AND projects) — that's more convincing than one lucky line.
- We only use the top 3, so a very long CV doesn't win just by having many pieces.

The best pieces are also kept, because they become the "evidence" shown on screen.
"""

from app.models import Chunk

BEST_WEIGHT = 0.8
TOP_N_FOR_AVERAGE = 3


def aggregate_candidates(
    scored_chunks: list[tuple[Chunk, float]],
) -> list[tuple[str, float, list[Chunk]]]:
    """Group scored chunks by candidate and compute one score per candidate.

    Args:
        scored_chunks: (chunk, score) pairs from retrieval/reranking.

    Returns:
        (candidate_id, candidate_score, supporting_chunks) sorted by score descending.
        supporting_chunks are that candidate's chunks, best first.
    """
    # 🧸 Step 1: one bucket per person.
    buckets: dict[str, list[tuple[Chunk, float]]] = {}
    for chunk, score in scored_chunks:
        buckets.setdefault(chunk.candidate_id, []).append((chunk, score))

    results: list[tuple[str, float, list[Chunk]]] = []
    for candidate_id, items in buckets.items():
        # 🧸 Step 2: sort the person's pieces, best first. If the same piece came
        # twice, keep it once.
        items.sort(key=lambda pair: pair[1], reverse=True)
        unique: list[tuple[Chunk, float]] = []
        seen: set[str] = set()
        for chunk, score in items:
            if chunk.chunk_id not in seen:
                seen.add(chunk.chunk_id)
                unique.append((chunk, score))

        # 🧸 Step 3: mix "best piece" with "average of top pieces".
        best = unique[0][1]
        top = [score for _, score in unique[:TOP_N_FOR_AVERAGE]]
        score = BEST_WEIGHT * best + (1 - BEST_WEIGHT) * (sum(top) / len(top))
        results.append((candidate_id, score, [chunk for chunk, _ in unique]))

    # 🧸 Step 4: highest score first.
    results.sort(key=lambda r: (-r[1], r[0]))
    return results
