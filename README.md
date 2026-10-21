# ReconAI - AI-assisted three-way settlement reconciliation (prototype)

A demonstration of reconciling a **merchant ledger**, a **payment-gateway statement** and a **bank settlement** (example: a mobile-recharge business), with AI-assisted column mapping. **Synthetic data only. This is a prototype: no RBI approval, no bank certification, not production-ready.**

## Run it (Windows)

1. Double-click **`run-demo.cmd`**. The first start compiles the app (about a minute); later starts take seconds.
2. Your browser opens **http://localhost:5080**. Use the **"Sign in as Operator"** or **"Sign in as Company Admin"** button.
3. To stop it, double-click **`stop-demo.cmd`**.

Prefer a terminal? `.\run-demo.ps1` (start), `.\run-demo.ps1 -Stop`, `.\run-demo.ps1 -NoBrowser`. If port 5080 or 8765 is busy: `.\run-demo.ps1 -WebPort 5090 -AiPort 8770`.

It starts two local services, both on your own machine only:

| Service | Address | What it is |
|---|---|---|
| Web app (ASP.NET Core Razor Pages) | http://localhost:5080 | the screens, import, matching (later steps) and database |
| AI service (Python FastAPI) | http://127.0.0.1:8765/health | the column-mapping model |

Logs are written to `logs/`. The database and uploaded files live in `src/ReconAI.Web/App_Data/` (delete that folder to reset the demo to empty).

## Try the demo data

Open **New reconciliation**, download a scenario's three files from the page (or take them from `data/`), upload them, and review the AI's suggested column mapping. `data/scenario1_main` has every kind of exception; `scenario3_changed_headers` is the same data with different headings and file formats.

## What is built

| Step | Status |
|---|---|
| 1. Synthetic datasets + ground truth | done |
| 2. Solution, sign-in, Operator / Company Admin roles | done |
| 3. File import and storage (CSV / XLSX, exact money, duplicate protection) | done |
| 4. AI column mapping (local trained model, confidence, evidence) | done |
| 5-11. Rule wizard + Admin approval, matcher, AI explanations, API import, email alerts, dashboards, packaging | not yet |

Details, assumptions and measured results for each step: [`docs/BUILD_LOG.md`](docs/BUILD_LOG.md), [`docs/results/`](docs/results), model card [`ai/MODEL_CARD.md`](ai/MODEL_CARD.md).

## Requirements

Both are already installed for you under `%USERPROFILE%\.reconai` on this machine. On another machine `run-demo.ps1` looks for:

- the **.NET 10 SDK** (`%USERPROFILE%\.reconai\dotnet`, or `dotnet` on PATH)
- **Python 3.11+** (it creates a virtual environment from `ai/requirements.txt` the first time, which needs internet)

## Layout

```
src/ReconAI.Core   rules and importing logic (no database)      ai/       Python AI service, model, evaluation
src/ReconAI.Data   database model and migrations (SQLite)        data/     synthetic datasets + ground truth
src/ReconAI.Web    Razor Pages web app                           docs/     build log, measured results
tests/             xUnit tests (incl. live tests of the AI)       tools/    dataset generator and verifier
```

## Tests

```powershell
dotnet test                           # needs the AI service running for the live tests; they skip themselves otherwise
cd ai; python -m pytest               # model and API tests
python ai/eval_mapping.py --write     # regenerate the measured results
python tools/verify_dataset.py        # re-check the datasets against the ground truth
```
Stop the web app before `dotnet test` (it locks the build output).

## Troubleshooting

- **"Port 5080 is already used"**: close the other program, or pick other ports as shown above.
- **The page does not load**: run `.\run-demo.ps1` again and read the last lines it prints; full logs are in `logs/`.
- **Sign-in shortcuts missing**: they only exist in the Development environment, which `run-demo` uses.
- **A path longer than about 100 characters**: the launcher borrows a short drive letter automatically. Keeping the project in a short folder avoids it.
