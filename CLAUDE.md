# TalentLens — Smart CV Search for HR Recruiters

Recruiters upload CVs (PDF/DOCX, Arabic + English) and search them in natural language.
The system returns ranked **candidates** (not chunks), each with a score, a reason, and quoted evidence from the CV.

This is a portfolio project. The developer (Ahmad) is writing the core logic himself to learn it.
**Your job is scaffolding and boilerplate only.** Read the "Ownership" section before touching any file.

---

## Ownership — READ FIRST

### 🔒 MANUAL (Ahmad writes these — do NOT implement)
For every file/function below, create only a **stub**: correct signature, type hints, a docstring
describing inputs/outputs, and a body of:

```python
raise NotImplementedError("MANUAL: <short description>")
```
(C#: `throw new NotImplementedException("MANUAL: ...");`)

Never fill in a MANUAL stub, even if a request says "finish the project" or "make the tests pass".
Only implement one if Ahmad explicitly names that file and says to implement it.
If a task you're doing depends on a MANUAL function, call it as-is and tell Ahmad it's pending.

**AI service (Python)**
- `app/chunking/section_chunker.py` — detect CV sections (Experience, Skills, Education, Projects…, Arabic + English headings) and chunk by section
- `app/extraction/extractor.py` — LLM structured extraction (the prompt + JSON schema + validation/retry)
- `app/extraction/prompts.py` — all LLM prompts
- `app/retrieval/bm25_index.py` — BM25 build/search per workspace
- `app/retrieval/fusion.py` — Reciprocal Rank Fusion
- `app/retrieval/aggregate.py` — aggregate chunk scores into candidate scores
- `app/retrieval/reranker.py` — cross-encoder reranking logic
- `app/retrieval/query_parser.py` — turn a recruiter query into filters (years ≥ N, skills, languages)
- `app/retrieval/pipeline.py` — the end-to-end search pipeline wiring the above
- `app/explain/explainer.py` — "why this candidate" with quoted evidence
- `eval/metrics.py` and `eval/run_eval.py` — Precision@k, nDCG@k, comparison of retrieval variants

**Web (.NET)**
- `TalentLens.Domain/Entities/*` — entity design (Ahmad designs; you may create empty files/folders only)
- `TalentLens.Infrastructure/Data/AppDbContext.cs` — relationships, workspace (tenant) global query filters
- `TalentLens.Infrastructure/AiService/AiServiceClient.cs` — typed HttpClient to the AI service (incl. streaming)
- `TalentLens.Web/Services/CandidateIngestionJob.cs` — background ingestion flow (Hangfire)
- `TalentLens.Web/Services/SearchService.cs` — search orchestration

### 🤖 CLAUDE CODE (you build these)
- Folder structure, `__init__.py` files, `requirements.txt`, `.gitignore`, `.env.example`
- `app/config.py` (pydantic-settings), `app/models.py` (Pydantic contracts below), logging setup
- `app/main.py` FastAPI app, routers, error handlers, `/health`
- `app/parsing/parser.py` — PDF (PyMuPDF) + DOCX (python-docx, **including tables**) → text; keep line breaks
- `app/embeddings/embedder.py` — thin wrapper loading `BAAI/bge-m3` via sentence-transformers (load once, cache)
- `app/storage/vector_store.py` — thin ChromaDB wrapper (one collection per workspace; add/query/delete by candidate_id)
- `app/llm/client.py` — provider-agnostic LLM client (OpenAI-compatible API; base URL + model from config)
- Test scaffolding + fixtures; tests for Claude-Code-owned modules
- `scripts/generate_sample_cvs.py` — generate fake CVs (fake names only, Arabic + English)
- Dockerfiles, `docker-compose.yml`
- .NET: solution + projects, Identity setup, Razor views/layouts (Bootstrap 5, RTL support), upload page,
  candidate list/detail pages, search page UI, file storage, DI registration, appsettings, migrations *after* Ahmad's entities exist
- README skeleton

---

## Repository structure

```
TalentLens/
├── CLAUDE.md
├── README.md
├── docker-compose.yml
├── ai-service/
│   ├── app/
│   │   ├── main.py
│   │   ├── config.py
│   │   ├── models.py
│   │   ├── api/            (routers: ingest.py, search.py, candidates.py)
│   │   ├── parsing/        parser.py
│   │   ├── chunking/       section_chunker.py        🔒
│   │   ├── extraction/     extractor.py, prompts.py  🔒
│   │   ├── embeddings/     embedder.py
│   │   ├── storage/        vector_store.py
│   │   ├── llm/            client.py
│   │   ├── retrieval/      bm25_index.py, fusion.py, aggregate.py,
│   │   │                   reranker.py, query_parser.py, pipeline.py   🔒
│   │   └── explain/        explainer.py              🔒
│   ├── eval/               metrics.py, run_eval.py 🔒, datasets/
│   ├── scripts/
│   ├── tests/
│   ├── sample_cvs/         (gitignored)
│   ├── data/               (gitignored — chroma, bm25 indexes)
│   ├── requirements.txt
│   ├── .env.example
│   └── Dockerfile
└── web/
    ├── TalentLens.slnx
    ├── TalentLens.Web/             ASP.NET Core MVC (.NET 10)
    ├── TalentLens.Domain/          entities 🔒
    └── TalentLens.Infrastructure/  EF Core (SQL Server), AI service client
```

---

## Contracts (app/models.py)

```python
class ParsedDocument(BaseModel):
    filename: str
    file_type: Literal["pdf", "docx"]
    text: str
    page_count: int
    char_count: int

class CVSection(BaseModel):
    name: str            # "experience" | "skills" | "education" | "projects" | "summary" | "other"
    text: str

class Chunk(BaseModel):
    chunk_id: str
    candidate_id: str
    workspace_id: str
    section: str
    text: str

class CandidateProfile(BaseModel):          # output of structured extraction
    full_name: str | None
    email: str | None
    phone: str | None
    location: str | None
    years_of_experience: float | None
    skills: list[str]
    languages: list[str]
    job_titles: list[str]
    education: list[dict]

class Evidence(BaseModel):
    section: str
    quote: str

class CandidateResult(BaseModel):
    candidate_id: str
    score: float
    reason: str
    evidence: list[Evidence]
```

## AI service API
- `POST /ingest` — multipart: `file`, `candidate_id`, `workspace_id` → `CandidateProfile`
- `POST /search` — `{workspace_id, query, top_k=10, candidate_ids?}` → `list[CandidateResult]`
- `POST /explain` — `{candidate_id, workspace_id, query}` → `CandidateResult`
- `DELETE /candidates/{candidate_id}?workspace_id=` — remove from vectors + BM25
- `GET /health`

---

## Conventions
- Python 3.11+, type hints everywhere, Pydantic v2. Format with ruff.
- Every vector/BM25 operation is scoped by `workspace_id` — never search across workspaces.
- Embeddings: `BAAI/bge-m3`. Reranker: `BAAI/bge-reranker-v2-m3`. Both must handle Arabic.
- Config only via `.env` / `appsettings.json`. No secrets in code.
- Never use real people's CVs in fixtures — fake data only.
- Keep changes small; after finishing a task, list which MANUAL stubs it touches or depends on.
