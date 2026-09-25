"""Domain errors; mapped to HTTP responses in app.main."""


class TalentLensError(Exception):
    """Base class for expected, user-facing errors."""

    status_code: int = 400


class UnsupportedFileError(TalentLensError):
    """File is not a PDF/DOCX, or its content doesn't match its extension."""

    status_code = 415


class EmptyDocumentError(TalentLensError):
    """No extractable text (e.g. a scanned PDF without OCR)."""

    status_code = 422


class FileTooLargeError(TalentLensError):
    status_code = 413


class LLMError(TalentLensError):
    """The LLM provider failed or returned an unusable response."""

    status_code = 502
