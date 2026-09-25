"""🔒 MANUAL — all LLM prompts."""


def build_extraction_prompt(cv_text: str) -> str:
    """Prompt asking the LLM to extract a CandidateProfile as JSON from CV text."""
    raise NotImplementedError("MANUAL: structured extraction prompt")


def build_query_parse_prompt(query: str) -> str:
    """Prompt asking the LLM to turn a recruiter query into search filters."""
    raise NotImplementedError("MANUAL: query parsing prompt")


def build_explain_prompt(query: str, evidence_texts: list[str]) -> str:
    """Prompt asking the LLM why a candidate matches the query, quoting evidence."""
    raise NotImplementedError("MANUAL: explanation prompt")
