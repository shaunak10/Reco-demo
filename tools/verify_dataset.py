#!/usr/bin/env python3
"""
Step 1 exit test: independently re-derive the settlement equations and reference outcomes from the
RAW generated files (not from the generator's in-memory objects) and compare them with the
hand-authored ground-truth CSVs.  Brute force on purpose - it shares no code with the real engine.

    Expected_Net = Gross - Fees_Recorded    (duplicates excluded)
    Variance     = Bank_Credit - Expected_Net
    Contract fee = round_half_up(Gross * 1.77%, 2)

Run:  python tools/verify_dataset.py
"""
from __future__ import annotations

import csv
import sys
from collections import Counter, defaultdict
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path

import pandas as pd

DATA = Path(__file__).absolute().parent.parent / "data"
Q = Decimal("0.01")
RATE = Decimal("0.0177")

FILES = {
    "scenario1_main": ("merchant_ledger.csv", "gateway_statement.xlsx", "bank_settlement.csv"),
    "scenario2_clean": ("merchant_ledger.csv", "gateway_statement.xlsx", "bank_settlement.csv"),
    "scenario3_changed_headers": ("ledger_export.xlsx", "pg_report.csv", "account_statement.xlsx"),
}


def dec(v) -> Decimal:
    s = str(v).replace(",", "").strip()
    return Decimal(s).quantize(Q)


def read_any(path: Path) -> pd.DataFrame:
    if path.suffix == ".csv":
        return pd.read_csv(path, dtype=str, keep_default_na=False)
    return pd.read_excel(path, dtype=object).astype(object).where(lambda d: d.notna(), "")


def canonical(df: pd.DataFrame, gt_map: dict[str, str]) -> pd.DataFrame:
    inv = {f: h for h, f in gt_map.items() if f != "IGNORE"}
    return pd.DataFrame({f: df[h] for f, h in inv.items()})


def load_gt(folder: Path, name: str) -> list[dict]:
    with (folder / name).open(encoding="utf-8") as f:
        return list(csv.DictReader(f))


