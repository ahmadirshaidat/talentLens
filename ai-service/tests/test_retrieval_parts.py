"""BM25, RRF, aggregation and reranker — the building blocks of the search pipeline."""

import math

import pytest

from app.models import Chunk
from app.retrieval.aggregate import aggregate_candidates
from app.retrieval.bm25_index import BM25Index, tokenize
from app.retrieval.fusion import reciprocal_rank_fusion
from app.retrieval.reranker import Reranker, calibrate


def _chunk(chunk_id: str, candidate_id: str, text: str, ws: str = "ws1") -> Chunk:
    return Chunk(
        chunk_id=chunk_id, candidate_id=candidate_id, workspace_id=ws, section="skills", text=text
    )


# ---------------------------------------------------------------- tokenize


def test_tokenize_normalizes_english_and_arabic():
    assert tokenize("Senior PYTHON developer, the C# guru") == [
        "senior", "python", "developer", "c#", "guru",
    ]
    # diacritics removed, أ→ا, ة→ه, and the "ال" prefix stripped from long words
    assert tokenize("المُبيعات والخبرة") == ["مبيعات", "والخبره"]
    assert tokenize("مبيعات") == ["مبيعات"]


# ---------------------------------------------------------------- BM25


@pytest.fixture
def bm25(tmp_path) -> BM25Index:
    index = BM25Index("ws1", directory=tmp_path)
    index.add(
        [
            _chunk("a-0", "a", "Python developer building Django APIs"),
            _chunk("b-0", "b", "Java developer with Spring"),
            _chunk("c-0", "c", "Sales manager, team leader"),
            _chunk("d-0", "d", "مطور بايثون في شركة ناشئة"),
        ]
    )
    return index


def test_bm25_ranks_exact_keyword_first(bm25):
    results = bm25.search("python django", top_k=3)

    assert results[0][0] == "a-0"
    assert all(score > 0 for _, score in results)
    assert "c-0" not in [cid for cid, _ in results]


def test_bm25_arabic_query(bm25):
    assert bm25.search("بايثون", top_k=3)[0][0] == "d-0"


def test_bm25_rare_words_score_higher(bm25):
    # "developer" is in 2 docs, "spring" in 1 → the Spring doc wins for "developer spring"
    assert bm25.search("developer spring", top_k=2)[0][0] == "b-0"


def test_bm25_persists_and_deletes(bm25, tmp_path):
    reloaded = BM25Index("ws1", directory=tmp_path)
    assert len(reloaded) == 4

    reloaded.delete_candidate("a")

    fresh = BM25Index("ws1", directory=tmp_path)
    assert len(fresh) == 3
    assert "a-0" not in [cid for cid, _ in fresh.search("python django", 5)]


def test_bm25_candidate_filter(bm25):
    results = bm25.search("developer", top_k=5, candidate_ids=["b"])
    assert [cid for cid, _ in results] == ["b-0"]


def test_bm25_workspaces_are_isolated(bm25, tmp_path):
    other = BM25Index("ws2", directory=tmp_path)
    assert other.search("python", 5) == []
    with pytest.raises(ValueError):
        other.add([_chunk("x", "x", "python", ws="ws1")])


def test_bm25_rejects_unsafe_workspace_id(tmp_path):
    with pytest.raises(ValueError):
        BM25Index("../evil", directory=tmp_path)


def test_bm25_empty_query_or_index(tmp_path):
    index = BM25Index("empty", directory=tmp_path)
    assert index.search("python", 5) == []
    assert index.search("", 5) == []


# ---------------------------------------------------------------- RRF


def test_rrf_prefers_items_in_both_lists():
    fused = reciprocal_rank_fusion([["car", "ball", "doll"], ["ball", "kite", "car"]], k=60)

    assert [item for item, _ in fused] == ["ball", "car", "kite", "doll"]
    assert fused[0][1] == pytest.approx(1 / 62 + 1 / 61)


def test_rrf_ignores_duplicates_within_a_list():
    fused = dict(reciprocal_rank_fusion([["a", "a", "b"]], k=60))
    assert fused["a"] == pytest.approx(1 / 61)
    assert fused["b"] == pytest.approx(1 / 63)


def test_rrf_empty():
    assert reciprocal_rank_fusion([]) == []
    assert reciprocal_rank_fusion([[], []]) == []


# ---------------------------------------------------------------- aggregate


def test_aggregate_groups_by_candidate_and_sorts():
    scored = [
        (_chunk("a-0", "a", "x"), 0.9),
        (_chunk("b-0", "b", "x"), 0.95),
        (_chunk("a-1", "a", "x"), 0.8),
        (_chunk("a-2", "a", "x"), 0.7),
    ]

    results = aggregate_candidates(scored)

    by_id = {cid: (score, [c.chunk_id for c in chunks]) for cid, score, chunks in results}
    assert by_id["a"][1] == ["a-0", "a-1", "a-2"]
    assert by_id["a"][0] == pytest.approx(0.8 * 0.9 + 0.2 * (0.9 + 0.8 + 0.7) / 3)
    assert by_id["b"][0] == pytest.approx(0.95)
    assert [cid for cid, _, _ in results] == ["b", "a"]


def test_aggregate_dedupes_chunks():
    chunk = _chunk("a-0", "a", "x")
    [(cid, score, chunks)] = aggregate_candidates([(chunk, 0.5), (chunk, 0.4)])
    assert (cid, len(chunks)) == ("a", 1)
    assert score == pytest.approx(0.5)


def test_aggregate_empty():
    assert aggregate_candidates([]) == []


# ---------------------------------------------------------------- reranker


class FakeCrossEncoder:
    def predict(self, pairs, **kwargs):
        return [0.9 if "python" in text.lower() else 0.1 for _, text in pairs]


def test_reranker_sorts_by_model_score():
    reranker = Reranker("fake", model=FakeCrossEncoder())
    chunks = [_chunk("a", "a", "Sales"), _chunk("b", "b", "Python dev"), _chunk("c", "c", "x")]

    ranked = reranker.rerank("python", chunks, top_k=2)

    assert [c.chunk_id for c, _ in ranked] == ["b", "a"]
    assert ranked[0][1] == pytest.approx(calibrate(0.9))
    assert reranker.rerank("python", [], top_k=2) == []


def test_calibration_keeps_order_and_spreads_scores():
    sigmoid = lambda x: 1 / (1 + math.exp(-x))  # noqa: E731
    good, okay, bad = calibrate(sigmoid(0.8)), calibrate(sigmoid(-3.2)), calibrate(sigmoid(-10))
    assert 1 > good > okay > bad > 0
    assert okay > 0.7  # a real match no longer shows up as "4%"
    assert bad < 0.05
    assert 0 < calibrate(0.0) < calibrate(1.0) < 1
