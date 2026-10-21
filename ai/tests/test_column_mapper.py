"""Column ranker tests: data hygiene, parsers, determinism, regression gates on the real datasets, rules, privacy, API."""
from __future__ import annotations

import csv
import json
import logging
from pathlib import Path

import numpy as np
import pytest
from fastapi.testclient import TestClient

from app import mapper as M
from app import profile as prof
from app.main import app
from app.text import normalize
from eval_mapping import MAND, SCENARIOS, read_columns

ROOT = Path(__file__).absolute().parent.parent.parent
DATA = ROOT / "ai" / "data"


@pytest.fixture(scope="session")
def mapper() -> M.ColumnMapper:
    return M.get_mapper()


def _flat(corpus: dict) -> dict[str, set[str]]:
    out: dict[str, set[str]] = {}
    for label, items in corpus.items():
        if label.startswith("_"):
            continue
        for item in items:
            out.setdefault(normalize(item["h"] if isinstance(item, dict) else item), set()).add(label)
    return out


def _demo_truth(scenario: str, kind: str) -> dict[str, str | None]:
    with (ROOT / "data" / scenario / "ground_truth_mapping.csv").open(encoding="utf-8") as fh:
        return {r["Header"]: (None if r["Expected_Canonical_Field"] == "IGNORE" else r["Expected_Canonical_Field"])
                for r in csv.DictReader(fh) if r["Source"] == kind.lower()}


# ---------------------------------------------------------------------------------------- data hygiene
def test_training_headings_never_overlap_the_evaluation_headings_or_the_demo_datasets():
    train = _flat(json.loads((DATA / "header_corpus.json").read_text(encoding="utf-8")))
    held_out = _flat(json.loads((DATA / "eval_headers.json").read_text(encoding="utf-8")))
    demo = {normalize(h) for s in SCENARIOS for k in ("Ledger", "Gateway", "Bank") for h in _demo_truth(s, k)}
    assert not (set(train) & set(held_out)), sorted(set(train) & set(held_out))
    assert not (set(train) & demo), sorted(set(train) & demo)


def test_each_training_heading_belongs_to_exactly_one_label():
    train = _flat(json.loads((DATA / "header_corpus.json").read_text(encoding="utf-8")))
    assert [h for h, labels in train.items() if len(labels) > 1] == []
    for field in ("TransactionRef", "Amount", "TxnDate", "SettlementRef", "Fees", "Status", "Currency"):
        assert sum(field in labels for labels in train.values()) >= 10


# ---------------------------------------------------------------------------------------- text and values
@pytest.mark.parametrize("raw,expected", [
    ("TxnRef", "txn ref"), ("Value (INR)", "value inr"), ("bank_reference_no", "bank reference no"),
    ("  Payment   Ref ", "payment ref"), ("UTR", "utr"), ("Order #", "order"), ("NEFT/IMPS UTR", "neft imps utr"),
])
def test_normalize(raw, expected):
    assert normalize(raw) == expected


def test_value_parsers():
    assert prof.parse_money("1,234.50") == 1234.5 and prof.parse_money("Rs. 149") == 149.0 and prof.parse_money("₹ 2,999.00") == 2999.0
    assert prof.parse_money("12,34") is None and prof.parse_money("abc") is None and prof.parse_money("5.123") is None
    assert prof.is_date("2026-10-15") and prof.is_date("15/10/2026 14:22:10") and prof.is_date("16-Oct-2026")
    assert not prof.is_date("RCH-261015-0001") and not prof.is_date("2026-13-45") and not prof.is_date("599.00")


def test_value_profile_recognises_balances_zero_columns_and_references():
    n = prof.NAMES
    balance, _ = prof.profile([f"{250000 + 4000 * i:.2f}" for i in range(20)])
    zeros, _ = prof.profile(["0.00"] * 20)
    refs, _ = prof.profile([f"RCH-2610{i:02d}-{i:04d}" for i in range(1, 21)])
    dates, _ = prof.profile([f"{d:02d}/10/2026" for d in range(1, 21)])
    assert balance[n.index("m_mono")] > 0.95
    assert zeros[n.index("m_zero_frac")] == 1.0
    assert refs[n.index("frac_ref_like")] == 1.0 and refs[n.index("distinct_ratio")] == 1.0
    assert dates[n.index("frac_date")] == 1.0
    assert np.all(np.isfinite(prof.profile([])[0]))                 # an empty column must not break anything


