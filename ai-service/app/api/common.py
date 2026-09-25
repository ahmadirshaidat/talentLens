"""Shared request types for the routers."""

from typing import Annotated

from pydantic import StringConstraints

# IDs end up in collection names and file paths (per-workspace BM25 indexes),
# so restrict them to a safe charset. GUIDs from the web app fit.
SafeId = Annotated[str, StringConstraints(pattern=r"^[A-Za-z0-9_-]{1,64}$")]
