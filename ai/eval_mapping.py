"""
Honest evaluation of the column ranker (Step 4).  Run from the repository root or from ai/:

    python ai/eval_mapping.py            # prints the report
    python ai/eval_mapping.py --write    # also writes docs/results/step4_column_mapping.md and ai/results/mapping_eval.json

Four independent measurements, all on data the model was NOT trained on:
  A. grouped cross-validation on the synthetic training files (whole headings held out together)
  B. held-out evaluation headings (ai/data/eval_headers.json) placed in freshly generated synthetic files, compared with a
     heading-only fuzzy-matching baseline
  C. the real demo datasets (data/scenario*/), scored against the human-authored ground_truth_mapping.csv
  D. runtime and memory
Nothing here tunes the model; failures are listed rather than hidden.
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import random
import statistics
import sys
import time
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path

import numpy as np
import pandas as pd

HERE = Path(__file__).absolute().parent
sys.path.insert(0, str(HERE))

from app import mapper as M  # noqa: E402
from app.synth import HeadingPool, make_file  # noqa: E402
from app.text import normalize  # noqa: E402

ROOT = HERE.parent
SCENARIOS = {
    "scenario1_main": {"Ledger": "merchant_ledger.csv", "Gateway": "gateway_statement.xlsx", "Bank": "bank_settlement.csv"},
    "scenario2_clean": {"Ledger": "merchant_ledger.csv", "Gateway": "gateway_statement.xlsx", "Bank": "bank_settlement.csv"},
    "scenario3_changed_headers": {"Ledger": "ledger_export.xlsx", "Gateway": "pg_report.csv", "Bank": "account_statement.xlsx"},
}
MAND = {"Ledger": ["TransactionRef", "Amount", "TxnDate"], "Gateway": ["TransactionRef", "Amount", "Fees", "SettlementRef", "TxnDate"],
        "Bank": ["SettlementRef", "Amount", "TxnDate"]}


# ------------------------------------------------------------------------------ helpers
def cell_text(v) -> str:
    """Mirror how the C# importer turns cells into text (numbers via decimal, dates as ISO)."""
    if v is None or (isinstance(v, float) and np.isnan(v)):
        return ""
    if isinstance(v, (pd.Timestamp, datetime)):
        return v.strftime("%Y-%m-%d") if (v.hour, v.minute, v.second) == (0, 0, 0) else v.strftime("%Y-%m-%d %H:%M:%S")
    if isinstance(v, float):
        return str(int(v)) if v == int(v) else repr(v)
    return str(v).strip()


def read_columns(path: Path) -> tuple[list[str], list[list[str]]]:
    df = pd.read_csv(path, dtype=str, keep_default_na=False) if path.suffix == ".csv" else pd.read_excel(path, dtype=object)
    headers = [str(c).strip() for c in df.columns]
    return headers, [[cell_text(v) for v in df[c].tolist()[:30]] for c in df.columns]


def accuracy_by_band(rows):
    out = {}
    for band in ("high", "medium", "low"):
        sel = [r for r in rows if r["band"] == band]
        out[band] = {"columns": len(sel), "accuracy": (sum(r["ok"] for r in sel) / len(sel)) if sel else None}
    return out


def pct(x):
    return "n/a" if x is None else f"{100 * x:.1f}%"


# ------------------------------------------------------------------------------ A. grouped CV
def grouped_cv(mapper: M.ColumnMapper, folds: int = 5) -> dict:
    X, y, groups = mapper.X, mapper.y, np.array(mapper.groups)
    uniq = sorted(set(groups))
    rng = random.Random(11)
    rng.shuffle(uniq)
    fold_of = {g: i % folds for i, g in enumerate(uniq)}
    f = np.array([fold_of[g] for g in groups])
    correct, total, nll = 0, 0, 0.0
    per_class = defaultdict(lambda: [0, 0])
    for k in range(folds):
        tr, te = f != k, f == k
        model = M.SoftmaxRegression(l2=mapper.model.l2).fit(X[tr], y[tr], len(M.LABELS))
        P = model.predict_proba(X[te])
        pred = P.argmax(axis=1)
        correct += int((pred == y[te]).sum())
        total += int(te.sum())
        nll += float(-np.log(np.clip(P[np.arange(te.sum()), y[te]], 1e-9, 1)).sum())
        for t, p in zip(y[te], pred):
            per_class[M.LABELS[t]][1] += 1
            per_class[M.LABELS[t]][0] += int(t == p)
    return {"folds": folds, "rows": total, "distinct_headings": len(uniq), "accuracy": correct / total, "log_loss": nll / total,
            "per_field_recall": {k: v[0] / v[1] for k, v in per_class.items()}}


