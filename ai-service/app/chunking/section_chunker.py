"""🔒 MANUAL — CV section detection and section-based chunking (Arabic + English headings)."""

from app.models import Chunk, CVSection


def detect_sections(text: str) -> list[CVSection]:
    """Split raw CV text into sections by detecting headings.

    Args:
        text: Full CV text from the parser (line breaks preserved).

    Returns:
        Sections in document order. `name` is one of "experience", "skills",
        "education", "projects", "summary", "other".
    """
    raise NotImplementedError("MANUAL: detect CV sections (AR + EN headings)")


def chunk_sections(sections: list[CVSection], candidate_id: str, workspace_id: str) -> list[Chunk]:
    """Turn detected sections into chunks ready for embedding and BM25.

    Args:
        sections: Output of `detect_sections`.
        candidate_id: Owner candidate.
        workspace_id: Tenant scope.

    Returns:
        Chunks with unique `chunk_id`s, each tagged with its section name.
    """
    raise NotImplementedError("MANUAL: chunk sections into Chunk objects")
