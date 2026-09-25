from types import SimpleNamespace

import httpx
import pytest
from openai import APIConnectionError

from app.config import Settings
from app.errors import LLMError
from app.llm.client import LLMClient


class FakeCompletions:
    def __init__(self, content=None, stream_parts=None, error=None):
        self.content = content
        self.stream_parts = stream_parts or []
        self.error = error
        self.last_kwargs: dict = {}

    def create(self, **kwargs):
        self.last_kwargs = kwargs
        if self.error:
            raise self.error
        if kwargs.get("stream"):
            return [
                SimpleNamespace(choices=[SimpleNamespace(delta=SimpleNamespace(content=p))])
                for p in self.stream_parts
            ]
        return SimpleNamespace(
            choices=[SimpleNamespace(message=SimpleNamespace(content=self.content))]
        )


def _client(completions: FakeCompletions, model: str = "test-model") -> LLMClient:
    fake = SimpleNamespace(chat=SimpleNamespace(completions=completions))
    return LLMClient(Settings(llm_model=model, _env_file=None), client=fake)


MESSAGES = [{"role": "user", "content": "hi"}]


def test_chat_returns_content_and_sends_model():
    completions = FakeCompletions(content="hello")

    assert _client(completions).chat(MESSAGES, max_tokens=50) == "hello"
    assert completions.last_kwargs["model"] == "test-model"
    assert completions.last_kwargs["max_tokens"] == 50
    assert "response_format" not in completions.last_kwargs


def test_chat_json_parses_and_strips_code_fence():
    completions = FakeCompletions(content='```json\n{"skills": ["Python"]}\n```')

    assert _client(completions).chat_json(MESSAGES) == {"skills": ["Python"]}
    assert completions.last_kwargs["response_format"] == {"type": "json_object"}


@pytest.mark.parametrize("content", ["not json", "[1, 2]"])
def test_chat_json_rejects_bad_json(content):
    with pytest.raises(LLMError):
        _client(FakeCompletions(content=content)).chat_json(MESSAGES)


def test_missing_model_raises():
    with pytest.raises(LLMError, match="LLM_MODEL"):
        _client(FakeCompletions(content="x"), model="").chat(MESSAGES)


def test_empty_response_raises():
    with pytest.raises(LLMError):
        _client(FakeCompletions(content=None)).chat(MESSAGES)


def test_provider_error_is_wrapped():
    error = APIConnectionError(request=httpx.Request("POST", "http://x"))
    with pytest.raises(LLMError):
        _client(FakeCompletions(error=error)).chat(MESSAGES)


def test_stream_chat_yields_deltas():
    completions = FakeCompletions(stream_parts=["Hel", None, "lo"])

    assert "".join(_client(completions).stream_chat(MESSAGES)) == "Hello"
