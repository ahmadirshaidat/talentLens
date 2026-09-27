"""🔒 MANUAL — pick evidence quotes and write a short "why" without an LLM.

🧸 EXPLAIN LIKE I'M 5
---------------------
When we show a candidate, the recruiter asks "WHY did you pick this person?".
A good answer shows PROOF: exact words copied from the CV. Not a summary — a quote.
Quotes can't lie: if it's in quotes, it's really in the CV.

To pick the best quote from a CV piece:
  1. Cut the piece into lines.
  2. Give each line points for every query word / wanted skill it contains.
  3. The line with the most points is the quote. (If no line has any points — the match
     was about MEANING, not exact words — we take the first meaningful line.)

To write the "why" sentence we just fill in a template with facts we already know:
    "Matches Python, SQL · 6 years experience (5+ required) · strongest in: experience"
It's in Arabic when the query is in Arabic.
"""

import re
from typing import TYPE_CHECKING

from app.models import CandidateResult, Chunk, Evidence
from app.retrieval.bm25_index import tokenize
from app.vocabulary import find_skills

if TYPE_CHECKING:
    from app.retrieval.pipeline import CandidateMatch

MAX_QUOTE_CHARS = 300
MAX_EVIDENCE = 3

_ARABIC_CHAR = re.compile(r"[؀-ۿ]")

_SECTION_NAMES_AR = {
    "header": "البيانات الشخصية",
    "summary": "الملخص",
    "experience": "الخبرات",
    "education": "التعليم",
    "skills": "المهارات",
    "projects": "المشاريع",
    "certifications": "الشهادات",
    "languages": "اللغات",
    "other": "أخرى",
}


def is_arabic(text: str) -> bool:
    return bool(_ARABIC_CHAR.search(text))


def pick_quote(chunk_text: str, query: str, wanted_skills: list[str] | None = None) -> str:
    """Return the most relevant line of `chunk_text` — always an exact substring of it."""
    query_tokens = set(tokenize(query))
    wanted = {s.lower() for s in wanted_skills or []}

    best_line, best_points = "", 0.0
    for raw in chunk_text.splitlines():
        line = raw.strip()
        if not line:
            continue
        # 🧸 One point per query word on the line, two points per wanted skill.
        points = float(len(query_tokens & set(tokenize(line))))
        points += 2 * len(wanted & {s.lower() for s in find_skills(line)})
        if points > best_points:
            best_line, best_points = line, points

    if not best_line:
        # 🧸 Nothing matched word-for-word: take the first line that says something.
        lines = [ln.strip() for ln in chunk_text.splitlines() if ln.strip()]
        best_line = next((ln for ln in lines if len(ln) >= 20), lines[0] if lines else "")

    # 🧸 Drop list markers ("- ", "• ") — what's left is still an exact piece of the CV.
    return best_line.lstrip("-•*▪● 	")[:MAX_QUOTE_CHARS].strip()


def build_evidence(chunks: list[Chunk], query: str, wanted_skills: list[str]) -> list[Evidence]:
    """One quote from each of the candidate's best chunks (no duplicate quotes)."""
    evidence: list[Evidence] = []
    seen: set[str] = set()
    for chunk in chunks:
        quote = pick_quote(chunk.text, query, wanted_skills)
        if quote and quote not in seen:
            seen.add(quote)
            evidence.append(Evidence(section=chunk.section, quote=quote))
        if len(evidence) >= MAX_EVIDENCE:
            break
    return evidence


def build_reason(match: "CandidateMatch", query: str) -> str:
    """Short template explanation built from facts, in the query's language."""
    arabic = is_arabic(query)
    parts: list[str] = []
    years = match.profile.years_of_experience if match.profile else None
    min_years = match.filters.min_years
    top_section = match.chunks[0].section if match.chunks else None

    if arabic:
        if match.matched_skills:
            parts.append("يطابق: " + "، ".join(match.matched_skills))
        if match.missing_skills:
            parts.append("غير مذكور: " + "، ".join(match.missing_skills))
        if years is not None:
            req = f" (المطلوب {min_years:g}+)" if min_years else ""
            span = "سنة واحدة" if years == 1 else f"{years:g} سنوات"
            parts.append(f"خبرة {span}{req}")
        elif min_years:
            parts.append("سنوات الخبرة غير واضحة في السيرة الذاتية")
        if match.matched_languages:
            parts.append("اللغات: " + "، ".join(match.matched_languages))
        if top_section:
            parts.append("أقوى تطابق في قسم " + _SECTION_NAMES_AR.get(top_section, top_section))
        return " · ".join(parts) or "تطابق دلالي مع نص السيرة الذاتية"

    if match.matched_skills:
        parts.append("Matches " + ", ".join(match.matched_skills))
    if match.missing_skills:
        parts.append("Not mentioned: " + ", ".join(match.missing_skills))
    if years is not None:
        req = f" ({min_years:g}+ required)" if min_years else ""
        unit = "year" if years == 1 else "years"
        parts.append(f"{years:g} {unit} of experience{req}")
    elif min_years:
        parts.append("Years of experience unclear from the CV")
    if match.matched_languages:
        parts.append("Languages: " + ", ".join(match.matched_languages))
    if top_section:
        parts.append(f"Strongest match in {top_section}")
    return " · ".join(parts) or "Semantic match with the CV text"


def template_result(match: "CandidateMatch", query: str) -> CandidateResult:
    """CandidateResult with a template reason and quoted evidence (no LLM needed)."""
    wanted = match.filters.skills
    return CandidateResult(
        candidate_id=match.candidate_id,
        score=round(match.score, 4),
        reason=build_reason(match, query),
        evidence=build_evidence(match.chunks, query, wanted),
    )
