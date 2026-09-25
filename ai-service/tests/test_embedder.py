import pytest

from app.embeddings.embedder import Embedder


def test_embed_documents_normalizes_and_returns_lists(fake_encoder):
    embedder = Embedder("fake-model", model=fake_encoder)

    vectors = embedder.embed_documents(["Python developer", "مطور عربي"])

    assert len(vectors) == 2
    assert all(isinstance(x, float) for x in vectors[0])
    assert fake_encoder.calls[0]["normalize_embeddings"] is True


def test_embed_query_matches_document_encoding(fake_encoder):
    embedder = Embedder("fake-model", model=fake_encoder)

    assert embedder.embed_query("Python") == embedder.embed_documents(["Python"])[0]


def test_embed_empty_list_skips_model(fake_encoder):
    embedder = Embedder("fake-model", model=fake_encoder)

    assert embedder.embed_documents([]) == []
    assert fake_encoder.calls == []


def test_model_is_loaded_once(monkeypatch, fake_encoder):
    import sys
    import types

    loads: list[str] = []

    def fake_sentence_transformer(name):
        loads.append(name)
        return fake_encoder

    monkeypatch.setitem(
        sys.modules,
        "sentence_transformers",
        types.SimpleNamespace(SentenceTransformer=fake_sentence_transformer),
    )
    embedder = Embedder("BAAI/bge-m3")
    assert not embedder.is_loaded

    embedder.embed_query("a")
    embedder.embed_query("b")

    assert loads == ["BAAI/bge-m3"]
    assert embedder.is_loaded


@pytest.fixture(autouse=True)
def _clear_cache():
    from app.embeddings.embedder import get_embedder

    get_embedder.cache_clear()
    yield
    get_embedder.cache_clear()
