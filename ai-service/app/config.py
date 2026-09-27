"""Settings loaded from environment / .env via pydantic-settings."""

from functools import lru_cache
from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    # "development" | "production". Production refuses to start without SERVICE_API_KEY.
    environment: str = "development"

    # Shared secret the web backend sends as X-Api-Key. Empty = no auth (development only).
    service_api_key: str = ""

    # LLM (any OpenAI-compatible endpoint)
    llm_base_url: str = "https://api.openai.com/v1"
    llm_api_key: str = ""
    llm_model: str = ""
    llm_timeout_seconds: float = 60.0

    # Models
    embedding_model: str = "BAAI/bge-m3"
    reranker_model: str = "BAAI/bge-reranker-v2-m3"

    # Storage
    data_dir: Path = Path("./data")
    chroma_dir: Path = Path("./data/chroma")
    bm25_dir: Path = Path("./data/bm25")
    profiles_dir: Path = Path("./data/profiles")

    # Search pipeline
    retrieval_candidates: int = 50  # chunks fetched from each retriever (dense, BM25)
    rerank_candidates: int = 30  # fused chunks sent to the cross-encoder
    use_reranker: bool = True
    use_llm_query_parser: bool = False  # rule-based parsing is fast and free; LLM is optional

    # Upload limits
    max_upload_mb: int = 10

    # Logging
    log_level: str = "INFO"


@lru_cache
def get_settings() -> Settings:
    return Settings()
