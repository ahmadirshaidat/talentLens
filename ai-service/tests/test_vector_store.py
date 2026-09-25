import pytest

from app.models import Chunk


def _chunk(chunk_id: str, candidate_id: str, workspace_id: str, text: str) -> Chunk:
    return Chunk(
        chunk_id=chunk_id,
        candidate_id=candidate_id,
        workspace_id=workspace_id,
        section="skills",
        text=text,
    )


def test_add_and_query_returns_best_match_first(vector_store):
    chunks = [_chunk("c1-0", "c1", "ws1", "Python"), _chunk("c2-0", "c2", "ws1", "Sales")]
    vector_store.add_chunks(chunks, [[1, 0, 0, 0], [0, 0, 1, 0]])

    results = vector_store.query("ws1", [1, 0, 0, 0], top_k=2)

    assert [c.chunk_id for c, _ in results] == ["c1-0", "c2-0"]
    assert results[0][1] == pytest.approx(1.0, abs=1e-5)
    assert results[0][0] == chunks[0]


def test_workspaces_are_isolated(vector_store):
    vector_store.add_chunks([_chunk("a", "c1", "ws1", "Python")], [[1, 0, 0, 0]])
    vector_store.add_chunks([_chunk("b", "c2", "ws2", "Python")], [[1, 0, 0, 0]])

    results = vector_store.query("ws1", [1, 0, 0, 0], top_k=10)

    assert [c.chunk_id for c, _ in results] == ["a"]


def test_query_filters_by_candidate_ids(vector_store):
    vector_store.add_chunks(
        [_chunk("a", "c1", "ws1", "x"), _chunk("b", "c2", "ws1", "y")],
        [[1, 0, 0, 0], [1, 0, 0, 0]],
    )

    results = vector_store.query("ws1", [1, 0, 0, 0], top_k=10, candidate_ids=["c2"])

    assert [c.candidate_id for c, _ in results] == ["c2"]


def test_query_empty_workspace(vector_store):
    assert vector_store.query("empty", [1, 0, 0, 0], top_k=5) == []


def test_delete_candidate_and_get_chunks(vector_store):
    vector_store.add_chunks(
        [
            _chunk("a", "c1", "ws1", "x"),
            _chunk("b", "c1", "ws1", "y"),
            _chunk("c", "c2", "ws1", "z"),
        ],
        [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0]],
    )
    assert {c.chunk_id for c in vector_store.get_candidate_chunks("ws1", "c1")} == {"a", "b"}

    vector_store.delete_candidate("ws1", "c1")

    assert vector_store.get_candidate_chunks("ws1", "c1") == []
    assert vector_store.count("ws1") == 1


def test_add_rejects_mixed_workspaces(vector_store):
    with pytest.raises(ValueError):
        vector_store.add_chunks(
            [_chunk("a", "c1", "ws1", "x"), _chunk("b", "c2", "ws2", "y")],
            [[1, 0, 0, 0], [1, 0, 0, 0]],
        )


@pytest.mark.parametrize("bad_id", ["../etc", "", "a b", "x" * 65])
def test_invalid_workspace_id(vector_store, bad_id):
    with pytest.raises(ValueError):
        vector_store.count(bad_id)
