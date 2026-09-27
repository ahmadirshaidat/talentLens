"""🔒 MANUAL — retrieval metrics.

🧸 EXPLAIN LIKE I'M 5
---------------------
How do we know if search is GOOD? We make a little exam: questions where WE already know
the right answers ("for 'Python backend', the right people are Lina and Omar").
Then we grade the search engine's answers.

Precision@k — "Of the first k people you showed me, how many were right?"
    Showed 5 people, 3 were right → 3/5 = 0.6
    Simple, but it doesn't care about ORDER: right answers at #1 or #5 count the same.

nDCG@k — "Did you put the BEST answers at the TOP?"
    - Each right answer earns points = how relevant it is (e.g. 2 = perfect, 1 = okay).
    - Points shrink the lower they appear: #1 counts fully, #2 a bit less, #3 even less…
      (divide by log2(position + 1)). That's "DCG" (Discounted Cumulative Gain).
    - Then we compare with the PERFECT order (best answers first) — "IDCG" — so the
      final number is between 0 (terrible) and 1 (perfect). That's the "n" (normalized).
"""

import math


def precision_at_k(retrieved: list[str], relevant: set[str], k: int) -> float:
    """Fraction of the top-k retrieved ids that are relevant."""
    if k <= 0:
        return 0.0
    top = retrieved[:k]
    hits = sum(1 for item in top if item in relevant)
    # 🧸 We divide by k (not by len(top)): showing fewer than k results is not a free pass.
    return hits / k


def recall_at_k(retrieved: list[str], relevant: set[str], k: int) -> float:
    """Fraction of all relevant ids that appear in the top-k. ("Did you find them all?")"""
    if not relevant or k <= 0:
        return 0.0
    return len(set(retrieved[:k]) & relevant) / len(relevant)


def dcg(gains: list[float]) -> float:
    # 🧸 position 1 → divide by log2(2)=1, position 2 → log2(3)≈1.58, position 3 → 2 …
    return sum(g / math.log2(i + 2) for i, g in enumerate(gains))


def ndcg_at_k(retrieved: list[str], relevance: dict[str, float], k: int) -> float:
    """Normalized DCG of the top-k retrieved ids given graded relevance."""
    if k <= 0:
        return 0.0
    actual = dcg([relevance.get(item, 0.0) for item in retrieved[:k]])
    ideal = dcg(sorted(relevance.values(), reverse=True)[:k])
    return actual / ideal if ideal > 0 else 0.0


def reciprocal_rank(retrieved: list[str], relevant: set[str]) -> float:
    """1 / position of the first right answer (1st → 1.0, 2nd → 0.5, none → 0)."""
    for i, item in enumerate(retrieved, start=1):
        if item in relevant:
            return 1.0 / i
    return 0.0
