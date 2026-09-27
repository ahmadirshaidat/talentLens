"""Request correlation IDs and access logging.

The web backend forwards its trace id as `X-Request-ID`; we reuse it (or create one), attach it
to every log line of the request, and echo it in the response. Only method, path, status and
latency are logged — never request bodies, which contain CVs and queries.
"""

import logging
import re
import time
import uuid
from contextvars import ContextVar

from starlette.middleware.base import BaseHTTPMiddleware, RequestResponseEndpoint
from starlette.requests import Request
from starlette.responses import Response

REQUEST_ID_HEADER = "X-Request-ID"
_VALID_ID = re.compile(r"^[A-Za-z0-9._:-]{1,64}$")

request_id_var: ContextVar[str] = ContextVar("request_id", default="-")
logger = logging.getLogger("app.access")


class RequestIdFilter(logging.Filter):
    """Adds `request_id` to every log record (used by the log format)."""

    def filter(self, record: logging.LogRecord) -> bool:
        record.request_id = request_id_var.get()
        return True


class RequestContextMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: Request, call_next: RequestResponseEndpoint) -> Response:
        incoming = request.headers.get(REQUEST_ID_HEADER, "")
        request_id = incoming if _VALID_ID.fullmatch(incoming) else uuid.uuid4().hex
        token = request_id_var.set(request_id)
        start = time.perf_counter()
        status = 500
        try:
            response = await call_next(request)
            status = response.status_code
            response.headers[REQUEST_ID_HEADER] = request_id
            return response
        finally:
            elapsed_ms = (time.perf_counter() - start) * 1000
            if request.url.path != "/health":  # health probes would drown the log
                logger.info(
                    "%s %s -> %d in %.0f ms", request.method, request.url.path, status, elapsed_ms
                )
            request_id_var.reset(token)
