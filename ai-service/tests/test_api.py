"""API wiring tests. MANUAL functions are replaced with fakes here — this checks the
routers call them correctly, not their logic."""

from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from app.api import candidates as candidates_api
from app.api import ingest as ingest_api
from app.api import search as search_api
from app.config import Settings, get_settings
from app.embeddings.embedder import Embedder, get_embedder
from app.llm.client import get_llm_client
from app.main import create_app
from app.models import CandidateProfile, CandidateResult, Chunk, CVSection, Evidence
from app.storage.vector_store import get_vector_store

PROFILE = CandidateProfile(
    full_name="Lina Faris",
    email="lina@example.com",
    phone=None,
    location="Amman",
    years_of_experience=4,
    skills=["Python"],
    languages=["Arabic", "English"],
    job_titles=["Backend Developer"],
    education=[],
)


class FakeBM25:
    instances: list["FakeBM25"] = []

    def __init__(self, workspace_id: str) -> None:
        self.workspace_id = workspace_id
        self.added: list[Chunk] = []
        self.deleted: list[str] = []
        FakeBM25.instances.append(self)

    def add(self, chunks: list[Chunk]) -> None:
        self.added.extend(chunks)

    def delete_candidate(self, candidate_id: str) -> None:
        self.deleted.append(candidate_id)


@pytest.fixture
def client(tmp_path, vector_store, fake_encoder, monkeypatch):
    FakeBM25.instances = []
    monkeypatch.setattr(ingest_api, "BM25Index", FakeBM25)
    monkeypatch.setattr(candidates_api, "BM25Index", FakeBM25)

    settings = Settings(
        _env_file=None,
        data_dir=tmp_path,
        chroma_dir=tmp_path / "chroma",
        bm25_dir=tmp_path / "bm25",
        max_upload_mb=1,
    )
    app = create_app()
    app.dependency_overrides[get_settings] = lambda: settings
    app.dependency_overrides[get_vector_store] = lambda: vector_store
    app.dependency_overrides[get_embedder] = lambda: Embedder("fake", model=fake_encoder)
    app.dependency_overrides[get_llm_client] = lambda: SimpleNamespace()
    with TestClient(app) as c:
        yield c


@pytest.fixture
def fake_manual_ingest(monkeypatch):
    monkeypatch.setattr(
        ingest_api, "detect_sections", lambda text: [CVSection(name="skills", text=text)]
    )
    monkeypatch.setattr(
        ingest_api,
        "chunk_sections",
        lambda sections, cid, wid: [
            Chunk(
                chunk_id=f"{cid}-{i}",
                candidate_id=cid,
                workspace_id=wid,
                section=s.name,
                text=s.text,
            )
            for i, s in enumerate(sections)
        ],
    )
    monkeypatch.setattr(ingest_api, "extract_profile", lambda text, llm: PROFILE)


def test_health(client):
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json()["status"] == "ok"


def test_ingest_indexes_chunks_and_returns_profile(
    client, make_docx, vector_store, fake_manual_ingest
):
    files = {"file": ("cv.docx", make_docx(["Skills", "Python"]), "application/octet-stream")}
    response = client.post(
        "/ingest", files=files, data={"candidate_id": "c1", "workspace_id": "ws1"}
    )

    assert response.status_code == 200
    assert response.json()["full_name"] == "Lina Faris"
    assert vector_store.count("ws1") == 1
    bm25 = FakeBM25.instances[-1]
    assert bm25.workspace_id == "ws1"
    assert bm25.deleted == ["c1"]  # re-ingest clears old chunks first
    assert [c.chunk_id for c in bm25.added] == ["c1-0"]


def test_ingest_unsupported_file(client):
    files = {"file": ("cv.txt", b"plain text", "text/plain")}
    response = client.post(
        "/ingest", files=files, data={"candidate_id": "c1", "workspace_id": "ws1"}
    )

    assert response.status_code == 415


def test_ingest_too_large(client):
    files = {"file": ("cv.pdf", b"%PDF" + b"0" * (1024 * 1024 + 1), "application/pdf")}
    response = client.post(
        "/ingest", files=files, data={"candidate_id": "c1", "workspace_id": "ws1"}
    )

    assert response.status_code == 413


def test_ingest_rejects_unsafe_workspace_id(client, make_docx):
    files = {"file": ("cv.docx", make_docx(["x"]), "application/octet-stream")}
    response = client.post(
        "/ingest", files=files, data={"candidate_id": "c1", "workspace_id": "../other"}
    )

    assert response.status_code == 422


def test_search_passes_request_to_pipeline(client, monkeypatch):
    calls = []

    def fake_search(**kwargs):
        calls.append(kwargs)
        return [
            CandidateResult(
                candidate_id="c1",
                score=0.9,
                reason="Python",
                evidence=[Evidence(section="skills", quote="Python")],
            )
        ]

    monkeypatch.setattr(search_api.pipeline, "search", fake_search)
    response = client.post(
        "/search", json={"workspace_id": "ws1", "query": "python dev", "top_k": 5}
    )

    assert response.status_code == 200
    assert response.json()[0]["candidate_id"] == "c1"
    assert calls == [
        {"workspace_id": "ws1", "query": "python dev", "top_k": 5, "candidate_ids": None}
    ]


def test_pending_manual_stub_returns_501(client, monkeypatch):
    def not_done(**kwargs):
        raise NotImplementedError("MANUAL: end-to-end search pipeline")

    monkeypatch.setattr(search_api.pipeline, "search", not_done)
    response = client.post("/search", json={"workspace_id": "ws1", "query": "x"})

    assert response.status_code == 501
    assert response.json()["detail"].startswith("MANUAL")


def test_search_validates_body(client):
    assert client.post("/search", json={"workspace_id": "ws1", "query": ""}).status_code == 422
    assert (
        client.post("/search", json={"workspace_id": "ws1", "query": "x", "top_k": 0}).status_code
        == 422
    )


def test_explain(client, monkeypatch):
    result = CandidateResult(candidate_id="c1", score=0.8, reason="r", evidence=[])
    monkeypatch.setattr(search_api, "explain_candidate", lambda **kwargs: result)

    response = client.post(
        "/explain", json={"candidate_id": "c1", "workspace_id": "ws1", "query": "x"}
    )

    assert response.status_code == 200
    assert response.json()["candidate_id"] == "c1"


def test_delete_candidate(client, vector_store):
    vector_store.add_chunks(
        [Chunk(chunk_id="a", candidate_id="c1", workspace_id="ws1", section="skills", text="x")],
        [[1, 0, 0, 0]],
    )

    response = client.delete("/candidates/c1", params={"workspace_id": "ws1"})

    assert response.status_code == 204
    assert vector_store.count("ws1") == 0
    assert FakeBM25.instances[-1].deleted == ["c1"]
