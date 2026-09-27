"""🔒 MANUAL — end-to-end search pipeline.

🧸 EXPLAIN LIKE I'M 5
---------------------
Finding the right people is like a talent show with several rounds:

  Round 0 — UNDERSTAND THE QUESTION (query_parser.py)
      "Python dev, 5+ years, English" → hard rules {min_years: 5, skills: [Python], …}

  Round 1 — TWO FAST SCOUTS look at every CV piece in THIS company's box only:
      • Dense scout (embeddings): finds pieces with similar MEANING
        ("backend engineer" ≈ "server-side developer"), even in another language.
      • BM25 scout (keywords): finds pieces with the exact WORDS ("C#", "SAP").
      Each brings back its top ~50 pieces.

  Round 2 — MERGE THE TWO LISTS (fusion.py, RRF)
      Pieces both scouts liked go to the top. Keep the best ~30.

  Round 3 — THE CAREFUL JUDGE (reranker.py)
      Reads the question + each of the 30 pieces together and scores them 0..1.

  Round 4 — PIECES → PEOPLE (aggregate.py)
      Group pieces by candidate; one score per person.

  Round 5 — CHECK THE HARD RULES using each person's profile
      • Fewer years than required → out.
      • Missing wanted skills / languages → score goes down (not out: the CV might
        just phrase it differently).

  Round 6 — PREPARE THE ANSWER
      Best `top_k` people, each with a score, a short reason, and quotes as evidence.

Everything is scoped by `workspace_id`: we never look in another company's box.
"""

import logging
from dataclasses import dataclass, field
from functools import lru_cache

from app.config import get_settings
from app.embeddings.embedder import Embedder, get_embedder
from app.explain.evidence import template_result
from app.llm.client import LLMClient, get_llm_client
from app.models import CandidateProfile, CandidateResult, Chunk
from app.retrieval.aggregate import aggregate_candidates
from app.retrieval.bm25_index import BM25Index
from app.retrieval.fusion import reciprocal_rank_fusion
from app.retrieval.query_parser import QueryFilters, parse_query
from app.retrieval.reranker import Reranker, get_reranker
from app.storage.profile_store import ProfileStore, get_profile_store
from app.storage.vector_store import VectorStore, get_vector_store
from app.vocabulary import find_languages, find_skills

logger = logging.getLogger(__name__)

RRF_K = 60

# 🧸 How much a score shrinks for each kind of "not quite what you asked for".
UNKNOWN_YEARS_PENALTY = 0.85  # asked for years, CV doesn't say → maybe, but less sure
MISSING_LANGUAGE_PENALTY = 0.7
UNKNOWN_LANGUAGE_PENALTY = 0.9
SKILL_BASE = 0.35  # with 0 of the wanted skills a score keeps 35%; with all of them 100%


@dataclass
class CandidateMatch:
    """One candidate after ranking — everything needed to build the answer."""

    candidate_id: str
    score: float
    chunks: list[Chunk]  # supporting chunks, best first
    filters: QueryFilters
    profile: CandidateProfile | None = None
    matched_skills: list[str] = field(default_factory=list)
    missing_skills: list[str] = field(default_factory=list)
    matched_languages: list[str] = field(default_factory=list)
    passes_filters: bool = True