def verify(scn: str) -> list[str]:
    folder = DATA / scn
    errs: list[str] = []
    mapping = defaultdict(dict)
    for r in load_gt(folder, "ground_truth_mapping.csv"):
        mapping[r["Source"]][r["Header"]] = r["Expected_Canonical_Field"]
    lf, gf, bf = FILES[scn]
    led = canonical(read_any(folder / lf), mapping["ledger"])
    gw = canonical(read_any(folder / gf), mapping["gateway"])
    bk = canonical(read_any(folder / bf), mapping["bank"])
    for df, cols in ((led, ["Amount"]), (gw, ["Amount", "Fees"]), (bk, ["Amount"])):
        for c in cols:
            df[c] = df[c].map(dec)

    # ---- de-duplicate exactly as the plan's duplicate protection describes (same ref + same amount) ----
    seen, gw_rows, gw_dups = set(), [], []
    for _, r in gw.iterrows():
        k = (r["TransactionRef"], r["Amount"])
        (gw_dups if k in seen else gw_rows).append(r)
        seen.add(k)
    seen, led_rows, led_dups = set(), [], []
    for _, r in led.iterrows():
        k = (r["TransactionRef"], r["Amount"])
        (led_dups if k in seen else led_rows).append(r)
        seen.add(k)

    # ---- settlement batch equations ----
    gt_batches = {r["Settlement_Ref"]: r for r in load_gt(folder, "ground_truth_settlement_batches.csv")}
    agg = defaultdict(lambda: [0, Decimal(0), Decimal(0)])
    for r in gw_rows:
        a = agg[r["SettlementRef"]]
        a[0] += 1; a[1] += r["Amount"]; a[2] += r["Fees"]
    bank_by_ref = defaultdict(list)
    for _, r in bk.iterrows():
        bank_by_ref[r["SettlementRef"]].append(r["Amount"])
    for utr, g in gt_batches.items():
        n, gross, fees = agg[utr]
        net = gross - fees
        if n != int(g["Gateway_Txn_Count_Excl_Duplicates"]): errs.append(f"{utr}: txn count {n} != {g['Gateway_Txn_Count_Excl_Duplicates']}")
        if gross != Decimal(g["Gross"]): errs.append(f"{utr}: gross {gross} != {g['Gross']}")
        if fees != Decimal(g["Fees_Recorded"]): errs.append(f"{utr}: fees {fees} != {g['Fees_Recorded']}")
        if net != Decimal(g["Expected_Net"]): errs.append(f"{utr}: net {net} != {g['Expected_Net']}")
        credits = bank_by_ref.get(utr, [])
        if g["Bank_Credit"] == "":
            if credits: errs.append(f"{utr}: ground truth says no credit but bank file has {credits}")
        else:
            if len(credits) != 1: errs.append(f"{utr}: expected exactly one bank credit, found {credits}")
            elif credits[0] != Decimal(g["Bank_Credit"]): errs.append(f"{utr}: bank {credits[0]} != {g['Bank_Credit']}")
            elif credits[0] - net != Decimal(g["Variance_Bank_Minus_Expected"]): errs.append(f"{utr}: variance {credits[0]-net} != {g['Variance_Bank_Minus_Expected']}")

    # ---- reference-level outcomes (brute force, exact equality only) ----
    gw_by, led_by = defaultdict(list), defaultdict(list)
    for r in gw_rows: gw_by[r["TransactionRef"]].append(r)
    for r in led_rows: led_by[r["TransactionRef"]].append(r)
    got = Counter()
    for ref in set(gw_by) | set(led_by):
        l, g = led_by.get(ref, []), gw_by.get(ref, [])
        if l and g:
            got[(ref, "MATCHED" if l[0]["Amount"] == g[0]["Amount"] else "AMOUNT_MISMATCH")] += 1
        elif l:
            got[(ref, "LEDGER_ONLY")] += 1
        else:
            got[(ref, "GATEWAY_ONLY")] += 1
    for r in led_dups: got[(r["TransactionRef"], "DUPLICATE_LEDGER")] += 1
    for r in gw_dups: got[(r["TransactionRef"], "DUPLICATE_GATEWAY")] += 1
    want = Counter((r["Reference"], r["Expected_Outcome"]) for r in load_gt(folder, "ground_truth_stage1_ledger_vs_gateway.csv"))
    for k in sorted((want - got).keys()): errs.append(f"missing outcome {k}")
    for k in sorted((got - want).keys()): errs.append(f"unexpected outcome {k}")

    # ---- fee audit against contract rate ----
    got_fee = {r["TransactionRef"]: r["Fees"] - (r["Amount"] * RATE).quantize(Q, ROUND_HALF_UP)
               for r in gw_rows if r["Fees"] != (r["Amount"] * RATE).quantize(Q, ROUND_HALF_UP)}
    want_fee = {r["Reference"]: Decimal(r["Exact_Amount"]) for r in load_gt(folder, "ground_truth_exceptions.csv") if r["Exception_Type"] == "FEE_MISMATCH"}
    if got_fee != want_fee: errs.append(f"fee audit {got_fee} != {want_fee}")

    # ---- bank credits that belong to no gateway batch ----
    orphan = sorted(r for r, _ in bank_by_ref.items() if r not in gt_batches)
    want_orphan = sorted(r["Reference"] for r in load_gt(folder, "ground_truth_exceptions.csv") if r["Exception_Type"] == "UNEXPLAINED_BANK_CREDIT")
    if orphan != want_orphan: errs.append(f"unexplained bank credits {orphan} != {want_orphan}")

    print(f"[{scn}] ledger={len(led)} (dups {len(led_dups)})  gateway={len(gw)} (dups {len(gw_dups)})  bank={len(bk)}  "
          f"batches={len(gt_batches)}  outcomes_checked={sum(want.values())}  -> {'OK' if not errs else 'FAIL'}")
    return errs


if __name__ == "__main__":
    all_errs = []
    for s in FILES:
        for e in verify(s):
            all_errs.append(f"{s}: {e}")
            print("   !!", e)
    sys.exit(1 if all_errs else 0)
