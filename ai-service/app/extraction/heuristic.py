"""🔒 MANUAL — rule-based profile extraction, used when no LLM is configured.

🧸 EXPLAIN LIKE I'M 5
---------------------
No AI helper? We can still fill most of the form with simple "look for patterns" rules:

  - EMAIL:  something@something.something           → easy with a pattern (regex)
  - PHONE:  a "+" and/or 8–15 digits with spaces/dashes
  - NAME:   usually the very first line of the CV
  - SKILLS / LANGUAGES: look for words from our word list (app/vocabulary.py)
  - YEARS:  either the CV says it ("5 years of experience", "خبرة 5 سنوات"),
            or we add up the job dates like "2019 – 2023" and "2021 – Present".
  - TITLES / DEGREES: lines in the Experience / Education sections that contain words
            like "Developer", "Engineer", "مطور" / "Bachelor", "بكالوريوس".

It's not as smart as the AI (it can't understand sentences), but it's free, instant,
and never makes things up — it only copies what it finds.
"""

import re
from datetime import date

from app.chunking.section_chunker import detect_sections
from app.models import CandidateProfile
from app.vocabulary import find_languages, find_skills, normalize

_EMAIL = re.compile(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")
_PHONE = re.compile(r"(?<!\w)\+?\d[\d\s\-()]{6,18}\d(?!\w)")
_YEARS_STATED = re.compile(
    r"(\d{1,2}(?:\.\d)?)\s*\+?\s*(?:years?|yrs?|سنوات|سنه|سنين|اعوام|عام)", re.IGNORECASE
)
# "2019 - 2023", "2019 – Present", "01/2020 - الآن". Matched against normalize()d text,
# so Arabic words are written in their normalized spelling (الآن → الان, حتى → حتي).
_DATE_RANGE = re.compile(
    r"((?:19|20)\d{2})\s*(?:-|–|—|to|الي|حتي)\s*((?:19|20)\d{2}|present|current|now|الان|حاليا)",
    re.IGNORECASE,
)
_LOCATION_LABEL = re.compile(
    r"^(?:location|address|city|العنوان|الموقع|المدينه|مكان الاقامه)\s*[:：]\s*(.+)$",
    re.IGNORECASE,
)

_TITLE_WORDS = (
    "developer", "engineer", "manager", "analyst", "specialist", "designer", "consultant",
    "accountant", "officer", "lead", "architect", "scientist", "administrator", "intern",
    "coordinator", "representative", "executive", "director", "teacher",
    "مطور", "مهندس", "مدير", "محلل", "اخصائي", "مصمم", "مستشار", "محاسب", "موظف",
    "مسؤول", "منسق", "مندوب", "متدرب", "معلم", "رئيس",
)
_DEGREE_WORDS = (
    "bachelor", "master", "phd", "diploma", "b.sc", "bsc", "m.sc", "msc", "mba", "degree",
    "b.a.", "m.a.", "b.eng", "m.eng",
    "بكالوريوس", "ماجستير", "دكتوراه", "دبلوم",
)


def extract_profile_heuristic(cv_text: str) -> CandidateProfile:
    """Best-effort CandidateProfile using regexes and the shared vocabulary."""
    sections = detect_sections(cv_text)
    by_name: dict[str, str] = {}
    for s in sections:
        by_name[s.name] = (by_name.get(s.name, "") + "\n" + s.text).strip()

    experience = by_name.get("experience", "")
    languages_text = by_name.get("languages") or cv_text

    return CandidateProfile(
        full_name=_guess_name(cv_text),
        email=_first(_EMAIL, cv_text),
        phone=_guess_phone(cv_text),
        location=_guess_location(cv_text),
        years_of_experience=_guess_years(cv_text, experience),
        skills=find_skills(cv_text),
        languages=find_languages(languages_text),
        job_titles=[
            _title_part(line) for line in _lines_with(experience, _TITLE_WORDS, limit=5)
        ],
        education=[
            {"degree": line, "field": None, "institution": None, "year": _last_year(line)}
            for line in _lines_with(by_name.get("education", ""), _DEGREE_WORDS, limit=4)
        ],
    )


def _first(pattern: re.Pattern[str], text: str) -> str | None:
    match = pattern.search(text)
    return match.group(0).strip() if match else None


def _guess_name(text: str) -> str | None:
    # 🧸 The first short line without an "@" or digits is usually the name.
    for line in text.splitlines()[:5]:
        line = line.strip()
        if line and "@" not in line and not re.search(r"\d", line) and len(line.split()) <= 5:
            return line
    return None


def _guess_phone(text: str) -> str | None:
    for match in _PHONE.finditer(text):
        candidate = match.group(0).strip()
        digits = re.sub(r"\D", "", candidate)
        # 🧸 Skip things like "2019 - 2023" that only look like phone numbers.
        if 8 <= len(digits) <= 15 and not _DATE_RANGE.search(candidate):
            return candidate
    return None


def _guess_location(text: str) -> str | None:
    for line in text.splitlines()[:15]:
        match = _LOCATION_LABEL.match(normalize(line.strip()))
        if match:
            # Return the original spelling, not the normalized one.
            return line.split(":", 1)[-1].split("：", 1)[-1].strip() or None
    return None


def _guess_years(cv_text: str, experience_text: str) -> float | None:
    # 🧸 Way 1: the CV tells us directly. Take the biggest number mentioned.
    stated = [float(m.group(1)) for m in _YEARS_STATED.finditer(normalize(cv_text))]
    stated = [y for y in stated if 0 < y <= 50]
    if stated:
        return max(stated)

    # 🧸 Way 2: add up the job periods. Overlapping jobs are merged so we don't
    # count the same year twice.
    this_year = date.today().year
    periods: list[tuple[int, int]] = []
    for m in _DATE_RANGE.finditer(normalize(experience_text or "")):
        start = int(m.group(1))
        end = int(m.group(2)) if m.group(2).isdigit() else this_year
        if start <= end <= this_year:
            periods.append((start, end))
    if not periods:
        return None
    periods.sort()
    total = 0
    cur_start, cur_end = periods[0]
    for start, end in periods[1:]:
        if start <= cur_end:
            cur_end = max(cur_end, end)
        else:
            total += cur_end - cur_start
            cur_start, cur_end = start, end
    total += cur_end - cur_start
    return float(total) if total > 0 else None


def _lines_with(text: str, words: tuple[str, ...], limit: int) -> list[str]:
    """Non-bullet lines containing one of `words` (bullets describe duties, not titles)."""
    out: list[str] = []
    for line in text.splitlines():
        clean = line.strip()
        if not clean or clean[0] in "•-*▪●" or len(clean) > 120:
            continue
        if any(w in normalize(clean) for w in words) and clean not in out:
            out.append(clean)
        if len(out) >= limit:
            break
    return out


def _title_part(line: str) -> str:
    # 🧸 "Backend Developer | Acme | 2020 – Present" → "Backend Developer"
    return re.split(r"\s[|–—-]\s|\s+at\s+|\s+في\s+شركه\s+", line)[0].strip()


def _last_year(line: str) -> str | None:
    years = re.findall(r"(?:19|20)\d{2}", line)
    return years[-1] if years else None