class SearchPipeline:
    """Wires the retrieval pieces together. Every dependency can be swapped in tests."""

    def __init__(
        self,
        embedder: Embedder,
        store: VectorStore,
        profiles: ProfileStore,
        bm25_factory=BM25Index,
        reranker: Reranker | None = None,
        llm: LLMClient | None = None,
        retrieval_candidates: int = 50,
        rerank_candidates: int = 30,
    ) -> None:
        self.embedder = embedder
        self.store = store
        self.profiles = profiles
        self.bm25_factory = bm25_factory
        self.reranker = reranker
        self.llm = llm
        self.retrieval_candidates = retrieval_candidates
        self.rerank_candidates = rerank_candidates

    # ------------------------------------------------------------------ public

    def search(
        self,
        workspace_id: str,
        query: str,
        top_k: int = 10,
        candidate_ids: list[str] | None = None,
    ) -> list[CandidateResult]:
        """Ranked candidates with score, reason and evidence (template reason, no LLM)."""
        matches = self.rank(workspace_id, query, candidate_ids, drop_filtered=True)
        return [template_result(m, query) for m in matches[:top_k]]

    def rank(
        self,
        workspace_id: str,
        query: str,
        candidate_ids: list[str] | None = None,
        drop_filtered: bool = True,
    ) -> list[CandidateMatch]:
        """Rounds 0–5. Returns all matching candidates, best first."""
        if candidate_ids is not None and not candidate_ids:
            return []  # 🧸 an empty allow-list means "nobody", not "everybody"

        # Round 0: understand the question
        filters = parse_query(query, self.llm)

        # Round 1: two fast scouts
        n = self.retrieval_candidates
        dense = self.store.query(
            workspace_id, self.embedder.embed_query(query), n, candidate_ids
        )
        keyword = self.bm25_factory(workspace_id).search(query, n, candidate_ids)
        if not dense and not keyword:
            return []

        # Round 2: merge the two lists by rank
        fused = reciprocal_rank_fusion(
            [[c.chunk_id for c, _ in dense], [cid for cid, _ in keyword]], k=RRF_K
        )[: self.rerank_candidates]
        chunks = self._load_chunks(workspace_id, [cid for cid, _ in fused], dense)

        # Round 3: careful judge (or, without a reranker, use the fusion score)
        if self.reranker is not None:
            scored = self.reranker.rerank(query, chunks, top_k=len(chunks))
        else:
            # 🧸 Best possible RRF score is being 1st in both lists: 2 / (k + 1).
            # Dividing by it turns the score into a 0..1 number.
            best_possible = 2.0 / (RRF_K + 1)
            fused_score = dict(fused)
            scored = [(c, fused_score[c.chunk_id] / best_possible) for c in chunks]

        # Round 4: pieces → people
        grouped = aggregate_candidates(scored)

        # Round 5: hard rules
        profiles = self.profiles.get_all(workspace_id)
        matches = [
            self._apply_filters(
                CandidateMatch(candidate_id=cid, score=score, chunks=cs, filters=filters),
                profiles.get(cid),
            )
            for cid, score, cs in grouped
        ]
        if drop_filtered:
            matches = [m for m in matches if m.passes_filters]
        matches.sort(key=lambda m: (-m.score, m.candidate_id))
        logger.info(
            "Search in %s: %d dense, %d bm25, %d candidates (filters: %s)",
            workspace_id, len(dense), len(keyword), len(matches), filters.model_dump(),
        )
        return matches

    # ----------------------------------------------------------------- helpers

    def _load_chunks(
        self, workspace_id: str, chunk_ids: list[str], dense: list[tuple[Chunk, float]]
    ) -> list[Chunk]:
        """Chunk objects for the fused ids. Dense hits we already have; BM25-only hits
        are fetched from the vector store (it's the source of truth for chunk text)."""
        known = {c.chunk_id: c for c, _ in dense}
        missing = [cid for cid in chunk_ids if cid not in known]
        for chunk in self.store.get_chunks(workspace_id, missing):
            known[chunk.chunk_id] = chunk
        return [known[cid] for cid in chunk_ids if cid in known]

    @staticmethod
    def _apply_filters(match: CandidateMatch, profile: CandidateProfile | None) -> CandidateMatch:
        filters = match.filters
        match.profile = profile
        cv_text = "\n".join(c.text for c in match.chunks)

        # 🧸 Years: the only rule that can knock someone OUT.
        if filters.min_years is not None:
            years = profile.years_of_experience if profile else None
            if years is None:
                match.score *= UNKNOWN_YEARS_PENALTY
            elif years < filters.min_years:
                match.passes_filters = False

        # 🧸 Skills: look in the profile AND in the matched CV text.
        if filters.skills:
            have = {s.lower() for s in (profile.skills if profile else [])}
            have |= {s.lower() for s in find_skills(cv_text)}
            match.matched_skills = [s for s in filters.skills if s.lower() in have]
            match.missing_skills = [s for s in filters.skills if s.lower() not in have]
            coverage = len(match.matched_skills) / len(filters.skills)
            match.score *= SKILL_BASE + (1 - SKILL_BASE) * coverage

        # 🧸 Languages
        if filters.languages:
            have_langs = {s.lower() for s in (profile.languages if profile else [])}
            have_langs |= {s.lower() for s in find_languages(cv_text)}
            match.matched_languages = [s for s in filters.languages if s.lower() in have_langs]
            if not have_langs:
                match.score *= UNKNOWN_LANGUAGE_PENALTY
            elif len(match.matched_languages) < len(filters.languages):
                match.score *= MISSING_LANGUAGE_PENALTY

        return match


@lru_cache
def get_pipeline() -> SearchPipeline:
    settings = get_settings()
    return SearchPipeline(
        embedder=get_embedder(),
        store=get_vector_store(),
        profiles=get_profile_store(),
        reranker=get_reranker() if settings.use_reranker else None,
        llm=get_llm_client() if settings.use_llm_query_parser else None,
        retrieval_candidates=settings.retrieval_candidates,
        rerank_candidates=settings.rerank_candidates,
    )


def search(
    workspace_id: str,
    query: str,
    top_k: int = 10,
    candidate_ids: list[str] | None = None,
) -> list[CandidateResult]:
    """Run the full search: parse query -> dense + BM25 -> RRF -> rerank -> aggregate.

    Args:
        workspace_id: Tenant scope; never search outside it.
        query: Recruiter query.
        top_k: Number of candidates to return.
        candidate_ids: Optional allow-list restricting the search.

    Returns:
        Ranked candidates with score, reason, and evidence.
    """
    return get_pipeline().search(workspace_id, query, top_k, candidate_ids)
