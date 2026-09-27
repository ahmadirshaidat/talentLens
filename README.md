# TalentLens — AI job platform for the Arab world

A bilingual (Arabic + English, RTL) job platform in the spirit of Bayt.com, with an AI
engine that actually **reads CVs**. Three kinds of users:

| Role | What they do |
|---|---|
| **Job seeker** | Signs up free, uploads a CV (the AI fills the profile), browses and saves jobs, applies in one click, clicks **"Check my match"** to see an AI score with quotes from their CV, tracks every application through the hiring steps. |
| **Company / HR recruiter** | Signs up a company (approved by the admin), posts jobs, clicks **"Rank with AI"** to order applicants by real fit (score + reason + CV quotes), moves applicants through Submitted → Reviewed → Shortlisted → Interview → Offer → Hired, searches the **job seekers database** or a **private talent pool** of CVs they uploaded, in plain language. |
| **Platform admin** | Approves / suspends companies, locks users, closes jobs, sees platform stats and AI service health. |

```
┌───────────────────────────────┐   HTTP/JSON   ┌──────────────────────────────────────────┐
│ web/ ASP.NET Core MVC         │ ────────────▶ │ ai-service/ FastAPI (Python)             │
│  • Identity + 3 roles         │               │  parse → sections → chunks               │
│  • public job board           │               │  LLM / heuristic profile extraction      │
│  • Seeker / Employer / Admin  │               │  bge-m3 embeddings → ChromaDB  (dense)   │
│    areas                      │               │  BM25                          (keyword) │
│  • Hangfire CV ingestion      │               │  RRF fusion → bge-reranker → aggregate   │
│  • SQL Server (EF Core)       │               │  query filters (years/skills/languages)  │
│  • EN / AR (RTL) UI           │               │  explanations with verified quotes       │
└───────────────────────────────┘               └──────────────────────────────────────────┘
```

**Privacy model.** Each company's talent pool and search history are private: EF Core query
filters scope them to the signed-in recruiter's company, and the AI service keeps one index
per company. Job seekers live in a shared `seekers` index; recruiters can only find seekers
who opted in to being visible, or who applied to that company's jobs (an allow-list is sent
with every search).

## How search works

1. **Parse the query** — `5+ years`, `خبرة ٥ سنوات`, skills and spoken languages become filters.
2. **Two retrievers** — dense (`BAAI/bge-m3`, multilingual meaning) and BM25 (exact words like `C#`).
3. **Reciprocal Rank Fusion** merges both rankings.
4. **Cross-encoder reranking** (`BAAI/bge-reranker-v2-m3`) rescores the top ~30 chunks.
5. **Aggregate** chunk scores into one score per candidate (best chunk + top-3 average).
6. **Apply filters** — too few years → dropped; missing skills/languages → score reduced.
7. **Explain** — best-matching CV lines as quotes; with an LLM configured, `/explain` writes a
   reason and every quote is checked to exist verbatim in the CV (hallucinated quotes are dropped).

Every step lives in its own small file under `ai-service/app/retrieval/` with a 🧸
"explain like I'm 5" comment at the top.

### Evaluation (16 fake CVs, 16 labeled queries, k = 3)

`python -m eval.run_eval --ingest sample_cvs --k 3`

| variant        | P@3   | R@3   | nDCG@3 | MRR   |
|----------------|-------|-------|--------|-------|
| dense          | 0.437 | 0.958 | 0.954  | 0.969 |
| bm25           | 0.396 | 0.865 | 0.874  | 0.919 |
| hybrid (RRF)   | 0.417 | 0.896 | 0.912  | 0.953 |
| hybrid+rerank  | 0.458 | 0.979 | 0.961  | 0.969 |
| **full**       | **0.479** | **1.000** | **0.997** | **1.000** |

