import pytest

from app.errors import EmptyDocumentError, UnsupportedFileError
from app.parsing.parser import normalize_text, parse_document


def test_docx_keeps_paragraph_order_and_line_breaks(make_docx):
    data = make_docx(["Experience", "Backend Developer at Acme", "Skills", "Python, SQL"])
    doc = parse_document(data, "cv.docx")

    assert doc.file_type == "docx"
    assert doc.text.split("\n") == [
        "Experience",
        "Backend Developer at Acme",
        "Skills",
        "Python, SQL",
    ]
    assert doc.char_count == len(doc.text)
    assert doc.page_count >= 1


def test_docx_includes_tables_in_document_order(make_docx):
    data = make_docx(
        ["Skills"],
        table=[["Language", "Level"], ["Python", "Advanced"]],
    )
    text = parse_document(data, "cv.docx").text

    assert "Language | Level" in text
    assert "Python | Advanced" in text
    assert text.index("Skills") < text.index("Python | Advanced")


def test_docx_includes_header_text(make_docx):
    data = make_docx(["Summary"], header="Sami Haddad — sami@example.com")
    text = parse_document(data, "cv.docx").text

    assert text.startswith("Sami Haddad")


def test_docx_arabic_text(make_docx):
    data = make_docx(["الخبرات", "مطور برمجيات في شركة وهمية", "المهارات", "بايثون"])
    text = parse_document(data, "سيرة.docx").text

    assert "الخبرات" in text
    assert "مطور برمجيات في شركة وهمية" in text


def test_pdf_pages_and_text(make_pdf):
    data = make_pdf(["Experience: Data Analyst", "Education: BSc Computer Science"])
    doc = parse_document(data, "CV.PDF")

    assert doc.file_type == "pdf"
    assert doc.page_count == 2
    assert "Data Analyst" in doc.text
    assert "BSc Computer Science" in doc.text


def test_pdf_without_text_raises_empty(make_pdf):
    with pytest.raises(EmptyDocumentError):
        parse_document(make_pdf([""]), "scanned.pdf")


@pytest.mark.parametrize("filename", ["cv.txt", "cv.doc", "cv"])
def test_unsupported_extension(filename):
    with pytest.raises(UnsupportedFileError):
        parse_document(b"hello", filename)


def test_content_must_match_extension(make_docx):
    with pytest.raises(UnsupportedFileError):
        parse_document(make_docx(["x"]), "cv.pdf")
    with pytest.raises(UnsupportedFileError):
        parse_document(b"%PDF-1.7 not really", "cv.docx")


def test_normalize_text():
    raw = "Line one  \r\nLine two\n\n\n\n\nLine three\n"
    assert normalize_text(raw) == "Line one\nLine two\n\nLine three"