# ---------------------------------------------------------------------------------------- model basics
def test_training_is_deterministic():
    a = M.ColumnMapper(files_per_kind=40)
    b = M.ColumnMapper(files_per_kind=40)
    assert a.info.version == b.info.version
    assert np.array_equal(a.model.W, b.model.W) and np.array_equal(a.model.b, b.model.b)


def test_version_changes_when_the_corpus_changes():
    corpus = M.load_corpus()
    corpus["Amount"] = corpus["Amount"] + ["brand new amount label"]
    assert M.ColumnMapper(corpus=corpus, files_per_kind=10).info.version != M.ColumnMapper(files_per_kind=10).info.version


def test_cached_weights_round_trip(tmp_path, monkeypatch):
    monkeypatch.setattr(M, "CACHE_DIR", tmp_path)
    fresh = M.ColumnMapper(files_per_kind=30, use_cache=True)
    cached = M.ColumnMapper(files_per_kind=30, use_cache=True)
    assert fresh.info.source == "trained" and cached.info.source == "loaded from cache"
    assert np.array_equal(fresh.model.W, cached.model.W) and fresh.info.version == cached.info.version
    assert list(tmp_path.glob("*.npz"))


def test_probabilities_are_valid_distributions(mapper):
    headers, values = read_columns(ROOT / "data" / "scenario1_main" / "bank_settlement.csv")
    P, _, _ = mapper.probabilities("Bank", headers, values)
    assert np.allclose(P.sum(axis=1), 1.0) and (P >= 0).all()


# ---------------------------------------------------------------------------------------- regression gates on the real datasets
@pytest.mark.parametrize("scenario", list(SCENARIOS))
@pytest.mark.parametrize("kind", ["Ledger", "Gateway", "Bank"])
def test_real_dataset_suggestions_match_the_human_ground_truth(mapper, scenario, kind):
    """Regression gate (measured 100% when written). A drop below 90% per file, or any wrong REQUIRED field, fails."""
    headers, values = read_columns(ROOT / "data" / scenario / SCENARIOS[scenario][kind])
    truth = _demo_truth(scenario, kind)
    suggestions = mapper.suggest(kind, headers, values)
    got = {s["header"]: s["field"] for s in suggestions}
    accuracy = sum(got[h] == truth[h] for h in headers) / len(headers)
    assert accuracy >= 0.9, {h: (got[h], truth[h]) for h in headers if got[h] != truth[h]}
    for required in MAND[kind]:
        assert [h for h in headers if got[h] == required] == [h for h in headers if truth[h] == required]


def test_changing_the_headings_changes_the_suggestions_to_match(mapper):
    """Same data, completely different headings and file formats: the suggested mapping follows the data."""
    def by_field(scenario, kind):
        headers, values = read_columns(ROOT / "data" / scenario / SCENARIOS[scenario][kind])
        return {s["field"]: s["columnIndex"] for s in mapper.suggest(kind, headers, values) if s["field"]}
    for kind in ("Ledger", "Gateway", "Bank"):
        original, renamed = by_field("scenario1_main", kind), by_field("scenario3_changed_headers", kind)
        assert set(original) == set(renamed) == set(MAND[kind]) | ({"Status"} if kind != "Bank" else set())


def test_a_misleading_heading_lowers_or_changes_the_suggestion(mapper):
    headers, values = read_columns(ROOT / "data" / "scenario1_main" / "gateway_statement.xlsx")
    base = {s["header"]: s for s in mapper.suggest("Gateway", headers, values)}["Gross Amount"]
    swapped = [("Customer Remarks" if h == "Gross Amount" else h) for h in headers]
    now = {s["header"]: s for s in mapper.suggest("Gateway", swapped, values)}["Customer Remarks"]
    assert base["field"] == "Amount"
    assert now["field"] != "Amount" or now["confidence"] < base["confidence"] - 0.15


# ---------------------------------------------------------------------------------------- rules that are not the model
def test_each_field_is_suggested_at_most_once(mapper):
    headers = ["Amount", "Gross Amount", "Total Amount", "Payment Amount", "Txn Date", "Order Id"]
    values = [[f"{100 + i}.00" for i in range(10)]] * 4 + [[f"2026-10-{d:02d}" for d in range(1, 11)], [f"ORD{i:08d}" for i in range(10)]]
    fields = [s["field"] for s in mapper.suggest("Ledger", headers, values) if s["field"]]
    assert len(fields) == len(set(fields))


