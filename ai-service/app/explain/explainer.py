"""🔒 MANUAL — "why this candidate" with quoted evidence.

🧸 EXPLAIN LIKE I'M 5
---------------------
The recruiter clicks "Why?" on one candidate. We answer in 3 steps:

  1. FIND THE PROOF — run the normal search, but only inside this one person's CV,
     so we get their pieces that best match the question (and their score).

  2. ASK THE AI TO EXPLAIN — we hand the AI those pieces, numbered [1] [2] [3], and say:
     "Explain the match, and copy EXACT words from the pieces as proof."

  3. CHECK THE AI'S HOMEWORK — AI helpers sometimes "remember" things that aren't there
     (called hallucination). So for every quote, we check: is this text REALLY inside
     piece [n]? If not → thrown away. If the AI gave no real quotes at all, or there is
     no AI configured, or the AI is down → we use the simple template answer
     (evidence.py), which only ever copies real lines. The recruiter never sees a fake quote.
"""

import logging
import re
from typing import TYPE_CHECKING

from app.errors import CandidateNotFoundError, LLMError
from app.explain.evidence import MAX_QUOTE_CHARS, template_result
from app.extraction import prompts
from app.models import CandidateResult, Chunk, Evidence

if TYPE_CHECKING:
    from app.llm.client import LLMClient
    from app.retrieval.pipeline import SearchPipeline

logger = logging.getLogger(__name__)

MAX_EXCERPTS = 4


def explain_candidate(
    candidate_id: str,
    workspace_id: str,
    query: str,
    llm: "LLMClient",
    pipeline: "SearchPipeline | None" = None,
) -> CandidateResult:
    """Explain why a candidate matches the query.

    Args:
        candidate_id: Candidate to explain.
        workspace_id: Tenant scope.
        query: Recruiter query.
        llm: LLM client used to write the reason.
        pipeline: Search pipeline (defaults to the shared one; injectable for tests).

    Returns:
        CandidateResult whose evidence quotes come verbatim from the CV.

    Raises:
        CandidateNotFoundError: The candidate has nothing indexed in this workspace.
    """
    if pipeline is None:
        from app.retrieval.pipeline import get_pipeline  # avoid an import cycle

        pipeline = get_pipeline()

    # 🧸 Step 1: find the proof. drop_filtered=False → even if they fail a hard rule
    # (e.g. too few years), we still explain them; the reason will mention it.
    matches = pipeline.rank(
        workspace_id, query, candidate_ids=[candidate_id], drop_filtered=False
    )
    if not matches:
        raise CandidateNotFoundError(f"Candidate {candidate_id} not found in workspace")
    match = matches[0]
    fallback = template_result(match, query)

    if not getattr(llm, "model", ""):
        return fallback

    excerpts = match.chunks[:MAX_EXCERPTS]
    try:
        # 🧸 Step 2: ask the AI.
        data = llm.chat_json(
            [
                {"role": "system", "content": prompts.EXPLAIN_SYSTEM},
                {
                    "role": "user",
                    "content": prompts.build_explain_prompt(query, [c.text for c in excerpts]),
                },
            ]
        )
    except LLMError as exc:
        logger.warning("LLM explanation failed, using template: %s", exc)
        return fallback

    # 🧸 Step 3: check the homework.
    evidence = verify_quotes(data.get("evidence"), excerpts)
    reason = str(data.get("reason") or "").strip()
    if not evidence or not reason:
        logger.info("LLM explanation had no verifiable quotes; using template")
        return fallback

    return CandidateResult(
        candidate_id=candidate_id,
        score=fallback.score,
        reason=reason,
        evidence=evidence,
    )


def _squash(text: str) -> str:
    """Compare text ignoring spacing differences (PDFs love odd spaces and line breaks)."""
    return re.sub(r"\s+", " ", text).strip()


def verify_quotes(raw_evidence, excerpts: list[Chunk]) -> list[Evidence]:
    """Keep only quotes that really appear in the excerpt they claim to come from."""
    if not isinstance(raw_evidence, list):
        return []
    verified: list[Evidence] = []
    for item in raw_evidence:
        if not isinstance(item, dict):
            continue
        quote = _squash(str(item.get("quote") or "")).strip(" \"'“”«»")
        try:
            index = int(item.get("excerpt")) - 1
        except (TypeError, ValueError):
            index = -1
        if not quote:
            continue

        # 🧸 Look in the piece the AI named first; if it named the wrong number but the
        # quote is real, we still accept it from the piece where it really is.
        candidates = ([excerpts[index]] if 0 <= index < len(excerpts) else []) + excerpts
        source = next((c for c in candidates if quote in _squash(c.text)), None)
        if source is None:
            logger.info("Dropped unverifiable quote: %.60s", quote)
            continue
        if all(e.quote != quote for e in verified):
            verified.append(Evidence(section=source.section, quote=quote[:MAX_QUOTE_CHARS]))
    return verified
