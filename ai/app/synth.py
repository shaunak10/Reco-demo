"""
Synthetic source files for training and evaluating the column ranker.

Headings come from a corpus (training or held-out); values come from the generators below, which imitate the *shape*
of real recharge / gateway / bank data: unique order references, payout references shared by many rows, dates in several
formats, amounts with or without decimals, fees that are a small fraction of the amount, running balances, all-zero debit
columns, free-text narrations and so on. Everything is synthetic and seeded.
"""
from __future__ import annotations

import random
import string
from dataclasses import dataclass
from datetime import date, datetime, timedelta

from .text import normalize

FIELD_LABELS = ["TransactionRef", "Amount", "Currency", "TxnDate", "SettlementRef", "Fees", "Status"]

PLAN_AMOUNTS = [149, 199, 239, 299, 349, 479, 599, 719, 999, 1499, 2399, 2999]
OPERATORS = ["Jio", "Airtel", "Vi", "BSNL", "Tata Play", "Dish TV", "Sun Direct"]
FIRST = ["Aarav", "Vivaan", "Aditi", "Diya", "Rohan", "Sneha", "Karthik", "Meera", "Imran", "Priya", "Arjun", "Kavya", "Nisha", "Rahul"]
LAST = ["Sharma", "Iyer", "Khan", "Patel", "Reddy", "Nair", "Singh", "Das", "Joshi", "Mehta", "Gupta", "Rao"]
CATEGORIES = ["Mobile Prepaid", "DTH", "Postpaid", "Data Card", "Electricity", "Broadband", "FASTag"]
MODES = ["UPI", "Card", "NetBanking", "Wallet", "Debit Card", "Credit Card", "HDFC Bank", "ICICI Bank", "SBI", "Axis Bank"]
STATUS_VOCABS = [
    ["SUCCESS", "FAILED"], ["CAPTURED", "AUTHORIZED", "REFUNDED"], ["Settled", "Pending"], ["PAID", "FAILED", "PENDING"],
    ["Completed", "Failed", "Pending"], ["success", "failed"], ["SETTLED"], ["Captured", "Refunded"],
]


@dataclass
class SynthColumn:
    heading: str
    label: str          # one of FIELD_LABELS or "IGNORE"
    values: list[str]
    group: str = ""     # generator group for IGNORE columns


@dataclass
class SynthFile:
    kind: str
    columns: list[SynthColumn]


# ---------------------------------------------------------------------------------- primitives
def _letters(rng: random.Random, n: int) -> str:
    return "".join(rng.choice(string.ascii_uppercase) for _ in range(n))


def _digits(rng: random.Random, n: int) -> str:
    return "".join(rng.choice(string.digits) for _ in range(n))


def _fmt_money(x: float, style: str) -> str:
    if style == "int" and abs(x - round(x)) < 1e-9:
        return str(int(round(x)))
    if style == "comma":
        return f"{x:,.2f}"
    return f"{x:.2f}"


def _day_list(rng: random.Random, n: int) -> list[date]:
    start = date(2026, 1, 1) + timedelta(days=rng.randint(0, 330))
    span = rng.randint(2, 6)
    return sorted(start + timedelta(days=rng.randint(0, span)) for _ in range(n))


def _fmt_date(d: date, style: str, rng: random.Random) -> str:
    t = datetime(d.year, d.month, d.day, rng.randint(0, 23), rng.randint(0, 59), rng.randint(0, 59))
    return {
        "iso": d.strftime("%Y-%m-%d"),
        "slash": d.strftime("%d/%m/%Y"),
        "mon": d.strftime("%d-%b-%Y"),
        "iso_time": t.strftime("%Y-%m-%d %H:%M:%S"),
        "slash_time": t.strftime("%d/%m/%Y %H:%M:%S"),
        "spaced": d.strftime("%d %b %Y"),
    }[style]


DATE_STYLES = ["iso", "slash", "mon", "iso_time", "slash_time", "spaced"]


def gen_txn_refs(rng: random.Random, n: int, days: list[date]) -> list[str]:
    style = rng.randint(1, 6)
    pfx = _letters(rng, rng.randint(2, 4))
    out, seen = [], set()
    for i in range(n):
        d = days[i % len(days)]
        if style == 1:
            v = f"{pfx}-{d:%y%m%d}-{i + 1:04d}"
        elif style == 2:
            v = f"{pfx}{d:%Y%m%d}{i + 1:05d}"
        elif style == 3:
            v = _digits(rng, rng.randint(10, 14))
        elif style == 4:
            v = f"{pfx}_{''.join(rng.choice('0123456789abcdef') for _ in range(8))}"
        elif style == 5:
            v = f"{pfx}/{d.year}/{i + 1:05d}"
        else:
            v = "".join(rng.choice(string.ascii_uppercase + string.digits) for _ in range(rng.randint(12, 20)))
        while v in seen:
            v += rng.choice(string.digits)
        seen.add(v)
        out.append(v)
    return out


