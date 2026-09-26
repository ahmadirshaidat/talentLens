import pytest

from app.chunking.section_chunker import (
    _match_heading,
    _normalize,
    chunk_sections,
    detect_sections,
)
from app.models import CVSection


def test_normalize():
    assert _normalize("  الخبرة العمليّة: ") == "الخبره العمليه"
    assert _normalize("• WORK   Experience:") == "work experience"
    assert _normalize("1. Education") == "education"
    assert _normalize("المؤهلات الأكاديمية") == "المؤهلات الاكاديميه"
    assert _normalize("") == ""


@pytest.mark.parametrize("line, expected", [
    ("Skills", "skills"),
    ("WORK EXPERIENCE:", "experience"),
    ("  Education  ", "education"),
    ("المهارات", "skills"),
    ("الخبرات العملية:", "experience"),
    ("الخبره العمليه", "experience"),
    ("• Technical Skills", "skills"),
    ("I have experience with SQL Server", None),
    ("Built REST APIs with ASP.NET Core", None),
    ("", None),
])
def test_match_heading(line, expected):
    assert _match_heading(line) == expected


def test_detect_sections():
    text = "Sara Khaled\nsara@example.com\nSkills\nPython, SQL\nالخبرات العملية\nمطورة في شركة"
    sections = detect_sections(text)
    assert [s.name for s in sections] == ["header", "skills", "experience"]
    assert sections[1].text == "Python, SQL"


def test_chunk_sections_splits_long_section():
    sections = [
        CVSection(name="skills", text="Python, SQL"),
        CVSection(name="experience", text="aaaa\nbbbb\ncccc"),
    ]
    chunks = chunk_sections(sections, candidate_id="c1", workspace_id="w1", max_chars=9)
    assert [c.section for c in chunks] == ["skills", "experience", "experience"]
    assert chunks[1].text == "aaaa\nbbbb"
    assert chunks[2].text == "cccc"
    assert len({c.chunk_id for c in chunks}) == 3