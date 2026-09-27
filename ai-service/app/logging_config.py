"""Logging setup for the AI service."""

import logging
import sys

from app.observability import RequestIdFilter


def setup_logging(level: str = "INFO") -> None:
    """Configure root logging once: timestamped lines to stdout."""
    root = logging.getLogger()
    if root.handlers:
        root.setLevel(level.upper())
        return
    handler = logging.StreamHandler(sys.stdout)
    handler.addFilter(RequestIdFilter())
    handler.setFormatter(
        logging.Formatter("%(asctime)s %(levelname)-7s [%(request_id)s] %(name)s: %(message)s")
    )
    root.addHandler(handler)
    root.setLevel(level.upper())