def gen_settlement_refs(rng: random.Random, n: int, days: list[date], shared: bool) -> list[str]:
    style = rng.randint(1, 5)
    pfx = _letters(rng, rng.randint(2, 4))
    bank = rng.choice(["HDFC", "ICIC", "SBIN", "UTIB", "KKBK", "YESB"])
    groups = max(1, n // rng.randint(3, 10)) if shared else n

    def one(k: int) -> str:
        d = days[k % len(days)]
        if style == 1:
            return f"UTR{pfx}{d:%y%m%d}{k + 1:02d}"
        if style == 2:
            return f"{bank}N5{d:%Y%m%d}{_digits(rng, 5)}"
        if style == 3:
            return _digits(rng, 12)
        if style == 4:
            return f"SB-{d:%Y%m%d}-{string.ascii_uppercase[k % 26]}"
        return f"{pfx}{d:%Y%m%d}{k + 1:03d}"

    pool = [one(k) for k in range(groups)]
    if not shared:
        seen, out = set(), []
        for v in pool:
            while v in seen:
                v += rng.choice(string.digits)
            seen.add(v)
            out.append(v)
        return out
    return [rng.choice(pool) for _ in range(n)]


# ---------------------------------------------------------------------------------- file builder
@dataclass
class _Money:
    amounts: list[float]
    fees: list[float]


def _amounts(rng: random.Random, kind: str, n: int, scale: float, cents: bool) -> list[float]:
    out = []
    for _ in range(n):
        if kind == "Bank":
            x = sum(rng.choice(PLAN_AMOUNTS) for _ in range(rng.randint(5, 12))) * scale * rng.uniform(0.96, 0.99)
        else:
            x = rng.choice(PLAN_AMOUNTS) * scale
            if cents:
                x *= rng.uniform(0.9, 1.1)
        out.append(round(x, 2) if (cents or kind == "Bank") else float(round(x)))
    return out


def _ignore_values(rng: random.Random, group: str, n: int, ctx: dict) -> list[str]:
    amounts, fees, scale = ctx["amounts"], ctx["fees"], ctx["scale"]
    style = ctx["money_style"]
    if group == "name":
        return [f"{rng.choice(FIRST)} {rng.choice(LAST)}" for _ in range(n)]
    if group == "mobile":
        return [str(rng.randint(6, 9)) + _digits(rng, 9) for _ in range(n)]
    if group == "email":
        return [f"{rng.choice(FIRST).lower()}{rng.randint(1, 99)}@{rng.choice(['mail.com', 'example.org', 'demo.in'])}" for _ in range(n)]
    if group == "operator":
        return [rng.choice(OPERATORS) for _ in range(n)]
    if group == "plan":
        return [f"Rs {rng.choice(PLAN_AMOUNTS)} - {rng.choice([28, 56, 84, 365])} days" for _ in range(n)]
    if group == "narration":
        return [f"NEFT CR-{_letters(rng, 6)}-SETTLEMENT {_digits(rng, rng.randint(4, 9))}" if rng.random() < 0.7
                else f"UPI/{_digits(rng, 10)}/{rng.choice(FIRST)}" for _ in range(n)]
    if group == "mode":
        return [rng.choice(MODES) if rng.random() < 0.85 else f"{_letters(rng, 4)}0{_digits(rng, 6)}" for _ in range(n)]
    if group == "balance":
        bal = rng.uniform(20_000, 900_000) * scale
        out = []
        for a in amounts[:n]:
            bal += a
            out.append(_fmt_money(bal, style if style != "int" else "2dp"))
        return out
    if group == "debit_zero":
        zero = rng.choice(["0.00", "0", "0.00"])
        return [zero if rng.random() < 0.95 else _fmt_money(rng.uniform(1, 50), "2dp") for _ in range(n)]
    if group == "net_money":
        return [_fmt_money(a - f, "2dp") for a, f in zip(amounts[:n], fees[:n])]
    if group == "serial":
        return [str(i + 1) for i in range(n)]
    if group == "internal_id":
        sty = rng.randint(1, 3)
        pfx = _letters(rng, 3)
        if sty == 1:
            return [f"{pfx}-{i + 1:06d}" for i in range(n)]
        if sty == 2:
            return ["".join(rng.choice("0123456789abcdef") for _ in range(12)) for _ in range(n)]
        return [f"{pfx}{_digits(rng, 8)}" for _ in range(n)]
    if group == "other_date":
        days = [d + timedelta(days=rng.randint(1, 3)) for d in ctx["days"]]
        return [_fmt_date(days[i % len(days)], ctx["date_style"], rng) for i in range(n)]
    if group == "text_short":
        return [rng.choice(CATEGORIES + ["Karnataka", "Delhi NCR", "Mumbai", "web", "app"]) for _ in range(n)]
    if group == "ip":
        return [f"{rng.randint(10, 200)}.{rng.randint(0, 255)}.{rng.randint(0, 255)}.{rng.randint(1, 254)}" if rng.random() < 0.6
                else "".join(rng.choice("0123456789abcdef") for _ in range(16)) for _ in range(n)]
    if group == "tax_money":
        base = [round(f * 0.18, 2) for f in fees[:n]] if rng.random() < 0.5 else [round(rng.choice([0, 0, 5, 10, 20, 50]) * 1.0, 2) for _ in range(n)]
        return [_fmt_money(x, "2dp") for x in base]
    if group == "code":
        sty = rng.randint(1, 3)
        if sty == 1:
            return [rng.choice(["00", "00", "00", "05", "91", "51"]) for _ in range(n)]
        if sty == 2:
            return [_digits(rng, 6) for _ in range(n)]
        return [rng.choice(["APPROVED", "DECLINED", "TIMEOUT"]) for _ in range(n)]
    raise ValueError(f"unknown ignore group {group}")


_TEMPLATES = {
    "Ledger": dict(
        required=["TransactionRef", "Amount", "TxnDate"], optional=[("Status", 0.7), ("Currency", 0.15)],
        ignores=[("name", 0.4), ("mobile", 0.35), ("operator", 0.5), ("plan", 0.4), ("mode", 0.5), ("serial", 0.4),
                 ("internal_id", 0.5), ("text_short", 0.35), ("email", 0.1), ("tax_money", 0.1)]),
    "Gateway": dict(
        required=["TransactionRef", "Amount", "Fees", "SettlementRef", "TxnDate"], optional=[("Status", 0.8), ("Currency", 0.15)],
        ignores=[("net_money", 0.65), ("other_date", 0.6), ("internal_id", 0.6), ("serial", 0.2), ("mode", 0.4),
                 ("code", 0.3), ("ip", 0.1), ("tax_money", 0.3), ("text_short", 0.2)]),
    "Bank": dict(
        required=["SettlementRef", "Amount", "TxnDate"], optional=[("Status", 0.1), ("Currency", 0.15)],
        ignores=[("narration", 0.85), ("balance", 0.8), ("debit_zero", 0.75), ("serial", 0.2), ("internal_id", 0.1),
                 ("mode", 0.2), ("text_short", 0.1)]),
}


class HeadingPool:
    """Headings grouped by label from a corpus file (training or evaluation)."""

    def __init__(self, corpus: dict):
        self.by_label = {k: list(v) for k, v in corpus.items() if k in FIELD_LABELS}
        self.ignore_by_group: dict[str, list[str]] = {}
        for item in corpus["IGNORE"]:
            self.ignore_by_group.setdefault(item["g"], []).append(item["h"])

    def pick(self, rng: random.Random, label: str) -> str:
        return rng.choice(self.by_label[label])

    def pick_ignore(self, rng: random.Random, group: str) -> str | None:
        pool = self.ignore_by_group.get(group)
        return rng.choice(pool) if pool else None


def make_file(rng: random.Random, kind: str, pool: HeadingPool) -> SynthFile:
    tpl = _TEMPLATES[kind]
    n = rng.randint(10, 70)
    scale = round(rng.uniform(0.3, 8.0), 2)
    cents = rng.random() < 0.4
    money_style = rng.choice(["2dp", "2dp", "int", "comma"])
    date_style = rng.choice(DATE_STYLES)
    days = _day_list(rng, n)
    amounts = _amounts(rng, kind, n, scale, cents)
    fee_rate = rng.uniform(0.008, 0.035)
    fees = [round(a * fee_rate, 2) for a in amounts]
    ctx = dict(amounts=amounts, fees=fees, scale=scale, money_style=money_style, days=days, date_style=date_style)

    cols: list[SynthColumn] = []
    wanted = list(tpl["required"]) + [f for f, p in tpl["optional"] if rng.random() < p]
    for label in wanted:
        if label == "TransactionRef":
            values = gen_txn_refs(rng, n, days)
        elif label == "SettlementRef":
            values = gen_settlement_refs(rng, n, days, shared=(kind == "Gateway"))
        elif label == "Amount":
            values = [_fmt_money(a, money_style) for a in amounts]
        elif label == "Fees":
            values = [_fmt_money(f, "2dp") for f in fees]
        elif label == "TxnDate":
            values = [_fmt_date(days[i], date_style, rng) for i in range(n)]
        elif label == "Currency":
            values = [rng.choice(["INR"] * 9 + ["Rs"]) for _ in range(n)]
        else:  # Status
            vocab = rng.choice(STATUS_VOCABS)
            values = [rng.choice(vocab) for _ in range(n)]
        cols.append(SynthColumn(pool.pick(rng, label), label, values))

    groups = [g for g, p in tpl["ignores"] if rng.random() < p]
    if len(groups) < 2:
        groups += [g for g, _ in rng.sample(tpl["ignores"], 2 - len(groups))]
    taken = {normalize(c.heading) for c in cols}
    for g in dict.fromkeys(groups):
        h = pool.pick_ignore(rng, g)
        if h is None or normalize(h) in taken:
            continue
        taken.add(normalize(h))
        cols.append(SynthColumn(h, "IGNORE", _ignore_values(rng, g, n, ctx), group=g))

    rng.shuffle(cols)
    return SynthFile(kind, cols)
