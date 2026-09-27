"""Service-to-service authentication.

Only the TalentLens web backend may call the AI service. It sends a shared secret in the
`X-Api-Key` header. The backend is responsible for user authorization (who may search which
workspace); this check only proves the caller is the backend.
"""

import hmac
from typing import Annotated

from fastapi import Depends, Header

from app.config import Settings, get_settings
from app.errors import TalentLensError


class UnauthorizedError(TalentLensError):
    status_code = 401


def require_service_key(
    settings: Annotated[Settings, Depends(get_settings)],
    x_api_key: Annotated[str | None, Header()] = None,
) -> None:
    """Reject requests without the configured service key.

    With no SERVICE_API_KEY configured (local development) every request is accepted;
    the app refuses to start that way in production (see app.main).
    """
    expected = settings.service_api_key
    if not expected:
        return
    # Constant-time comparison so the key can't be guessed byte by byte from timings.
    if not x_api_key or not hmac.compare_digest(x_api_key.encode(), expected.encode()):
        raise UnauthorizedError("Missing or invalid service API key")