# ------------------------------------------------------------------------------ B. held-out headings
def baseline_header_only(mapper: M.ColumnMapper, headings: list[str], kind: str) -> list[str | None]:
    """Heading-only fuzzy matching: best known label by WRatio, no values, no one-to-one rule."""
    out = []
    for h in headings:
        best, best_s = None, 0.0
        for li, name in enumerate(M.LABELS):
            if name == "IGNORE" or name not in M.ALLOWED[kind]:
                continue
            _, s = mapper.index.best_alias(h, li)
            if s > best_s:
                best, best_s = name, s
        out.append(best if best_s >= 75 else None)
    return out


def typo(rng: random.Random, heading: str) -> str:
    """One random keyboard-style slip (swap, drop or substitute a letter) inside the longest word."""
    words = heading.split(" ")
    i = max(range(len(words)), key=lambda k: len(words[k]))
    w = words[i]
    if len(w) < 4:
        return heading
    j = rng.randrange(1, len(w) - 1)
    kind = rng.choice(["swap", "drop", "sub"])
    if kind == "swap":
        w = w[:j] + w[j + 1] + w[j] + w[j + 2:]
    elif kind == "drop":
        w = w[:j] + w[j + 1:]
    else:
        w = w[:j] + rng.choice("aeiourstn") + w[j + 1:]
    words[i] = w
    return " ".join(words)


def heldout_headings(mapper: M.ColumnMapper, files_per_kind: int = 150) -> dict:
    pool = HeadingPool(json.loads((HERE / "data" / "eval_headers.json").read_text(encoding="utf-8")))
    rng = random.Random(1001)
    rows, base_ok, base_total = [], 0, 0
    abl = {"headings_only": [0, 0], "values_only": [0, 0], "typos": [0, 0]}
    conf = Counter()
    per_field = defaultdict(lambda: {"tp": 0, "fp": 0, "fn": 0})
    for kind in M.KINDS:
        for _ in range(files_per_kind):
            f = make_file(rng, kind, pool)
            heads = [c.heading for c in f.columns]
            sug = mapper.suggest(kind, heads, [c.values[:30] for c in f.columns])
            base = baseline_header_only(mapper, heads, kind)
            # ablations on the very same files: what do headings alone, values alone, or damaged headings give?
            variants = {
                "headings_only": mapper.suggest(kind, heads, [[] for _ in f.columns]),
                "values_only": mapper.suggest(kind, [f"Column {i + 1}" for i in range(len(heads))], [c.values[:30] for c in f.columns]),
                "typos": mapper.suggest(kind, [typo(rng, h) for h in heads], [c.values[:30] for c in f.columns]),
            }
            for name, got in variants.items():
                for c, g in zip(f.columns, got):
                    abl[name][0] += int(g["field"] == (None if c.label == "IGNORE" else c.label))
                    abl[name][1] += 1
            for c, s, b in zip(f.columns, sug, base):
                truth = None if c.label == "IGNORE" else c.label
                ok = s["field"] == truth
                rows.append({"band": s["band"], "ok": ok, "kind": kind, "heading": c.heading, "truth": truth, "pred": s["field"], "conf": s["confidence"]})
                base_ok += int(b == truth)
                base_total += 1
                if not ok:
                    conf[(truth, s["field"])] += 1
                for name in M.FIELD_LABELS:
                    if s["field"] == name and truth == name: per_field[name]["tp"] += 1
                    elif s["field"] == name: per_field[name]["fp"] += 1
                    elif truth == name: per_field[name]["fn"] += 1
    acc = sum(r["ok"] for r in rows) / len(rows)
    fields = {}
    for name, d in per_field.items():
        p = d["tp"] / (d["tp"] + d["fp"]) if d["tp"] + d["fp"] else None
        r = d["tp"] / (d["tp"] + d["fn"]) if d["tp"] + d["fn"] else None
        fields[name] = {"precision": p, "recall": r, "support": d["tp"] + d["fn"]}
    wrong_headings = Counter((r["heading"], r["truth"], r["pred"]) for r in rows if not r["ok"])
    return {"files": files_per_kind * 3, "columns": len(rows), "accuracy": acc, "baseline_accuracy": base_ok / base_total,
            "ablations": {k: v[0] / v[1] for k, v in abl.items()},
            "by_band": accuracy_by_band(rows), "per_field": fields,
            "top_confusions": [{"truth": t, "predicted": p, "count": n} for (t, p), n in conf.most_common(8)],
            "most_missed_headings": [{"heading": h, "truth": t, "predicted": p, "times": n} for (h, t, p), n in wrong_headings.most_common(12)]}


