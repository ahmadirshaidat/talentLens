"""Eval metrics, dataset loading, profile store and the sample CV generator."""

import json
import subprocess
import sys
from pathlib import Path

import pytest

from app.models import CandidateProfile
from app.parsing.parser import parse_document
from app.storage.profile_store import ProfileStore
from eval.metrics import ndcg_at_k, precision_at_k, recall_at_k, reciprocal_rank
from eval.run_eval import load_dataset

# ---------------------------------------------------------------- metrics


def test_precision_at_k():
    assert precision_at_k(["a", "x", "b", "y", "z"], {"a", "b", "c"}, 5) == pytest.approx(0.4)
    assert precision_at_k(["a"], {"a"}, 5) == pytest.approx(0.2)  # short list isn't a free pass
    assert precision_at_k(["a"], {"a"}, 0) == 0.0


def test_recall_and_mrr():
    assert recall_at_k(["a", "x", "b"], {"a", "b", "c", "d"}, 3) == pytest.approx(0.5)
    assert reciprocal_rank(["x", "y", "a"], {"a"}) == pytest.approx(1 / 3)
    assert reciprocal_rank(["x"], {"a"}) == 0.0


def test_ndcg_perfect_and_reversed():
    relevance = {"a": 2.0, "b": 1.0}
    assert ndcg_at_k(["a", "b", "x"], relevance, 3) == pytest.approx(1.0)
    reversed_score = ndcg_at_k(["b", "a"], relevance, 2)
    assert 0 < reversed_score < 1
    assert ndcg_at_k(["x", "y"], relevance, 2) == 0.0
    assert ndcg_at_k(["a"], {}, 2) == 0.0


def test_load_dataset(tmp_path):
    path = tmp_path / "d.json"
    path.write_text(
        json.dumps(
            {
                "workspace_id": "w",
                "queries": [
                    {"query": "q1", "relevant": {"a": 2}},
                    {"query": "q2", "relevant": ["b", "c"]},
                ],
            }
        ),
        encoding="utf-8",
    )
    data = load_dataset(path)
    assert data[0] == {"workspace_id": "w", "query": "q1", "relevant": {"a": 2.0}}
    assert data[1]["relevant"] == {"b": 1.0, "c": 1.0}


# ---------------------------------------------------------------- profile store


def test_profile_store_roundtrip(tmp_path):
    store = ProfileStore(tmp_path)
    profile = CandidateProfile(
        full_name="سارة", email=None, phone=None, location=None, years_of_experience=3,
        skills=["React"], languages=["Arabic"], job_titles=[], education=[],
    )

    store.put("ws1", "c1", profile)

    assert ProfileStore(tmp_path).get("ws1", "c1") == profile
    assert store.get("ws2", "c1") is None
    assert list(store.get_all("ws1")) == ["c1"]
    store.delete("ws1", "c1")
    assert store.get("ws1", "c1") is None
    with pytest.raises(ValueError):
        store.get("../x", "c1")


# ---------------------------------------------------------------- sample generator


def test_generate_sample_cvs(tmp_path):
    root = Path(__file__).resolve().parent.parent
    out, dataset = tmp_path / "cvs", tmp_path / "queries.json"
    subprocess.run(
        [sys.executable, "scripts/generate_sample_cvs.py", "--out", str(out),
         "--dataset", str(dataset)],
        cwd=root, check=True, capture_output=True,
    )

    files = sorted(out.iterdir())
    assert len(files) == 16
    assert {f.suffix for f in files} == {".pdf", ".docx"}
    for f in files:
        assert parse_document(f.read_bytes(), f.name).char_count > 200
    ids = {f.stem for f in files}
    for q in json.loads(dataset.read_text(encoding="utf-8"))["queries"]:
        assert set(q["relevant"]) <= ids
