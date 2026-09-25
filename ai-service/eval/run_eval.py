"""🔒 MANUAL — compare retrieval variants (dense, BM25, hybrid, +rerank)."""

from pathlib import Path


def load_dataset(path: Path) -> list[dict]:
    """Load labeled queries (query + relevant candidate ids) from eval/datasets."""
    raise NotImplementedError("MANUAL: load eval dataset")


def evaluate_variant(name: str, dataset: list[dict], k: int) -> dict[str, float]:
    """Run one retrieval variant over the dataset and return its metrics."""
    raise NotImplementedError("MANUAL: evaluate one retrieval variant")


def main() -> None:
    """Evaluate all variants and print a comparison table."""
    raise NotImplementedError("MANUAL: eval entry point")


if __name__ == "__main__":
    main()
