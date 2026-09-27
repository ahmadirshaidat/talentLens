"""Extracted CandidateProfiles on disk: one JSON file per workspace.

The search pipeline needs structured facts (years of experience, languages) to
apply query filters; chunks alone don't carry them.
"""

import json
import logging
import re
import threading
from functools import lru_cache
from pathlib import Path

from app.config import get_settings
from app.models import CandidateProfile

logger = logging.getLogger(__name__)

_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,64}$")


class ProfileStore:
    def __init__(self, directory: Path) -> None:
        directory.mkdir(parents=True, exist_ok=True)
        self._dir = directory
        self._lock = threading.Lock()

    def _path(self, workspace_id: str) -> Path:
        if not _ID_PATTERN.fullmatch(workspace_id):
            raise ValueError(f"Invalid workspace_id: {workspace_id!r}")
        return self._dir / f"{workspace_id}.json"

    def _load(self, workspace_id: str) -> dict[str, dict]:
        path = self._path(workspace_id)
        if not path.exists():
            return {}
        return json.loads(path.read_text(encoding="utf-8"))

    def _save(self, workspace_id: str, data: dict[str, dict]) -> None:
        path = self._path(workspace_id)
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        tmp.replace(path)

    def put(self, workspace_id: str, candidate_id: str, profile: CandidateProfile) -> None:
        with self._lock:
            data = self._load(workspace_id)
            data[candidate_id] = profile.model_dump()
            self._save(workspace_id, data)

    def get(self, workspace_id: str, candidate_id: str) -> CandidateProfile | None:
        raw = self._load(workspace_id).get(candidate_id)
        return CandidateProfile.model_validate(raw) if raw else None

    def get_all(self, workspace_id: str) -> dict[str, CandidateProfile]:
        return {
            cid: CandidateProfile.model_validate(raw)
            for cid, raw in self._load(workspace_id).items()
        }

    def delete(self, workspace_id: str, candidate_id: str) -> None:
        with self._lock:
            data = self._load(workspace_id)
            if data.pop(candidate_id, None) is not None:
                self._save(workspace_id, data)


@lru_cache
def get_profile_store() -> ProfileStore:
    return ProfileStore(get_settings().profiles_dir)
