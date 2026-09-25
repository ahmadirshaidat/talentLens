"""Provider-agnostic LLM client for any OpenAI-compatible API (OpenAI, Groq, Ollama, vLLM…)."""

import json
import logging
from collections.abc import Iterator
from functools import lru_cache
from typing import Any

from openai import OpenAI, OpenAIError

from app.config import Settings, get_settings
from app.errors import LLMError

logger = logging.getLogger(__name__)

Message = dict[str, str]  # {"role": "system" | "user" | "assistant", "content": "..."}


class LLMClient:
    def __init__(self, settings: Settings, client: Any | None = None) -> None:
        self.model = settings.llm_model
        self._client = client or OpenAI(
            base_url=settings.llm_base_url,
            # Local servers (e.g. Ollama) accept any key but the SDK requires one.
            api_key=settings.llm_api_key or "not-needed",
            timeout=settings.llm_timeout_seconds,
            max_retries=2,
        )

    def chat(
        self,
        messages: list[Message],
        *,
        temperature: float = 0.0,
        max_tokens: int | None = None,
        json_mode: bool = False,
    ) -> str:
        """Single completion; returns the assistant message text.

        json_mode asks the provider for a JSON object response. Not every
        OpenAI-compatible provider supports it.
        """
        kwargs = self._request_kwargs(messages, temperature, max_tokens)
        if json_mode:
            kwargs["response_format"] = {"type": "json_object"}
        try:
            response = self._client.chat.completions.create(**kwargs)
        except OpenAIError as exc:
            raise LLMError(f"LLM request failed: {exc}") from exc
        content = response.choices[0].message.content
        if content is None:
            raise LLMError("LLM returned an empty response")
        return content

    def chat_json(self, messages: list[Message], **kwargs: Any) -> dict[str, Any]:
        """Like chat(json_mode=True) but parses the result. Raises LLMError on invalid JSON."""
        text = self.chat(messages, json_mode=True, **kwargs)
        try:
            data = json.loads(_strip_code_fence(text))
        except json.JSONDecodeError as exc:
            raise LLMError(f"LLM returned invalid JSON: {exc}") from exc
        if not isinstance(data, dict):
            raise LLMError("LLM returned JSON that is not an object")
        return data

    def stream_chat(
        self,
        messages: list[Message],
        *,
        temperature: float = 0.0,
        max_tokens: int | None = None,
    ) -> Iterator[str]:
        """Yield text deltas as they arrive."""
        kwargs = self._request_kwargs(messages, temperature, max_tokens)
        try:
            for event in self._client.chat.completions.create(stream=True, **kwargs):
                if event.choices and event.choices[0].delta.content:
                    yield event.choices[0].delta.content
        except OpenAIError as exc:
            raise LLMError(f"LLM stream failed: {exc}") from exc

    def _request_kwargs(
        self, messages: list[Message], temperature: float, max_tokens: int | None
    ) -> dict[str, Any]:
        if not self.model:
            raise LLMError("LLM_MODEL is not configured")
        kwargs: dict[str, Any] = {
            "model": self.model,
            "messages": messages,
            "temperature": temperature,
        }
        if max_tokens is not None:
            kwargs["max_tokens"] = max_tokens
        return kwargs


def _strip_code_fence(text: str) -> str:
    """Some models wrap JSON in ```json ... ``` even in JSON mode."""
    text = text.strip()
    if text.startswith("```"):
        text = text.split("\n", 1)[1] if "\n" in text else ""
        text = text.rsplit("```", 1)[0]
    return text.strip()


@lru_cache
def get_llm_client() -> LLMClient:
    return LLMClient(get_settings())
