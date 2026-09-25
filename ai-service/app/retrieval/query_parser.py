"""🔒 MANUAL — turn a recruiter query into structured filters."""

from typing import TYPE_CHECKING

from pydantic import BaseModel

if TYPE_CHECKING:
    from app.llm.client import LLMClient


class QueryFilters(BaseModel):
    """Filters parsed from a query. Fields are a starting point — reshape freely."""

    min_years: float | None = None
    skills: list[str] = []
    languages: list[str] = []


def parse_query(query: str, llm: "LLMClient | None" = None) -> QueryFilters:
    """Extract filters (years >= N, skills, languages) from a natural-language query.

    Args:
        query: Recruiter query, Arabic or English.
        llm: Optional LLM client if parsing is LLM-assisted.

    Returns:
        Parsed filters; empty fields mean "no constraint".
    """
    raise NotImplementedError("MANUAL: parse recruiter query into filters")
