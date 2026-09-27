"""End-to-end search pipeline + explainer with fake models (no downloads)."""

import pytest

from app.embeddings.embedder import Embedder
from app.errors import CandidateNotFoundError, LLMError
from app.explain.evidence import pick_quote
from app.explain.explainer import explain_candidate, verify_quotes
from app.models import CandidateProfile, Chunk
from app.retrieval.bm25_index import BM25Index
from app.retrieval.pipeline import SearchPipeline
from app.storage.profile_store import ProfileStore


def _profile(years: float | None, skills: list[str], languages: list[str]) -> CandidateProfile:
    return CandidateProfile(
        full_name=None, email=None, phone=None, location=None,
        years_of_experience=years, skills=skills, languages=languages,
        job_titles=[], education=[],
    )


CVS = {
    "alice": (
        "Python backend developer\nBuilt Django APIs for 6 years",
        _profile(6, ["Python"], ["English"]),
    ),
    "bob": ("Junior Python developer\nFlask side projects", _profile(1, ["Python"], ["English"])),
    "carol": ("Sales manager\nGrew B2B revenue", _profile(8, ["Sales"], ["Arabic"])),
    "dan": ("Java developer\nSpring microservices", _profile(None, ["Java"], [])),
}


class FakeLLM:
    def __init__(self, *responses, model: str = "fake") -> None:
        self.model = model
        self.responses = list(responses)

    def chat_json(self, messages, **kwargs):
        item = self.responses.pop(0)
        if isinstance(item, Exception):
            raise item
        return item


@pytest.fixture
def pipeline(tmp_path, vector_store, fake_encoder) -> SearchPipeline:
    embedder = Embedder("fake", model=fake_encoder)
    profiles = ProfileStore(tmp_path / "profiles")
    bm25_dir = tmp_path / "bm25"

    for cid, (text, profile) in CVS.items():
        chunk = Chunk(chunk_id=f"{cid}-0", candidate_id=cid, workspace_id="ws1",
                      section="experience", text=text)
        vector_store.add_chunks([chunk], embedder.embed_documents([text]))
        BM25Index("ws1", directory=bm25_dir).add([chunk])
        profiles.put("ws1", cid, profile)

    return SearchPipeline(
        embedder=embedder,
        store=vector_store,
        profiles=profiles,
        bm25_factory=lambda ws: BM25Index(ws, directory=bm25_dir),
    )


def test_search_ranks_relevant_candidates_first(pipeline):
    results = pipeline.search("ws1", "python developer", top_k=3)

    assert [r.candidate_id for r in results][:2] in (["alice", "bob"], ["bob", "alice"])
    assert all(0 <= r.score <= 1 for r in results)
    assert results[0].evidence and "Python" in results[0].evidence[0].quote
    assert "Matches Python" in results[0].reason


def test_search_drops_candidates_below_min_years(pipeline):
    ids = [r.candidate_id for r in pipeline.search("ws1", "python developer 5+ years", 10)]

    assert ids[0] == "alice"
    assert "bob" not in ids  # 1 year < 5


def test_unknown_years_is_penalized_not_dropped(pipeline):
    ids = [r.candidate_id for r in pipeline.search("ws1", "java developer 3 years", 10)]
    assert "dan" in ids


def test_search_respects_candidate_allow_list(pipeline):
    assert {r.candidate_id for r in pipeline.search("ws1", "developer", 10, ["carol"])} <= {"carol"}
    assert pipeline.search("ws1", "developer", 10, []) == []


def test_search_is_scoped_to_workspace(pipeline):
    assert pipeline.search("other_ws", "python", 10) == []


def test_arabic_query_gets_arabic_reason(pipeline):
    results = pipeline.search("ws1", "مطور python", top_k=1)
    assert "يطابق" in results[0].reason


def test_search_uses_reranker_scores(pipeline):
    class FakeReranker:
        def rerank(self, query, chunks, top_k):
            return sorted(
                ((c, 0.99 if c.candidate_id == "carol" else 0.1) for c in chunks),
                key=lambda p: -p[1],
            )[:top_k]

    pipeline.reranker = FakeReranker()
    assert pipeline.search("ws1", "developer", 1)[0].candidate_id == "carol"


# ---------------------------------------------------------------- evidence


def test_pick_quote_returns_best_matching_line_verbatim():
    text = "Summary line here\nBuilt Django APIs in Python\nLikes hiking"
    assert pick_quote(text, "python django") == "Built Django APIs in Python"
    assert pick_quote("- Led a team of 5", "team") == "Led a team of 5"
    assert pick_quote("short\nA longer line without matches", "zzz") == (
        "A longer line without matches"
    )


# ---------------------------------------------------------------- explainer


def test_explain_without_llm_uses_template(pipeline):
    result = explain_candidate("alice", "ws1", "python", FakeLLM(model=""), pipeline=pipeline)

    assert result.candidate_id == "alice"
    assert result.evidence[0].quote in CVS["alice"][0]


def test_explain_keeps_only_verbatim_llm_quotes(pipeline):
    llm = FakeLLM(
        {
            "reason": "Strong Python background.",
            "evidence": [
                {"excerpt": 1, "quote": "Built Django APIs for 6 years"},
                {"excerpt": 1, "quote": "Invented Python"},  # hallucinated → dropped
            ],
        }
    )

    result = explain_candidate("alice", "ws1", "python", llm, pipeline=pipeline)

    assert result.reason == "Strong Python background."
    assert [e.quote for e in result.evidence] == ["Built Django APIs for 6 years"]


def test_explain_falls_back_when_llm_fails_or_invents_everything(pipeline):
    failing = FakeLLM(LLMError("down"))
    assert explain_candidate("alice", "ws1", "python", failing, pipeline=pipeline).evidence

    liar = FakeLLM({"reason": "x", "evidence": [{"excerpt": 1, "quote": "made up"}]})
    result = explain_candidate("alice", "ws1", "python", liar, pipeline=pipeline)
    assert result.reason != "x"


def test_explain_unknown_candidate(pipeline):
    with pytest.raises(CandidateNotFoundError):
        explain_candidate("nobody", "ws1", "python", FakeLLM(model=""), pipeline=pipeline)


def test_verify_quotes_tolerates_whitespace_and_wrong_excerpt_number():
    chunks = [
        Chunk(chunk_id="1", candidate_id="a", workspace_id="w", section="skills", text="A"),
        Chunk(chunk_id="2", candidate_id="a", workspace_id="w", section="experience",
              text="Led a  team\nof 5 engineers"),
    ]
    evidence = verify_quotes([{"excerpt": 1, "quote": "Led a team of 5 engineers"}], chunks)
    assert [(e.section, e.quote) for e in evidence] == [
        ("experience", "Led a team of 5 engineers")
    ]
    assert verify_quotes("not a list", chunks) == []
