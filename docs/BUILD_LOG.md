# ReconAI prototype - build log

One section per implementation-plan step: what changed, schema/APIs, assumptions and deviations, and the **measured**
test results (failures included). Synthetic data only.

---

## Step 1 - Demonstration dataset  (commit `Step 1: ...`)

- `tools/generate_dataset.py` builds three deterministic scenarios under `data/`: `scenario1_main` (every exception type),
  `scenario2_clean` (fully reconciles), `scenario3_changed_headers` (scenario 1 content, different headings and file formats).
- Hand-authored ground truth per scenario: `ground_truth_stage1_ledger_vs_gateway.csv`, `ground_truth_settlement_batches.csv`,
  `ground_truth_exceptions.csv`, `ground_truth_mapping.csv`. Written from the scenario definitions, **not** from engine output.
- Equations: `Expected_Net = Gross - Fees_Recorded` (duplicates excluded); `Variance = Bank - Expected_Net`;
  contract fee = `round_half_up(Gross x 1.77%, 2)`.
- **Exit test:** `tools/verify_dataset.py` re-derives batch maths, reference outcomes, fee audit and orphan bank credits from the
  raw files with brute-force code that shares nothing with the engine. Result: OK on all 3 scenarios (64 / 24 / 64 outcomes checked).

## Step 2 - Solution, sign-in and role gating

### What was built
| Area | Contents |
|---|---|
| `src/ReconAI.Core` | `UserRole`, `Roles`, `Policies`, PBKDF2 `PasswordHasher` (rules/matcher arrive in Steps 5-6) |
| `src/ReconAI.Data` | `ReconDbContext` (EF Core + SQLite), entities, fail-closed tenant filter, append-only audit guard, `DemoSeeder`, migration `Initial` |
| `src/ReconAI.Web` | Razor Pages shell: cookie sign-in, role policies, sidebar nav, dashboard home, Admin > Rule approvals, Admin > Audit log, security headers |
| `ai/` | FastAPI skeleton: `GET /health` only, loopback-only Host check. No model logic yet - capability flags are all `false` |
| `tests/ReconAI.Tests` | xUnit: 36 tests (authorization, sign-in hardening, password hashing, tenant isolation, audit immutability, migration) |

### Schema (migration `Initial`; the other plan tables arrive with the step that needs them)
- `Tenants(Id, Name, CreatedUtc)`
- `Users(Id, TenantId FK, Email UNIQUE lower-case, DisplayName, Role, PasswordHash, IsActive, FailedLoginCount, LockoutEndUtc, CreatedUtc)`
- `AuditEvents(Id, TenantId, UserId?, Actor, Action, EntityType, EntityId, Detail, OccurredUtc)` + index `(TenantId, OccurredUtc)`

### Routes
| Route | Access |
|---|---|
| `GET/POST /Account/Login` (+ `?handler=Demo`, Development only) | anonymous |
| `POST /Account/Logout` | signed in |
| `GET /` dashboard home | Operator, Company Admin |
| `GET /Admin/Approvals`, `POST ?handler=Approve\|Reject&id=` | **Company Admin only** (folder convention + `[Authorize]` attribute) |
| `GET /Admin/Audit` | Company Admin only |
| `GET /Account/AccessDenied` | returns HTTP 403 |
| AI service `GET http://127.0.0.1:8765/health` | loopback only |

Everything else falls under a fallback policy: **signed-in users only**.

### Assumptions and deviations (please read)
- **SQLite instead of SQL Server.** SQL Server needs an admin install, which this machine did not allow. The model is portable;
  moving to SQL Server changes the provider and (from Step 3) the money column type (`decimal(18,2)`).
- **.NET 10 (LTS)** rather than .NET 8, because .NET 8 support ends in November 2026.
- **AI service port 8765**, not 8100: another Python process on this machine already holds 8100.
- Toolchain lives outside the repo: .NET SDK in `C:\Users\Shaunak\.reconai\dotnet`, Python 3.13 venv in `C:\Users\Shaunak\.reconai\venv`.
- Demo accounts are synthetic and defined in `src/ReconAI.Web/appsettings.Development.json`; only PBKDF2 hashes are stored in the DB.
  The one-click demo sign-in buttons exist only when `Demo:QuickLogin` is true (Development).
- Local HTTP only (`http://localhost:5080`); the auth cookie is `SameAsRequest`, so use HTTPS and `Secure` before any real deployment.
- `POST /Admin/Approvals` Approve/Reject return **404** for now: no rule can exist before Step 5. They run only *after* the Admin-only
  authorization check, which is what the exit test proves.
- Unknown-email sign-in failures are logged but not written to `AuditEvents` (no tenant to attribute them to).

### Measured results
| Check | Result |
|---|---|
| `dotnet test` | **36 passed, 0 failed** |
| `pytest` (ai/) | **2 passed**, 0 failed |
| Dataset verifier (Step 1 re-check) | OK on 3 scenarios |
| Exit test "Operator cannot access Admin approval operation" | passes; **mutation check:** with the folder convention and the attribute removed, the Operator tests fail 3 of 4, so the tests do detect a missing guard |
| Manual browser check | Operator: dashboard OK, `/Admin/Approvals` redirected to the 403 page. Admin: both Admin pages open, audit log shows real sign-in/out events. Light + dark theme and the mobile drawer render correctly |

### How to run (until the one-command script in Step 11)
```powershell
# AI service
& C:\Users\Shaunak\.reconai\venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8765 --app-dir ai
# Web app (Development environment seeds the demo users)
& C:\Users\Shaunak\.reconai\dotnet\dotnet.exe run --project src/ReconAI.Web --urls http://localhost:5080 -- --environment Development
```
If the repo path is longer than ~120 characters on Windows, map a short drive first (`subst R: <repo folder>`).
