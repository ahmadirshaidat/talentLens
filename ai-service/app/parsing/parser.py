"""PDF (PyMuPDF) + DOCX (python-docx, incl. tables) -> ParsedDocument.

Line breaks are preserved because section detection relies on headings
sitting on their own lines.
"""

import io
import logging
import re
import zipfile
from pathlib import PurePath

import docx
import pymupdf
from docx.table import Table
from docx.text.paragraph import Paragraph

from app.errors import EmptyDocumentError, UnsupportedFileError
from app.models import ParsedDocument

logger = logging.getLogger(__name__)

_PDF_MAGIC = b"%PDF"
_ZIP_MAGIC = b"PK\x03\x04"


def parse_document(data: bytes, filename: str) -> ParsedDocument:
    """Parse an uploaded CV into plain text.

    Args:
        data: Raw file bytes.
        filename: Original filename; the extension selects the parser.

    Raises:
        UnsupportedFileError: Unknown extension or content doesn't match it.
        EmptyDocumentError: No extractable text (e.g. scanned PDF).
    """
    ext = PurePath(filename).suffix.lower()
    if ext == ".pdf":
        if not data.startswith(_PDF_MAGIC):
            raise UnsupportedFileError(f"{filename} is not a valid PDF")
        text, page_count = _parse_pdf(data)
        file_type = "pdf"
    elif ext == ".docx":
        if not data.startswith(_ZIP_MAGIC):
            raise UnsupportedFileError(f"{filename} is not a valid DOCX")
        text, page_count = _parse_docx(data)
        file_type = "docx"
    else:
        raise UnsupportedFileError(f"Unsupported file type '{ext or '?'}'; use PDF or DOCX")

    text = normalize_text(text)
    if not text:
        raise EmptyDocumentError(f"No text could be extracted from {filename}")

    logger.info("Parsed %s: %d pages, %d chars", filename, page_count, len(text))
    return ParsedDocument(
        filename=filename,
        file_type=file_type,
        text=text,
        page_count=page_count,
        char_count=len(text),
    )


def normalize_text(text: str) -> str:
    """Unify newlines, strip trailing spaces, collapse 3+ blank lines to one blank line."""
    text = text.replace("\r\n", "\n").replace("\r", "\n").replace(" ", " ")
    lines = [line.rstrip() for line in text.split("\n")]
    text = "\n".join(lines)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip()


def _parse_pdf(data: bytes) -> tuple[str, int]:
    try:
        doc = pymupdf.open(stream=data, filetype="pdf")
    except Exception as exc:  # PyMuPDF raises several exception types for bad files
        raise UnsupportedFileError(f"Could not open PDF: {exc}") from exc
    with doc:
        pages = [page.get_text("text") for page in doc]
        return "\n\n".join(pages), doc.page_count


def _parse_docx(data: bytes) -> tuple[str, int]:
    try:
        document = docx.Document(io.BytesIO(data))
    except Exception as exc:
        raise UnsupportedFileError(f"Could not open DOCX: {exc}") from exc

    parts: list[str] = []

    # Headers often hold the candidate's name and contact details.
    seen_headers: set[str] = set()
    for section in document.sections:
        header_text = _blocks_to_text(section.header.iter_inner_content())
        if header_text and header_text not in seen_headers:
            seen_headers.add(header_text)
            parts.append(header_text)

    # Body paragraphs and tables in document order.
    parts.append(_blocks_to_text(document.iter_inner_content()))
    return "\n\n".join(p for p in parts if p), _docx_page_count(data)


def _blocks_to_text(blocks) -> str:
    lines: list[str] = []
    for block in blocks:
        if isinstance(block, Paragraph):
            lines.append(block.text)
        elif isinstance(block, Table):
            lines.append(_table_to_text(block))
    return "\n".join(lines).strip()


def _table_to_text(table: Table) -> str:
    """One line per row, cells joined by ' | '. Merged cells repeat in python-docx, so dedupe."""
    rows: list[str] = []
    for row in table.rows:
        cells: list[str] = []
        seen: set[int] = set()
        for cell in row.cells:
            if id(cell._tc) in seen:
                continue
            seen.add(id(cell._tc))
            cell_text = _blocks_to_text(cell.iter_inner_content()).replace("\n", " ").strip()
            if cell_text:
                cells.append(cell_text)
        if cells:
            rows.append(" | ".join(cells))
    return "\n".join(rows)


def _docx_page_count(data: bytes) -> int:
    """Read <Pages> from docProps/app.xml (written by Word); fall back to 1."""
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as zf:
            app_xml = zf.read("docProps/app.xml").decode("utf-8", errors="ignore")
        match = re.search(r"<Pages>(\d+)</Pages>", app_xml)
        if match:
            return max(1, int(match.group(1)))
    except (KeyError, zipfile.BadZipFile):
        pass
    return 1
