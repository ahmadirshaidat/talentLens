"""🔒 MANUAL — Reciprocal Rank Fusion.

🧸 EXPLAIN LIKE I'M 5
---------------------
Two friends each make a "best toys" list:
  - Friend A (dense search, understands meaning):  [car, ball, doll]
  - Friend B (BM25, matches exact words):           [ball, kite, car]

How do we make ONE list? Their scores are in different "languages" (A gives 0.83,
B gives 12.4), so we can't just add the scores. Instead we only look at the PLACE
each toy got in each list:

    points = 1 / (k + place)

1st place → 1/61, 2nd → 1/62, 3rd → 1/63 ... (with k = 60)

  ball: 1/(60+2) from A + 1/(60+1) from B  = 0.0325  ← both friends liked it → wins!
  car:  1/(60+1) from A + 1/(60+3) from B  = 0.0323
  kite: only B                              = 0.0161
  doll: only A                              = 0.0159

Being liked by BOTH friends beats being loved by just one. `k` (60) stops 1st place
from being worth way more than 2nd — it makes the fusion calm and fair. 60 is the
value from the original paper and works well almost everywhere.
"""


def reciprocal_rank_fusion(rankings: list[list[str]], k: int = 60) -> list[tuple[str, float]]:
    """Fuse several ranked lists of ids into one.

    Args:
        rankings: Each list is ids ordered best-first (e.g. dense and BM25 results).
        k: RRF smoothing constant.

    Returns:
        (id, fused_score) pairs sorted by score descending.
    """
    scores: dict[str, float] = {}
    for ranking in rankings:
        seen: set[str] = set()
        for place, item_id in enumerate(ranking, start=1):
            if item_id in seen:  # 🧸 a friend can't vote twice for the same toy
                continue
            seen.add(item_id)
            scores[item_id] = scores.get(item_id, 0.0) + 1.0 / (k + place)

    # 🧸 Sort biggest first. If two ids tie, keep them in a stable, predictable order.
    return sorted(scores.items(), key=lambda pair: (-pair[1], pair[0]))
