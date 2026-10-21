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

---

## Step 3 - File import and SQL storage

### What was built
| Area | Contents |
|---|---|
| `ReconAI.Core/Importing` | `TabularReader` (CSV + XLSX, limits, safety checks), `ValueParsers` (exact money, day-first dates), `Canonicalizer` (mapping validation, canonical rows, row keys + checksums), `SourceSchema` (the three sources, canonical fields, mandatory sets) |
| `ReconAI.Data` | Entities `SourceFile`, `ColumnMapping`, `SourceRow`; `Money` converter (decimal <-> integer paise); append-only guard generalised to `IAppendOnly`; migration `SourceImport` |
| `ReconAI.Web` | `ImportService` (upload, preview, confirm, void), `AuditLog`, `SampleData`; pages **New reconciliation** (`/Recon/New`), **Map columns** (`/Recon/Mapping/{id}`), **File** (`/Recon/File/{id}`) |

### Flow
1. **Upload** a file into the Ledger / Gateway / Bank box. It is validated, its SHA-256 is computed, the original bytes are stored untouched (named by checksum, outside `wwwroot`), and a `SourceFile` is created as *AwaitingMapping*. **No rows exist yet.**
2. **Map columns**: the operator chooses a canonical field per column. A mapping confirmed earlier for the same headings is offered again. An AJAX dry run validates every row without storing anything.
3. **Confirm**: in one transaction the mapping is recorded and every row is written, valid or rejected, each with an immutable key and checksum. Rejected rows are kept with their reason; if **no** row is valid, nothing is imported.
4. **Void** (reason required) removes a file from use but keeps its rows for audit and frees its checksum for a corrected re-upload.

### Schema (migration `SourceImport`)
- `SourceFiles(Id, TenantId, Kind, OriginalFileName, Format, SheetName, SizeBytes, Sha256, StoredPath, HeaderJson, Status, DataRowCount, BlankRowsSkipped, ValidRowCount, RejectedRowCount, UploadedBy*, UploadedUtc, ImportedUtc, IsVoided, Voided*)`; **unique `(TenantId, Kind, Sha256) WHERE IsVoided = 0`**
- `ColumnMappings(Id, TenantId, SourceFileId UNIQUE, Kind, HeaderSignature, MappingJson, Origin, ConfirmedBy*, ConfirmedUtc)` - append-only
- `SourceRows(Id, TenantId, SourceFileId, Kind, RowNumber, RowKey, RowSha256, RawJson, ImportStatus, RejectReason, TransactionRef, SettlementRef, Amount, Fees, Currency, TxnDate, Status)`; unique `(SourceFileId, RowNumber)`; indexes on `(TenantId, Kind, TransactionRef)` and `(..., SettlementRef)` for the Step 6 matcher - append-only
- Row identity: `RowKey = {L|G|B}-{first 12 hex of file SHA-256}-{row number}`, `RowSha256` = hash of the original cells. Same bytes => same keys and checksums.

### Rules enforced on import
- Extensions `.csv` / `.xlsx` only, **and** the content must match (a zip renamed `.csv`, a text file renamed `.xlsx`, binary data, macro workbooks, zip bombs are refused). Limits: 5 MB, 50,000 rows, 60 columns, 512 characters per cell.
- Headings must be present and unique (case-insensitive). Trailing empty columns are ignored.
- **Money**: parsed to `decimal`; more than 2 decimal places is **rejected, never rounded**; `12,34`, `1e3`, negatives, `(100.00)` are rejected; `1,234.50`, `12,34,567.00`, `Rs. 149` are accepted. Stored as integer paise, so SQL `SUM` is exact, and the database layer itself refuses a value that would need rounding.
- **Dates** are day-first (`03/04/2026` = 3 April); `yyyy-MM-dd`, `dd/MM/yyyy`, `dd-MMM-yyyy` and Excel date cells are accepted.
- Currency defaults to INR when unmapped; anything other than INR is rejected (prototype scope).
- Mandatory fields: Ledger = reference, amount, date; Gateway = reference, amount, fees, settlement reference, date; Bank = settlement reference, amount, date.

### Routes
| Route | Access |
|---|---|
| `GET /Recon/New`, `POST ?handler=Upload`, `GET ?handler=Sample&scenario=&file=` | Operator, Company Admin |
| `GET /Recon/Mapping/{id}`, `POST ?handler=Preview` (JSON), `POST ?handler=Confirm` | Operator, Company Admin |
| `GET /Recon/File/{id}`, `POST ?handler=Void` | Operator, Company Admin |

All other behaviour follows the existing rules: signed-in only, antiforgery on every POST, tenant filter on every query.

### Assumptions and limitations
- Column mapping is **manual** in this step; AI suggestions are Step 4 and will use the same page.
- Only the first visible sheet of a workbook is read. Excel formulas are read as their cached values. Leading zeros lost by Excel's own number storage cannot be recovered.
- Uploaded originals are kept in `App_Data/uploads/{tenant}/{sha256}.{ext}` (gitignored) and are re-hashed before every import. There is no virus scanning in the prototype.
- The zip-bomb guard trusts the archive's declared sizes; it is a prototype-level safeguard.
- Duplicate *rows* across different files, and the rule that a file in use by a run cannot be voided, arrive with the matcher in Step 6. This step blocks duplicate *files*.
- Sample-data downloads are served only when `Demo:SampleData` is on (Development) and only for names present in a scenario folder.
- The `appsettings` upload and sample paths are configurable (`Storage:UploadsPath`, `Demo:SampleDataPath`).

### Measured results
| Check | Result |
|---|---|
| `dotnet test` | **144 passed, 0 failed** (36 from Step 2, 108 new) |
| `pytest` (ai/) | 2 passed |
| Dataset verifier | OK on 3 scenarios |
| Exit test "same files produce same canonical rows" | Passes at two levels: the importer (3 sources x 2 runs) and the database (two separate databases import the same three files; every row key, checksum, raw cell and canonical value is identical) |
| Exit test "duplicate upload doesn't double-count" | Passes: second upload refused, still 1 file / 61 rows, SQL total unchanged at 51,189.00; also caught before the first copy was mapped, and when the same file goes into the wrong box; the database independently blocks a second active copy |
| Ground-truth agreement | Imported ledger total 51,189.00, gateway gross incl. the duplicated row, gateway fees and bank credits equal the independently authored ground truth to the paisa; CSV, XLSX and renamed-heading variants give identical canonical rows |
| Error feedback | 9 bad-file cases (wrong type, empty, 6 MB, fake xlsx, binary, no data, duplicate/blank headings) each give a specific message, store nothing and are audited |
| Mutation check | With row keys made non-deterministic and the 2-decimal rule disabled, 8 tests fail; code restored |
| Manual browser check | Upload -> mapping (live required-field checklist, AJAX check: 61 valid / 0 rejected) -> confirm -> file page (61 rows, total 51,189.00); duplicate refused with a clear message; no console errors; no horizontal scroll at phone width |
