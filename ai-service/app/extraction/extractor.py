"""🔒 MANUAL — LLM structured extraction (prompt + JSON schema + validation/retry).

🧸 EXPLAIN LIKE I'M 5
---------------------
A CV is a messy drawing. The web app wants a neat form:
    name, email, phone, city, years of experience, skills, languages, titles, degrees.

We ask the AI helper (LLM) to fill in the form. But AI helpers sometimes make mistakes:
they forget a box, write "five" instead of 5, or wrap the answer in extra text. So:

  1. ASK      – send the prompt (see prompts.py) and ask for JSON only.
  2. CHECK    – Pydantic checks the answer has the right boxes with the right types.
                ("years_of_experience must be a number" …)
  3. TIDY     – fix small things ourselves: "5" → 5.0, remove duplicate skills,
                "english" → "English", drop impossible values like 200 years.
  4. RETRY    – if the answer is still broken, tell the AI what was wrong and ask again
                (up to 3 tries). Like a teacher handing back homework with a red mark.
  5. NO AI?   – if no LLM is configured at all (no API key/model in .env), we use simple
                rules instead (heuristic.py) so the app still works for a demo.
"""

import logging
from typing import TYPE_CHECKING, Any

from pydantic import ValidationError

from app.errors import LLMError
from app.extraction import prompts
from app.extraction.heuristic import extract_profile_heuristic
from app.models import CandidateProfile
from app.vocabulary import canonical_language, canonical_skill

if TYPE_CHECKING:
    from app.llm.client import LLMClient

logger = logging.getLogger(__name__)

MAX_ATTEMPTS = 3
# 🧸 Very long CVs are cut so we don't pay for (or overflow) a giant prompt.
# 24k characters is ~8 pages — plenty for any real CV.
MAX_CV_CHARS = 24_000


def extract_profile(cv_text: str, llm: "LLMClient") -> CandidateProfile:
    """Extract a structured candidate profile from CV text.

    Args:
        cv_text: Full parsed CV text.
        llm: LLM client used for the extraction call.

    Returns:
        A validated CandidateProfile. Retries on invalid JSON / schema errors.

    Raises:
        LLMError: The LLM kept failing after MAX_ATTEMPTS.
    """
    # 🧸 Step 5 from the top: no AI configured → use the simple rules.
    if not getattr(llm, "model", ""):
        logger.warning("LLM_MODEL not configured; using heuristic profile extraction")
        return extract_profile_heuristic(cv_text)

    messages = [
        {"role": "system", "content": prompts.EXTRACTION_SYSTEM},
        {"role": "user", "content": prompts.build_extraction_prompt(cv_text[:MAX_CV_CHARS])},
    ]

    last_error = "unknown error"
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            data = llm.chat_json(messages)  # 🧸 Step 1: ASK
            profile = CandidateProfile.model_validate(_tidy(data))  # 🧸 Steps 2 + 3
            logger.info("Extracted profile on attempt %d", attempt)
            return profile
        except (LLMError, ValidationError, ValueError) as exc:
            last_error = _short_error(exc)
            logger.warning("Extraction attempt %d failed: %s", attempt, last_error)
            # 🧸 Step 4: RETRY — tell the AI what went wrong.
            messages = messages[:2] + [
                {"role": "user", "content": prompts.build_extraction_retry_prompt(last_error)}
            ]

    raise LLMError(f"Profile extraction failed after {MAX_ATTEMPTS} attempts: {last_error}")


def _tidy(data: dict[str, Any]) -> dict[str, Any]:
    """Fix the small, predictable mistakes before strict validation.

    🧸 Like straightening a drawing before putting it in the frame: numbers-written-as-
    text become numbers, duplicates are removed. But if whole boxes are MISSING from the
    form, the AI didn't really follow the instructions → that's an error, ask again.
    """
    missing = [key for key in CandidateProfile.model_fields if key not in data]
    if missing:
        raise ValueError(f"missing keys: {', '.join(missing)}")
    return {
        "full_name": _clean_str(data.get("full_name")),
        "email": _clean_str(data.get("email")),
        "phone": _clean_str(data.get("phone")),
        "location": _clean_str(data.get("location")),
        "years_of_experience": _clean_years(data.get("years_of_experience")),
        "skills": _dedupe(canonical_skill(s) for s in _str_list(data.get("skills"))),
        "languages": _dedupe(canonical_language(s) for s in _str_list(data.get("languages"))),
        "job_titles": _dedupe(_str_list(data.get("job_titles"))),
        "education": _clean_education(data.get("education")),
    }


def _clean_str(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    return text if text and text.lower() not in {"null", "none", "n/a", "unknown"} else None


def _clean_years(value: Any) -> float | None:
    if value is None or value == "":
        return None
    try:
        years = float(str(value).replace("+", "").strip())
    except ValueError:
        return None
    # 🧸 Nobody has -2 or 90 years of experience; treat those as "don't know".
    return round(years, 1) if 0 <= years <= 60 else None


def _str_list(value: Any) -> list[str]:
    if value is None:
        return []
    if isinstance(value, str):  # the model sometimes returns "Python, SQL"
        value = value.split(",")
    return [str(v).strip() for v in value if v is not None and str(v).strip()]


def _dedupe(items) -> list[str]:
    seen: set[str] = set()
    out: list[str] = []
    for item in items:
        key = item.lower()
        if key not in seen:
            seen.add(key)
            out.append(item)
    return out


def _clean_education(value: Any) -> list[dict]:
    if not value:
        return []
    if isinstance(value, dict):
        value = [value]
    out: list[dict] = []
    for item in value:
        if isinstance(item, str):
            item = {"degree": item}
        if isinstance(item, dict):
            keys = ("degree", "field", "institution", "year")
            cleaned = {k: _clean_str(item.get(k)) for k in keys}
            if any(cleaned.values()):
                out.append(cleaned)
    return out


def _short_error(exc: Exception) -> str:
    if isinstance(exc, ValidationError):
        return "; ".join(
            f"{'.'.join(str(p) for p in err['loc'])}: {err['msg']}" for err in exc.errors()[:5]
        )
    return str(exc)