(P@3 can't reach 1.0 here: most queries have only 1–2 relevant CVs.) The reranker is the
slowest step on CPU (several seconds per query); set `USE_RERANKER=false` for instant results
with slightly lower quality.

## Run it locally

### 1. AI service (Python 3.11+)

```bash
cd ai-service
python -m venv .venv
.venv\Scripts\activate            # Windows  (source .venv/bin/activate on Linux/macOS)
pip install -r requirements.txt
cp .env.example .env              # optional: set LLM_BASE_URL / LLM_API_KEY / LLM_MODEL
uvicorn app.main:app --port 8000
```

- Without an LLM configured, profile extraction uses a rule-based fallback (regex + skill
  vocabulary) and explanations use templates — everything still works.
- With any OpenAI-compatible endpoint (OpenAI, Groq, Ollama, vLLM…), extraction and
  explanations use the LLM with JSON validation and retries.
- First start downloads `bge-m3` and `bge-reranker-v2-m3` (~4.5 GB) from Hugging Face.

### 2. Web app (.NET 10 + SQL Server)

```bash
cd web
dotnet user-secrets set "Seed:AdminEmail" "admin@example.com" --project TalentLens.Web
dotnet user-secrets set "Seed:AdminPassword" "<choose a password>" --project TalentLens.Web
dotnet run --project TalentLens.Web --launch-profile http
```

- `ConnectionStrings:Default` is in `appsettings.json` (LocalDB) and
  `appsettings.Development.json` (`.\SQLEXPRESS`); change it to your SQL Server.
- The database is migrated on startup, roles are created, the admin is seeded from the
  secrets above, and in Development `Seed:DemoData` adds 4 fake companies with 12 jobs.
- Set `Platform:AutoApproveCompanies=true` to skip admin approval of new employers.
- Hangfire dashboard: `/hangfire` (admins, Development only).

### Or everything with Docker

```bash
docker compose up --build        # http://localhost:8080
```

### Sample data

```bash
cd ai-service
python scripts/generate_sample_cvs.py   # 16 fake CVs (EN PDF/DOCX + AR DOCX) + eval dataset
```

All names, companies, emails and phone numbers in samples and tests are invented.

## Tests

```bash
cd ai-service && pytest          # 117 tests, no model downloads (fakes for models/LLM)
cd web && dotnet test            # 43 tests: AI client, company isolation, job board visibility,
                                 # matching, profile merge, storage, every UI text key exists
```

## Project structure

```
ai-service/
  app/
    api/            ingest.py, search.py (+ /explain), candidates.py
    parsing/        parser.py            PDF (PyMuPDF) + DOCX incl. tables
    chunking/       section_chunker.py   Arabic/English section headings → chunks
    extraction/     extractor.py (LLM + validation/retry), heuristic.py (no-LLM fallback), prompts.py
    embeddings/     embedder.py          bge-m3, loaded once
    storage/        vector_store.py (ChromaDB per workspace), profile_store.py
    retrieval/      bm25_index.py, fusion.py, reranker.py, aggregate.py, query_parser.py, pipeline.py
    explain/        explainer.py (LLM + quote verification), evidence.py (template reasons/quotes)
    vocabulary.py   skills + languages with Arabic aliases
  eval/             metrics.py, run_eval.py, datasets/sample_queries.json
  scripts/          generate_sample_cvs.py
web/
  TalentLens.Domain/          Company, Job, JobSeekerProfile, JobApplication, SavedJob, Candidate, SearchLog, AppRoles
  TalentLens.Infrastructure/  AppDbContext (company query filters), AiServiceClient, file storage, migrations
  TalentLens.Web/
    Controllers/              public: Home, Jobs (board, apply, save, match), Companies, Account (sign-up ×2)
    Areas/Seeker/             dashboard + recommendations, profile & CV, applications, saved jobs
    Areas/Employer/           dashboard, jobs + applicants + AI ranking, CV search, talent pool, company
    Areas/Admin/              overview, companies approval, users, jobs moderation
    Services/                 ingestion jobs (Hangfire), CvSearchService, ApplicantRankingService,
                              JobMatchService, SeedData
  TalentLens.Tests/           xUnit
```

## AI service API

All endpoints except `/health` require the `X-Api-Key` header (the shared `SERVICE_API_KEY`
/ `AiService:ApiKey`). Every response echoes an `X-Request-ID` (forwarded from the web app) that
also appears in both services' logs.

| Method | Path | Purpose |
|---|---|---|
| POST | `/ingest` | CV file → parsed, chunked, indexed; returns the extracted profile |
| POST | `/search` | natural-language CV search within one workspace |
| POST | `/explain` | why one candidate matches a query, with verbatim quotes |
| DELETE | `/candidates/{id}?workspace_id=` | remove a CV from all indexes |
| POST | `/extract/job-requirements` | job text → structured requirements *(pending, returns 501)* |
| POST | `/match/job` | requirement-by-requirement candidate matching *(pending, returns 501)* |
| GET | `/health` | liveness + model status (public) |

## Security principles (current state)

Implemented: cookie auth + roles, company-scoped query filters, private CV storage behind
authorized actions, CSRF on every POST, service-to-service API key, request correlation ids,
generic 500 messages, secrets via `.env` / user-secrets, and:

- **Upload screening** — every CV passes `UploadScreener`: extension + size + real file signature,
  then `IMalwareScanner`. Providers: `None` (development; files are *not* scanned, and a warning
  is logged outside Development) or `ClamAV` (clamd over TCP, `INSTREAM`). With `FailClosed=true`
  an unavailable scanner blocks the upload instead of letting it through.
- **Rate limiting** (ASP.NET Core rate limiter, per signed-in user or per IP when anonymous;
  429 + `Retry-After`): sign-in/sign-up 10/min per IP · AI calls (search, explain, match,
  rank) 20/min · CV uploads 30 per 10 min · everything else 300/min (static files excluded).
  Behind a reverse proxy, enable ForwardedHeaders so limits see the real client IP.

**Not yet:** audit log, consent records, data-deletion flows, HTTPS/HSTS review for deployment,
distributed rate limiting for multiple web instances. TalentLens is **not production-ready**, and
no legal/privacy compliance is claimed.

## Configuration

| Where | Key | Default |
|---|---|---|
| `ai-service/.env` | `SERVICE_API_KEY` | empty → no auth (refused when `ENVIRONMENT=production`) |
| | `LLM_BASE_URL`, `LLM_API_KEY`, `LLM_MODEL` | empty → heuristic mode |
| | `USE_RERANKER` / `RERANK_CANDIDATES` | `true` / `30` |
| | `USE_LLM_QUERY_PARSER` | `false` (rules are fast and free) |
| `appsettings.json` | `ConnectionStrings:Default` | LocalDB |
| | `AiService:BaseUrl` | `http://localhost:8000` |
| user-secrets / env | `AiService:ApiKey` | must equal `SERVICE_API_KEY` |
| | `Storage:UploadsPath`, `Storage:MaxUploadMb` | `App_Data/uploads`, `10` |
| | `Security:MalwareScanning:Provider` / `Host` / `Port` / `FailClosed` | `None` / `localhost` / `3310` / `true` |
| | `RateLimiting:Enabled` / `AuthPerMinute` / `AiPerMinute` / `UploadsPerTenMinutes` / `GlobalPerMinute` | `true` / 10 / 20 / 30 / 300 |