# ------------------------------------------------------------------------------ C. demo datasets
def demo_datasets(mapper: M.ColumnMapper) -> dict:
    results, latencies, all_rows = {}, [], []
    for scenario, files in SCENARIOS.items():
        truth = defaultdict(dict)
        with (ROOT / "data" / scenario / "ground_truth_mapping.csv").open(encoding="utf-8") as fh:
            for r in csv.DictReader(fh):
                truth[r["Source"]][r["Header"]] = None if r["Expected_Canonical_Field"] == "IGNORE" else r["Expected_Canonical_Field"]
        per_kind = {}
        for kind, fname in files.items():
            headers, values = read_columns(ROOT / "data" / scenario / fname)
            t0 = time.perf_counter()
            sug = mapper.suggest(kind, headers, values)
            latencies.append((time.perf_counter() - t0) * 1000)
            exp = truth[kind.lower()]
            cols = []
            for s in sug:
                ok = s["field"] == exp[s["header"]]
                row = {"header": s["header"], "expected": exp[s["header"]], "suggested": s["field"], "confidence": s["confidence"], "band": s["band"], "ok": ok}
                cols.append(row)
                all_rows.append(row)
            mand_ok = all(any(c["suggested"] == m and c["expected"] == m for c in cols) for m in MAND[kind])
            per_kind[kind] = {"file": fname, "columns": cols, "accuracy": sum(c["ok"] for c in cols) / len(cols), "required_fields_all_correct": mand_ok}
        results[scenario] = per_kind
    return {"scenarios": results, "columns": len(all_rows), "accuracy": sum(r["ok"] for r in all_rows) / len(all_rows),
            "by_band": accuracy_by_band(all_rows), "latencies_ms": latencies,
            "wrong": [{**r} for r in all_rows if not r["ok"]]}


# ------------------------------------------------------------------------------ report
def rss_mb() -> float | None:
    try:
        import psutil  # optional
        return round(psutil.Process(os.getpid()).memory_info().rss / 1_048_576, 1)
    except Exception:  # noqa: BLE001
        return None


