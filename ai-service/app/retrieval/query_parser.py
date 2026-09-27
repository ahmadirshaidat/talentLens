"""🔒 MANUAL — turn a recruiter query into structured filters.

🧸 EXPLAIN LIKE I'M 5
---------------------
A recruiter types:  "Python developer with 5+ years, speaks English"
                    "مطور بايثون خبرة 5 سنوات يتحدث الانجليزية"

Some parts are FUZZY ("developer") — the search engines handle those.
Some parts are HARD RULES ("5+ years") — a search engine is bad at counting, so we
pull those out and check them ourselves against the candidate's profile:

    {"min_years": 5, "skills": ["Python"], "languages": ["English"]}

How do we pull them out? With patterns (regex) and our word list (app/vocabulary.py):
  - a number next to "years"/"سنوات"  → min_years
  - words from the skills list         → skills
  - words from the languages list      → languages

Optionally, an LLM can do it instead (smarter with weird phrasing, but slower and costs
money). If the LLM fails, we quietly fall back to the patterns.
"""

import logging
import re
from typing import TYPE_CHECKING

from pydantic import BaseModel

from app.errors import LLMError
from app.extraction import prompts
from app.vocabulary import (
    canonical_language,
    canonical_skill,
    find_languages,
    find_skills,
    normalize,
)

if TYPE_CHECKING:
    from app.llm.client import LLMClient

logger = logging.getLogger(__name__)


class QueryFilters(BaseModel):
    """Filters parsed from a query. Empty fields mean "no constraint"."""

    min_years: float | None = None
    skills: list[str] = []
    languages: list[str] = []

    @property
    def is_empty(self) -> bool:
        return self.min_years is None and not self.skills and not self.languages


# 🧸 Arabic keyboards type ٥ instead of 5. Turn them into normal digits first.
_ARABIC_DIGITS = str.maketrans("٠١٢٣٤٥٦٧٨٩", "0123456789")

# "5 years", "5+ years", "3-5 years" (→ 3), "5 سنوات", "٥ سنوات خبرة", "10 yrs"
_YEARS = re.compile(
    r"(\d{1,2}(?:\.\d)?)\s*(?:\+|(?:-|–|to|الي)\s*\d{1,2})?\s*\+?\s*"
    r"(?:years?|yrs?|سنوات|سنين|سنه|اعوام|عام)"
)
# 🧸 Arabic has special words for "two years" and "one year".
_ARABIC_WORD_YEARS = {"سنتين": 2.0, "عامين": 2.0, "سنتان": 2.0}


def parse_query(query: str, llm: "LLMClient | None" = None) -> QueryFilters:
    """Extract filters (years >= N, skills, languages) from a natural-language query.

    Args:
        query: Recruiter query, Arabic or English.
        llm: Optional LLM client if parsing is LLM-assisted.

    Returns:
        Parsed filters; empty fields mean "no constraint".
    """
    rules = _parse_with_rules(query)
    if llm is None or not getattr(llm, "model", ""):
        return rules

    try:
        smart = _parse_with_llm(query, llm)
    except LLMError as exc:
        logger.warning("LLM query parsing failed, using rules: %s", exc)
        return rules

    # 🧸 Combine both: anything either one found counts.
    return QueryFilters(
        min_years=smart.min_years if smart.min_years is not None else rules.min_years,
        skills=_union(rules.skills, smart.skills),
        languages=_union(rules.languages, smart.languages),
    )


def _parse_with_rules(query: str) -> QueryFilters:
    text = normalize(query).translate(_ARABIC_DIGITS)

    years: list[float] = [float(m.group(1)) for m in _YEARS.finditer(text)]
    years += [v for word, v in _ARABIC_WORD_YEARS.items() if word in text]
    years = [y for y in years if 0 < y <= 50]

    return QueryFilters(
        # 🧸 If the query says two numbers, the bigger one is the real requirement.
        min_years=max(years) if years else None,
        skills=find_skills(query),
        languages=find_languages(query),
    )


def _parse_with_llm(query: str, llm: "LLMClient") -> QueryFilters:
    data = llm.chat_json(
        [
            {"role": "system", "content": prompts.QUERY_PARSE_SYSTEM},
            {"role": "user", "content": prompts.build_query_parse_prompt(query)},
        ]
    )
    min_years = data.get("min_years")
    try:
        min_years = float(min_years) if min_years is not None else None
    except (TypeError, ValueError):
        min_years = None
    return QueryFilters(
        min_years=min_years if min_years and 0 < min_years <= 50 else None,
        skills=[canonical_skill(s) for s in data.get("skills") or [] if isinstance(s, str)],
        languages=[
            canonical_language(s) for s in data.get("languages") or [] if isinstance(s, str)
        ],
    )


def _union(a: list[str], b: list[str]) -> list[str]:
    out = list(a)
    seen = {x.lower() for x in a}
    for x in b:
        if x.lower() not in seen:
            seen.add(x.lower())
            out.append(x)
    return out
