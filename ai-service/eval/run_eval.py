"""🔒 MANUAL — compare retrieval variants (dense, BM25, hybrid, +rerank).

🧸 EXPLAIN LIKE I'M 5
---------------------
We built a search engine with many parts. Does each part actually HELP?
To find out we race different versions against the same exam (a list of questions
with known right answers) and compare their grades (see metrics.py):

    dense          – only the meaning-based scout
    bm25           – only the keyword scout
    hybrid         – both scouts merged with RRF
    hybrid+rerank  – both scouts + the careful judge
    full           – everything, including the hard rules (years / skills / languages)

If "hybrid" doesn't beat "dense" and "bm25", fusion isn't pulling its weight. If
"+rerank" isn't better, the slow judge isn't worth the time. Numbers, not feelings! 📊

Usage (from ai-service/):
    python scripts/generate_sample_cvs.py                  # makes sample_cvs/ + dataset
    python -m eval.run_eval --ingest sample_cvs            # index them, then evaluate
    python -m eval.run_eval --k 3 --variants dense,hybrid  # re-run without re-indexing
"""

import argparse
import json
import logging
import time
from collections.abc import Callable
from pathlib import Path

from app.chunking.section_chunker import chunk_sections, detect_sections
from app.config import get_settings
from app.extraction.extractor import extract_profile
from app.llm.client import get_llm_client
from app.models import Chunk
from app.parsing.parser import parse_document
from app.retrieval.aggregate import aggregate_candidates
from app.retrieval.bm25_index import BM25Index
from app.retrieval.fusion import reciprocal_rank_fusion
from app.retrieval.pipeline import RRF_K, SearchPipeline
from app.retrieval.reranker import get_reranker
from eval.metrics import ndcg_at_k, precision_at_k, recall_at_k, reciprocal_rank

logger = logging.getLogger(__name__)

DEFAULT_DATASET = Path(__file__).parent / "datasets" / "sample_queries.json"
ALL_VARIANTS = ["dense", "bm25", "hybrid", "hybrid+rerank", "full"]


def load_dataset(path: Path) -> list[dict]:
    """Load labeled queries (query + relevant candidate ids) from eval/datasets.

    File format:
        {"workspace_id": "eval",
         "queries": [{"query": "python backend", "relevant": {"cv_01": 2, "cv_07": 1}}]}

    `relevant` maps candidate_id → grade (2 = perfect match, 1 = partial match).
    Returns one dict per query with keys: workspace_id, query, relevant.
    """
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    workspace_id = raw.get("workspace_id", "eval")
    dataset = []
    for item in raw["queries"]:
        relevant = item["relevant"]
        if isinstance(relevant, list):  # 🧸 plain list = every answer is equally right
            relevant = {cid: 1.0 for cid in relevant}
        dataset.append(
            {
                "workspace_id": item.get("workspace_id", workspace_id),
                "query": item["query"],
                "relevant": {cid: float(g) for cid, g in relevant.items()},
            }
        )
    return dataset


# ---------------------------------------------------------------- variants


def _pipeline(with_reranker: bool) -> SearchPipeline:
    from app.embeddings.embedder import get_embedder
    from app.storage.profile_store import get_profile_store
    from app.storage.vector_store import get_vector_store

    settings = get_settings()
    return SearchPipeline(
        embedder=get_embedder(),
        store=get_vector_store(),
        profiles=get_profile_store(),
        reranker=get_reranker() if with_reranker else None,
        retrieval_candidates=settings.retrieval_candidates,
        rerank_candidates=settings.rerank_candidates,
    )


def _ids(scored: list[tuple[Chunk, float]]) -> list[str]:
    """🧸 Pieces → people, keep only the people's names in order."""
    return [cid for cid, _, _ in aggregate_candidates(scored)]


def _dense(p: SearchPipeline, ws: str, query: str) -> list[str]:
    n = p.retrieval_candidates
    return _ids(p.store.query(ws, p.embedder.embed_query(query), n))


def _bm25(p: SearchPipeline, ws: str, query: str) -> list[str]:
    hits = BM25Index(ws).search(query, p.retrieval_candidates)
    chunks = {c.chunk_id: c for c in p.store.get_chunks(ws, [cid for cid, _ in hits])}
    return _ids([(chunks[cid], s) for cid, s in hits if cid in chunks])


def _hybrid(p: SearchPipeline, ws: str, query: str, rerank: bool) -> list[str]:
    n = p.retrieval_candidates
    dense = p.store.query(ws, p.embedder.embed_query(query), n)
    keyword = BM25Index(ws).search(query, n)
    fused = reciprocal_rank_fusion(
        [[c.chunk_id for c, _ in dense], [cid for cid, _ in keyword]], k=RRF_K
    )[: p.rerank_candidates]
    chunks = p._load_chunks(ws, [cid for cid, _ in fused], dense)
    if rerank and p.reranker is not None:
        return _ids(p.reranker.rerank(query, chunks, len(chunks)))
    score = dict(fused)
    return _ids([(c, score[c.chunk_id]) for c in chunks])


