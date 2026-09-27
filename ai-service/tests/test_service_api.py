"""Service auth, request ids, safe errors, /health, and the matching endpoints' wiring."""

from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from app.api import matching as matching_api
from app.api import search as search_api
from app.config import Settings, get_settings
from app.llm.client import get_llm_client
from app.main import create_app
from app.matching.requirement_models import CandidateJobMatch, JobRequirement, JobRequirements

KEY = "test-service-key"


def _client(tmp_path, **overrides) -> TestClient:
    settings = Settings(
        _env_file=None,
        data_dir=tmp_path,
        chroma_dir=tmp_path / "chroma",
        bm25_dir=tmp_path / "bm25",
        profiles_dir=tmp_path / "profiles",
        **overrides,
    )
    app = create_app()
    app.dependency_overrides[get_settings] = lambda: settings
    app.dependency_overrides[get_llm_client] = lambda: SimpleNamespace(model="")
    return TestClient(app, raise_server_exceptions=False)


@pytest.fixture
def secured(tmp_path):
    with _client(tmp_path, service_api_key=KEY) as c:
        yield c


@pytest.fixture
def open_client(tmp_path):
    with _client(tmp_path) as c:
        yield c


# ---------------------------------------------------------------- auth


def test_protected_routes_need_the_service_key(secured):
    body = {"workspace_id": "ws1", "query": "python"}
    assert secured.post("/search", json=body).status_code == 401
    assert secured.post("/search", json=body, headers={"X-Api-Key": "wrong"}).status_code == 401
    assert secured.delete("/candidates/c1", params={"workspace_id": "ws1"}).status_code == 401
    match = {"workspace_id": "ws1", "job_text": "x"}
    assert secured.post("/match/job", json=match).status_code == 401


def test_right_key_passes(secured, monkeypatch):
    monkeypatch.setattr(search_api.pipeline, "search", lambda **kwargs: [])
    response = secured.post(
        "/search", json={"workspace_id": "ws1", "query": "python"}, headers={"X-Api-Key": KEY}
    )
    assert response.status_code == 200


def test_health_is_public_and_reports_status(secured):
    body = secured.get("/health").json()
    assert body["status"] == "ok"
    assert body["auth_required"] is True
    assert body["llm_configured"] is False
    assert "embedding_model_loaded" in body


def test_production_refuses_to_start_without_key(tmp_path):
    with pytest.raises(RuntimeError, match="SERVICE_API_KEY"):
        # Lifespan reads settings via get_settings(); patch the cache for this test.
        get_settings.cache_clear()
        import os

        os.environ["ENVIRONMENT"] = "production"
        os.environ["SERVICE_API_KEY"] = ""
        try:
            with TestClient(create_app()):
                pass
        finally:
            del os.environ["ENVIRONMENT"]
            del os.environ["SERVICE_API_KEY"]
            get_settings.cache_clear()


# ---------------------------------------------------------------- request context + errors


def test_request_id_is_echoed_or_generated(open_client):
    echoed = open_client.get("/health", headers={"X-Request-ID": "abc-123"})
    assert echoed.headers["X-Request-ID"] == "abc-123"

    generated = open_client.get("/health", headers={"X-Request-ID": "bad id with spaces!"})
    assert generated.headers["X-Request-ID"] != "bad id with spaces!"
    assert len(generated.headers["X-Request-ID"]) == 32


def test_unexpected_errors_return_a_safe_message(open_client, monkeypatch):
    def boom(**kwargs):
        raise RuntimeError("secret internal detail")

    monkeypatch.setattr(search_api.pipeline, "search", boom)
    response = open_client.post("/search", json={"workspace_id": "ws1", "query": "x"})

    assert response.status_code == 500
    assert response.json() == {"detail": "Internal error"}


# ---------------------------------------------------------------- matching endpoints


def test_matching_endpoints_are_pending_manual(open_client):
    extract = open_client.post(
        "/extract/job-requirements", json={"workspace_id": "ws1", "job_text": "Python dev"}
    )
    match = open_client.post("/match/job", json={"workspace_id": "ws1", "job_text": "Python dev"})

    assert extract.status_code == 501
    assert extract.json()["detail"].startswith("MANUAL")
    assert match.status_code == 501


def test_match_job_validates_input(open_client):
    both = {
        "workspace_id": "ws1",
        "job_text": "x",
        "requirements": {"requirements": []},
    }
    assert open_client.post("/match/job", json={"workspace_id": "ws1"}).status_code == 422
    assert open_client.post("/match/job", json=both).status_code == 422
    assert (
        open_client.post("/match/job", json={"workspace_id": "../x", "job_text": "x"}).status_code
        == 422
    )


def test_match_job_passes_structured_requirements_through(open_client, monkeypatch):
    calls = []

    def fake_match(**kwargs):
        calls.append(kwargs)
        return [CandidateJobMatch(candidate_id="c1", score=0.8, summary="s", requirements=[])]

    monkeypatch.setattr(matching_api.candidate_matcher, "match_candidates", fake_match)
    requirements = JobRequirements(
        requirements=[JobRequirement(kind="skill", value="ASP.NET Core")]
    )
    response = open_client.post(
        "/match/job",
        json={
            "workspace_id": "ws1",
            "requirements": requirements.model_dump(),
            "top_k": 5,
            "candidate_ids": ["c1", "c2"],
        },
    )

    assert response.status_code == 200
    assert response.json()[0]["candidate_id"] == "c1"
    assert calls[0]["workspace_id"] == "ws1"
    assert calls[0]["requirements"] == requirements
    assert calls[0]["candidate_ids"] == ["c1", "c2"]