def test_a_source_never_receives_a_field_it_cannot_have(mapper):
    headers = ["Transaction Reference", "Fee", "Settlement Reference", "Amount"]
    values = [[f"TXN{i:07d}" for i in range(10)], [f"{2 + i / 10:.2f}" for i in range(10)], [f"UTR{i:09d}" for i in range(10)], [f"{1000 + i}.00" for i in range(10)]]
    bank = {s["field"] for s in mapper.suggest("Bank", headers, values)}
    ledger = {s["field"] for s in mapper.suggest("Ledger", headers, values)}
    assert not (bank & {"TransactionRef", "Fees"})
    assert not (ledger & {"SettlementRef", "Fees"})


def test_gibberish_with_no_values_is_never_a_confident_suggestion(mapper):
    out = mapper.suggest("Ledger", ["xq", "zzz", "qwerty asdf"], [[], [], []])
    assert all(s["band"] != "high" or s["field"] is None for s in out)


def test_confidence_is_between_zero_and_one_and_banded_consistently(mapper):
    headers, values = read_columns(ROOT / "data" / "scenario3_changed_headers" / "pg_report.csv")
    for s in mapper.suggest("Gateway", headers, values):
        assert 0.0 <= s["confidence"] <= 1.0
        assert s["band"] == ("high" if s["confidence"] >= 0.8 else "medium" if s["confidence"] >= 0.5 else "low")
        assert all(isinstance(r, str) and r for r in s["reasons"])


# ---------------------------------------------------------------------------------------- privacy
def test_reasons_and_logs_never_contain_sample_values(mapper, caplog):
    secret = "SECRET-VALUE-XYZ"
    caplog.set_level(logging.DEBUG)
    with TestClient(app) as client:
        body = {"sourceKind": "Ledger", "columns": [
            {"header": "Order Ref", "samples": [f"{secret}-{i:04d}" for i in range(20)]},
            {"header": "Customer Note", "samples": [f"{secret} note {i}" for i in range(20)]}]}
        res = client.post("/v1/map-columns", json=body)
    assert res.status_code == 200
    assert secret not in json.dumps(res.json())
    assert secret not in caplog.text


# ---------------------------------------------------------------------------------------- API contract
def _body(kind="Ledger", columns=None):
    return {"sourceKind": kind, "columns": columns or [{"header": "Order Ref", "samples": ["A1B2C3D4E5", "F6G7H8I9J0"]}]}


def test_api_happy_path_shape():
    with TestClient(app) as client:
        headers, values = read_columns(ROOT / "data" / "scenario1_main" / "merchant_ledger.csv")
        res = client.post("/v1/map-columns", json={"sourceKind": "Ledger", "columns": [{"header": h, "samples": v} for h, v in zip(headers, values)]})
    assert res.status_code == 200
    data = res.json()
    assert data["model"]["name"] == "reconai-column-ranker" and isinstance(data["latencyMs"], int)
    assert [s["columnIndex"] for s in data["suggestions"]] == list(range(len(headers)))
    first = data["suggestions"][1]
    assert first["header"] == "Order_Ref" and first["field"] == "TransactionRef" and first["reasons"] and "alternatives" in first


@pytest.mark.parametrize("payload", [
    {"sourceKind": "Nonsense", "columns": [{"header": "a", "samples": []}]},
    {"sourceKind": "Ledger", "columns": []},
    {"sourceKind": "Ledger", "columns": [{"header": "h" * 101, "samples": []}]},
    {"sourceKind": "Ledger", "columns": [{"header": "a", "samples": ["x"] * 51}]},
    {"sourceKind": "Ledger", "columns": [{"header": f"c{i}", "samples": []} for i in range(61)]},
    {"columns": [{"header": "a", "samples": []}]},
])
def test_api_rejects_malformed_requests(payload):
    with TestClient(app) as client:
        assert client.post("/v1/map-columns", json=payload).status_code == 422


def test_api_truncates_very_long_sample_values_instead_of_failing():
    with TestClient(app) as client:
        res = client.post("/v1/map-columns", json=_body(columns=[{"header": "Note", "samples": ["x" * 5000] * 5}]))
    assert res.status_code == 200