def _full(p: SearchPipeline, ws: str, query: str) -> list[str]:
    return [m.candidate_id for m in p.rank(ws, query)]


def _variant_fn(name: str) -> Callable[[str, str], list[str]]:
    needs_reranker = name in ("hybrid+rerank", "full") and get_settings().use_reranker
    p = _pipeline(with_reranker=needs_reranker)
    return {
        "dense": lambda ws, q: _dense(p, ws, q),
        "bm25": lambda ws, q: _bm25(p, ws, q),
        "hybrid": lambda ws, q: _hybrid(p, ws, q, rerank=False),
        "hybrid+rerank": lambda ws, q: _hybrid(p, ws, q, rerank=True),
        "full": lambda ws, q: _full(p, ws, q),
    }[name]


def evaluate_variant(name: str, dataset: list[dict], k: int) -> dict[str, float]:
    """Run one retrieval variant over the dataset and return its metrics."""
    run = _variant_fn(name)
    totals = {"P@k": 0.0, "R@k": 0.0, "nDCG@k": 0.0, "MRR": 0.0, "ms/query": 0.0}
    for item in dataset:
        start = time.perf_counter()
        ranked = run(item["workspace_id"], item["query"])
        totals["ms/query"] += (time.perf_counter() - start) * 1000

        relevant = {cid for cid, grade in item["relevant"].items() if grade > 0}
        totals["P@k"] += precision_at_k(ranked, relevant, k)
        totals["R@k"] += recall_at_k(ranked, relevant, k)
        totals["nDCG@k"] += ndcg_at_k(ranked, item["relevant"], k)
        totals["MRR"] += reciprocal_rank(ranked, relevant)
    # 🧸 Average over all questions = the final grade.
    return {metric: value / max(len(dataset), 1) for metric, value in totals.items()}


# ---------------------------------------------------------------- ingestion


def ingest_folder(folder: Path, workspace_id: str) -> int:
    """Index every PDF/DOCX in `folder` into `workspace_id` (candidate_id = file stem)."""
    from app.embeddings.embedder import get_embedder
    from app.storage.profile_store import get_profile_store
    from app.storage.vector_store import get_vector_store

    embedder, store, profiles = get_embedder(), get_vector_store(), get_profile_store()
    llm = get_llm_client()
    bm25 = BM25Index(workspace_id)
    files = sorted(p for p in folder.iterdir() if p.suffix.lower() in (".pdf", ".docx"))
    for path in files:
        candidate_id = path.stem
        parsed = parse_document(path.read_bytes(), path.name)
        chunks = chunk_sections(detect_sections(parsed.text), candidate_id, workspace_id)
        profile = extract_profile(parsed.text, llm)
        store.delete_candidate(workspace_id, candidate_id)
        bm25.delete_candidate(candidate_id)
        store.add_chunks(chunks, embedder.embed_documents([c.text for c in chunks]))
        bm25.add(chunks)
        profiles.put(workspace_id, candidate_id, profile)
        print(f"  indexed {path.name}: {len(chunks)} chunks")
    return len(files)


def main() -> None:
    """Evaluate all variants and print a comparison table."""
    parser = argparse.ArgumentParser(description="Compare TalentLens retrieval variants")
    parser.add_argument("--dataset", type=Path, default=DEFAULT_DATASET)
    parser.add_argument("--k", type=int, default=5)
    parser.add_argument("--variants", default=",".join(ALL_VARIANTS))
    parser.add_argument("--ingest", type=Path, help="folder of CVs to index first")
    args = parser.parse_args()

    logging.basicConfig(level=logging.WARNING)
    dataset = load_dataset(args.dataset)
    if not dataset:
        raise SystemExit("Dataset has no queries")

    if args.ingest:
        workspace_id = dataset[0]["workspace_id"]
        print(f"Indexing {args.ingest} into workspace '{workspace_id}' …")
        print(f"Indexed {ingest_folder(args.ingest, workspace_id)} CVs\n")

    variants = [v.strip() for v in args.variants.split(",") if v.strip()]
    unknown = set(variants) - set(ALL_VARIANTS)
    if unknown:
        raise SystemExit(f"Unknown variants: {', '.join(sorted(unknown))}")

    metrics = ["P@k", "R@k", "nDCG@k", "MRR", "ms/query"]
    print(f"{len(dataset)} queries, k={args.k}\n")
    print(f"{'variant':<15}" + "".join(f"{m:>10}" for m in metrics))
    print("-" * (15 + 10 * len(metrics)))
    for name in variants:
        result = evaluate_variant(name, dataset, args.k)
        cells = "".join(
            f"{result[m]:>10.1f}" if m == "ms/query" else f"{result[m]:>10.3f}" for m in metrics
        )
        print(f"{name:<15}{cells}")


if __name__ == "__main__":
    main()
