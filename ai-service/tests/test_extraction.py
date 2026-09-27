"""Profile extraction (LLM + heuristic fallback) and query parsing. All data is fake."""

import pytest

from app.errors import LLMError
from app.extraction import prompts
from app.extraction.extractor import extract_profile
from app.extraction.heuristic import extract_profile_heuristic
from app.retrieval.query_parser import parse_query
from app.vocabulary import find_languages, find_skills


class FakeLLM:
    """Returns queued responses from chat_json; an Exception in the queue is raised."""

    def __init__(self, *responses, model: str = "fake-model") -> None:
        self.model = model
        self.responses = list(responses)
        self.calls: list[list[dict]] = []

    def chat_json(self, messages, **kwargs):
        self.calls.append(messages)
        item = self.responses.pop(0)
        if isinstance(item, Exception):
            raise item
        return item


GOOD = {
    "full_name": "Lina Faris",
    "email": "lina@example.com",
    "phone": None,
    "location": "Amman",
    "years_of_experience": "4",
    "skills": ["python", "Python", "SQL Server"],
    "languages": ["english", "Arabic"],
    "job_titles": ["Backend Developer"],
    "education": [{"degree": "B.Sc.", "field": "CS", "institution": "Example U", "year": 2019}],
}

CV_EN = """Lina Faris
lina@example.com | +962 79 000 0000
Location: Amman, Jordan
Summary
Backend developer with 4 years of experience.
Work Experience
Backend Developer | Acme | 2021 - Present
• Built Python APIs with Django
Education
B.Sc. Computer Science, Example University, 2020
Languages
Arabic, English
"""

CV_AR = """سارة خالد
sara@example.com
الخبرات العملية
مطورة ويب | شركة المثال | 2018 - 2020
مطورة واجهات | شركة أخرى | 2019 - 2023
• تطوير واجهات باستخدام React
التعليم
بكالوريوس علم الحاسوب، جامعة المثال، 2018
اللغات
العربية، الإنجليزية
"""


# ---------------------------------------------------------------- LLM extraction


def test_extract_profile_tidies_llm_output():
    llm = FakeLLM(GOOD)

    profile = extract_profile(CV_EN, llm)

    assert profile.years_of_experience == 4.0
    assert profile.skills == ["Python", "SQL Server"]  # deduped + canonical
    assert profile.languages == ["English", "Arabic"]
    assert profile.education[0]["year"] == "2019"
    assert "<cv>" in llm.calls[0][1]["content"]


def test_extract_profile_retries_then_succeeds():
    llm = FakeLLM(LLMError("LLM returned invalid JSON"), {"years_of_experience": [1]}, GOOD)

    profile = extract_profile(CV_EN, llm)

    assert profile.full_name == "Lina Faris"
    assert len(llm.calls) == 3
    assert "not valid" in llm.calls[1][-1]["content"]


def test_extract_profile_gives_up_after_three_attempts():
    llm = FakeLLM(*[LLMError("boom")] * 3)
    with pytest.raises(LLMError, match="after 3 attempts"):
        extract_profile(CV_EN, llm)


def test_extract_profile_rejects_impossible_years():
    profile = extract_profile(CV_EN, FakeLLM({**GOOD, "years_of_experience": 250}))
    assert profile.years_of_experience is None


def test_extract_profile_without_llm_uses_heuristics():
    profile = extract_profile(CV_EN, FakeLLM(model=""))
    assert profile.email == "lina@example.com"


# ---------------------------------------------------------------- heuristic


def test_heuristic_english_cv():
    p = extract_profile_heuristic(CV_EN)

    assert p.full_name == "Lina Faris"
    assert p.email == "lina@example.com"
    assert p.phone == "+962 79 000 0000"
    assert p.location == "Amman, Jordan"
    assert p.years_of_experience == 4.0  # stated in the summary
    assert {"Python", "Django"} <= set(p.skills)
    assert p.languages == ["Arabic", "English"]
    assert p.job_titles == ["Backend Developer"]
    assert p.education[0]["year"] == "2020"


def test_heuristic_arabic_cv_merges_overlapping_periods():
    p = extract_profile_heuristic(CV_AR)

    assert p.full_name == "سارة خالد"
    assert p.years_of_experience == 5.0  # 2018–2020 and 2019–2023 overlap → 2018–2023
    assert p.skills == ["React"]
    assert p.languages == ["Arabic", "English"]
    assert p.job_titles == ["مطورة ويب", "مطورة واجهات"]
    assert p.education[0]["degree"].startswith("بكالوريوس")


# ---------------------------------------------------------------- vocabulary


def test_vocabulary_matches_symbols_and_arabic():
    assert find_skills("C#, ASP.NET Core and C++") == ["C#", "C++", ".NET"]
    assert find_skills("خبرة في بايثون والمبيعات") == ["Python", "Sales"]
    assert find_skills("javascript") == ["JavaScript"]  # not "Java"
    assert find_languages("يتحدث الإنجليزية بطلاقة") == ["English"]


# ---------------------------------------------------------------- query parsing


@pytest.mark.parametrize(
    "query, years, skills, languages",
    [
        ("Python developer with 5+ years, speaks English", 5, ["Python"], ["English"]),
        ("مطور بايثون خبرة ٥ سنوات يتحدث الانجليزية", 5, ["Python"], ["English"]),
        ("3-5 years React", 3, ["React"], []),
        ("محاسب خبرة سنتين", 2, ["Accounting"], []),
        ("sales manager", None, ["Sales"], []),
    ],
)
def test_parse_query_rules(query, years, skills, languages):
    filters = parse_query(query)
    assert filters.min_years == years
    assert filters.skills == skills
    assert filters.languages == languages


def test_parse_query_with_llm_merges_and_falls_back():
    llm = FakeLLM({"min_years": 3, "skills": ["docker"], "languages": []})
    filters = parse_query("python dev", llm)
    assert filters.min_years == 3
    assert filters.skills == ["Python", "Docker"]

    broken = FakeLLM(LLMError("down"))
    assert parse_query("python dev 4 years", broken).min_years == 4


# ---------------------------------------------------------------- prompts


def test_prompts_wrap_untrusted_text():
    assert "<cv>\nhello\n</cv>" in prompts.build_extraction_prompt("hello")
    assert "<query>\nq\n</query>" in prompts.build_query_parse_prompt("q")
    explain = prompts.build_explain_prompt("q", ["first", "second"])
    assert "[1]\nfirst" in explain and "[2]\nsecond" in explain