def render_markdown(r: dict) -> str:
    info, cv, ho, demo = r["model"], r["grouped_cv"], r["heldout"], r["demo"]
    lat = sorted(demo["latencies_ms"])
    L = []
    L.append("# Step 4 - AI column mapping: measured results\n")
    L.append(f"Generated by `ai/eval_mapping.py` on {r['generated']}. Synthetic data only. Failures are listed, not hidden.\n")
    L.append("## What was measured\n")
    L.append(f"- **Model:** `{info['name']}` version `{info['version']}` - {info['algorithm']}. Trained at start-up on {info['training_rows']:,} columns from {info['training_files']} synthetic files "
             f"built from {info['corpus_headings']} author-written headings. No pre-trained weights; no LLM. Licences: RapidFuzz MIT, NumPy/SciPy BSD-3.")
    L.append("- **Which parts are model-assisted:** the per-column scores. **Deterministic, not model:** the one-to-one assignment, per-source masks, confidence bands and the reasons text.")
    L.append("- **Leakage control:** the heading strings used for evaluation (held-out set and the demo datasets) never appear in the training corpus (asserted by tests).\n")
    L.append("## A. Grouped cross-validation (headings held out together)\n")
    L.append(f"{cv['rows']:,} columns, {cv['distinct_headings']} distinct headings, {cv['folds']} folds: **accuracy {pct(cv['accuracy'])}**, log-loss {cv['log_loss']:.3f}. "
             "Slightly optimistic: held-out headings still sit in the similarity index of the other folds.\n")
    L.append("## B. Held-out evaluation headings in fresh synthetic files\n")
    L.append(f"{ho['columns']:,} columns in {ho['files']} files. **Full ranker: {pct(ho['accuracy'])}** vs **heading-only fuzzy baseline: {pct(ho['baseline_accuracy'])}**.\n")
    L.append("| Field | Precision | Recall | Columns |\n|---|---|---|---|")
    for k, v in ho["per_field"].items():
        L.append(f"| {k} | {pct(v['precision'])} | {pct(v['recall'])} | {v['support']} |")
    ab = ho["ablations"]
    L.append("\n**Stress tests on the same files** (what happens when part of the evidence is missing or damaged):\n")
    L.append("| Condition | Accuracy |\n|---|---|")
    L.append(f"| Full ranker (headings and values) | {pct(ho['accuracy'])} |")
    L.append(f"| Headings only (no values supplied) | {pct(ab['headings_only'])} |")
    L.append(f"| Values only (headings replaced by 'Column N') | {pct(ab['values_only'])} |")
    L.append(f"| Headings with a typo (swap / drop / substitute a letter) | {pct(ab['typos'])} |")
    L.append(f"| Heading-only fuzzy-matching baseline (no model, no values) | {pct(ho['baseline_accuracy'])} |")
    L.append("\n| Confidence band | Columns | Accuracy |\n|---|---|---|")
    for b, v in ho["by_band"].items():
        L.append(f"| {b} | {v['columns']} | {pct(v['accuracy'])} |")
    if ho["most_missed_headings"]:
        L.append("\nMost frequently missed headings:\n")
        L.append("| Heading | Expected | Suggested | Times |\n|---|---|---|---|")
        for m in ho["most_missed_headings"]:
            L.append(f"| {m['heading']} | {m['truth'] or 'IGNORE'} | {m['predicted'] or 'IGNORE'} | {m['times']} |")
    L.append("\n## C. The real demo datasets (never trained on)\n")
    L.append(f"{demo['columns']} columns across 3 scenarios x 3 sources: **{pct(demo['accuracy'])} correct** (top suggestion equals the human ground truth).\n")
    L.append("| Scenario | Source | File | Accuracy | Required fields all right |\n|---|---|---|---|---|")
    for scn, kinds in demo["scenarios"].items():
        for kind, d in kinds.items():
            L.append(f"| {scn} | {kind} | {d['file']} | {pct(d['accuracy'])} | {'yes' if d['required_fields_all_correct'] else '**NO**'} |")
    if demo["wrong"]:
        L.append("\nWrong suggestions on the demo data:\n")
        L.append("| Heading | Expected | Suggested | Confidence |\n|---|---|---|---|")
        for w in demo["wrong"]:
            L.append(f"| {w['header']} | {w['expected'] or 'IGNORE'} | {w['suggested'] or 'IGNORE'} | {pct(w['confidence'])} |")
    else:
        L.append("\nNo wrong suggestions on the demo data.")
    L.append("\n## D. Runtime and memory\n")
    L.append(f"- Training at start-up: {info['train_seconds']} s. Suggestion latency over {len(lat)} real files: median {statistics.median(lat):.0f} ms, max {max(lat):.0f} ms.")
    L.append(f"- Process memory after loading and evaluation: {r['rss_mb'] if r['rss_mb'] is not None else 'not measured (psutil not installed)'} MB.\n")
    L.append("## Limitations\n")
    L.append("- Training and evaluation headings were all written by the same author, so real-world headings from other companies may behave differently.")
    L.append("- Only seven canonical fields; unusual layouts (several date columns, multi-line headers, merged cells) are not modelled.")
    L.append("- Suggestions are never applied automatically: an operator must review and confirm every mapping.")
    return "\n".join(L) + "\n"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    args = ap.parse_args()

    mapper = M.ColumnMapper()
    info = mapper.info
    print(f"model {info.name} v{info.version}: {info.training_rows} rows, trained in {info.train_seconds}s")
    result = {
        "generated": datetime.now().strftime("%d %b %Y"),
        "model": {"name": info.name, "version": info.version, "algorithm": info.algorithm, "training_rows": info.training_rows,
                  "training_files": info.training_files, "corpus_headings": info.corpus_headings, "train_seconds": info.train_seconds},
        "grouped_cv": grouped_cv(mapper),
        "heldout": heldout_headings(mapper),
        "demo": demo_datasets(mapper),
    }
    result["rss_mb"] = rss_mb()
    md = render_markdown(result)
    print(md)
    if args.write:
        (ROOT / "docs" / "results").mkdir(parents=True, exist_ok=True)
        (ROOT / "docs" / "results" / "step4_column_mapping.md").write_text(md, encoding="utf-8")
        (HERE / "results").mkdir(exist_ok=True)
        slim = {k: v for k, v in result.items()}
        slim["demo"] = {k: v for k, v in result["demo"].items() if k != "latencies_ms"}
        (HERE / "results" / "mapping_eval.json").write_text(json.dumps(slim, indent=2, default=float), encoding="utf-8")
        print("written: docs/results/step4_column_mapping.md, ai/results/mapping_eval.json")


if __name__ == "__main__":
    main()
