"""Value profiling: measurable statistics about the sample values of one column (no values are stored or echoed)."""
from __future__ import annotations

import math
import re
from collections import Counter
from datetime import datetime

import numpy as np

_CURRENCY_PREFIX = re.compile(r"^(?:₹|INR|Rs\.?)\s*", re.IGNORECASE)
# Same grouping rules as the C# importer: 1234.50 | 1,234.50 | 12,34,567.00 (Indian). Never 12,34 or 1e3.
_MONEY = re.compile(r"^(?:\d+|\d{1,3}(?:,\d{3})+|\d{1,2}(?:,\d{2})*,\d{3})(?:\.\d{1,2})?$")
_DATE_FORMATS = (
    "%Y-%m-%d", "%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M:%S", "%d/%m/%Y", "%d/%m/%Y %H:%M:%S", "%d/%m/%Y %H:%M",
    "%d-%m-%Y", "%d-%b-%Y", "%d-%b-%Y %H:%M:%S", "%d %b %Y", "%b %d, %Y", "%d/%m/%y",
)
_DATE_SHAPE = re.compile(r"^\d{1,4}[-/ ]\w{1,9}[-/ ,]+\d{1,4}")
_PHONE = re.compile(r"^(?:\+?91[- ]?)?[6-9]\d{9}$")
_EMAIL = re.compile(r"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")

NAMES = [
    "frac_empty", "frac_money", "frac_int_digits", "frac_two_dec", "frac_date", "frac_cur3",
    "distinct_ratio", "log_distinct", "frac_wordlike", "mean_len", "len_cv", "frac_ref_like", "frac_space",
    "mean_words", "frac_phone", "frac_email", "prefix_consistency", "frac_sep", "frac_upper_alpha",
    "m_log_mean", "m_log_max", "m_zero_frac", "m_mono", "m_cv", "m_dec_frac",
]


def parse_money(text: str) -> float | None:
    s = _CURRENCY_PREFIX.sub("", text.strip())
    if _MONEY.match(s):
        try:
            return float(s.replace(",", ""))
        except ValueError:
            return None
    return None


def is_date(text: str) -> bool:
    s = text.strip()
    if not _DATE_SHAPE.match(s):
        return False
    for fmt in _DATE_FORMATS:
        try:
            datetime.strptime(s, fmt)
            return True
        except ValueError:
            continue
    return False


def profile(values: list[str]) -> tuple[np.ndarray, dict]:
    """Returns (feature vector aligned with NAMES, side statistics for explanations)."""
    cleaned = [(v or "").strip() for v in values]
    n = len(cleaned)
    present = [v for v in cleaned if v != ""]
    m = len(present)
    f = dict.fromkeys(NAMES, 0.0)
    stats = {"n": n, "present": m, "distinct": 0, "money_mean": 0.0}
    f["frac_empty"] = 1.0 - (m / n if n else 0.0)
    if m == 0:
        return np.array([f[k] for k in NAMES], dtype=float), stats

    money = [parse_money(v) for v in present]
    money_vals = [x for x in money if x is not None]
    lens = np.array([len(v) for v in present], dtype=float)
    distinct = len(set(present))
    stats["distinct"] = distinct

    f["frac_money"] = len(money_vals) / m
    f["frac_int_digits"] = sum(v.isdigit() for v in present) / m
    f["frac_two_dec"] = sum(bool(re.search(r"\.\d{2}$", v)) for v in present) / m
    f["frac_date"] = sum(is_date(v) for v in present) / m
    f["frac_cur3"] = sum(bool(re.fullmatch(r"[A-Za-z]{3}|Rs\.?|₹", v)) for v in present) / m
    f["distinct_ratio"] = distinct / m
    f["log_distinct"] = math.log1p(distinct)
    f["frac_wordlike"] = sum(bool(re.fullmatch(r"[A-Za-z][A-Za-z .&'/_-]*", v)) for v in present) / m
    f["mean_len"] = float(lens.mean())
    f["len_cv"] = float(lens.std() / lens.mean()) if lens.mean() else 0.0
    f["frac_ref_like"] = sum(
        (" " not in v) and 5 <= len(v) <= 32 and any(c.isdigit() for c in v) and (any(c.isalpha() for c in v) or len(v) >= 8)
        for v in present
    ) / m
    f["frac_space"] = sum(" " in v for v in present) / m
    f["mean_words"] = float(np.mean([len(v.split()) for v in present]))
    f["frac_phone"] = sum(bool(_PHONE.match(v)) for v in present) / m
    f["frac_email"] = sum(bool(_EMAIL.match(v)) for v in present) / m
    prefixes = Counter(v[:3] for v in present if len(v) >= 3)
    f["prefix_consistency"] = (prefixes.most_common(1)[0][1] / m) if prefixes else 0.0
    f["frac_sep"] = sum(any(c in v for c in "-/_") for v in present) / m
    f["frac_upper_alpha"] = sum(v.isalpha() and v.isupper() for v in present) / m

    if f["frac_money"] >= 0.6 and money_vals:
        arr = np.array(money_vals, dtype=float)
        mean = float(arr.mean())
        stats["money_mean"] = mean
        f["m_log_mean"] = math.log10(mean + 1.0)
        f["m_log_max"] = math.log10(float(arr.max()) + 1.0)
        f["m_zero_frac"] = float((arr == 0).mean())
        if len(arr) >= 3:
            f["m_mono"] = float((np.diff(arr) > 0).mean())
        f["m_cv"] = float(arr.std() / mean) if mean > 0 else 0.0
        f["m_dec_frac"] = float((np.abs(arr - np.round(arr)) > 1e-9).mean())
    return np.array([f[k] for k in NAMES], dtype=float), stats


def file_context(stats_list: list[dict], profiles: list[np.ndarray]) -> list[np.ndarray]:
    """Per-column features that depend on the sibling columns: relative size among the money columns."""
    money_idx = [i for i, p in enumerate(profiles) if p[NAMES.index("frac_money")] >= 0.8 and stats_list[i]["money_mean"] > 0]
    means = {i: stats_list[i]["money_mean"] for i in money_idx}
    biggest = max(means.values()) if means else 0.0
    order = sorted(money_idx, key=lambda i: means[i])
    out = []
    for i in range(len(profiles)):
        if i in means and biggest > 0:
            rel = means[i] / biggest
            rank = (order.index(i) / (len(order) - 1)) if len(order) > 1 else 0.5
        else:
            rel, rank = 0.0, 0.0
        out.append(np.array([rel, rank, min(len(money_idx), 5) / 5.0]))
    return out
