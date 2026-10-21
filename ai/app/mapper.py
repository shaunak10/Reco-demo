"""
Column ranker for AI-assisted field mapping (Step 4).

What is model-assisted here, and what is not
  * MODEL: a multinomial logistic regression (trained at start-up, fixed seed) that scores every column against the seven
    canonical fields plus IGNORE. Its inputs are (a) similarity between the column heading and a corpus of alternative
    labels (RapidFuzz token/ratio similarity and character n-gram TF-IDF cosine), (b) measurable properties of the
    sample values (share that parse as dates / amounts, uniqueness, running-balance behaviour, relative size among the
    money columns, ...) and (c) the source type.
  * NOT a model: the one-to-one assignment (Hungarian algorithm), the per-source masks (a bank file never offers Fees),
    the confidence bands and the human-readable reasons, which only describe measured evidence.
  * Not an LLM and not a pre-trained network. The training data is a small synthetic, author-written corpus; see
    ai/MODEL_CARD.md for evaluation and limitations. The model is implemented with NumPy/SciPy because this machine's
    Application Control policy blocks parts of scikit-learn's compiled extensions.
"""
from __future__ import annotations

import hashlib
import json
import math
import random
import time
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from rapidfuzz import fuzz, process
from scipy import sparse
from scipy.optimize import linear_sum_assignment, minimize
from scipy.special import logsumexp, softmax

from . import profile as prof
from .synth import FIELD_LABELS, HeadingPool, make_file
from .text import normalize

LABELS = FIELD_LABELS + ["IGNORE"]
IGNORE = len(LABELS) - 1
KINDS = ["Ledger", "Gateway", "Bank"]
ALLOWED = {
    "Ledger": {"TransactionRef", "Amount", "TxnDate", "Currency", "Status"},
    "Gateway": {"TransactionRef", "Amount", "Fees", "SettlementRef", "TxnDate", "Currency", "Status"},
    "Bank": {"SettlementRef", "Amount", "TxnDate", "Currency", "Status"},
}
FIELD_TITLES = {
    "TransactionRef": "transaction reference", "Amount": "amount", "Currency": "currency", "TxnDate": "transaction date",
    "SettlementRef": "settlement reference", "Fees": "fees", "Status": "status",
}
DATA_DIR = Path(__file__).absolute().parent.parent / "data"
MODEL_NAME = "reconai-column-ranker"
CODE_VERSION = "1"
TAU_ASSIGN = 0.30          # minimum probability for a column to receive a field
BANDS = ((0.80, "high"), (0.50, "medium"), (0.0, "low"))


CACHE_DIR = Path(__file__).absolute().parent.parent / ".cache"


def _version(corpus: dict, seed: int, files_per_kind: int, l2: float) -> str:
    """Changes whenever the corpus, the hyper-parameters or ANY module that shapes the model changes."""
    h = hashlib.sha256(json.dumps(corpus, sort_keys=True).encode())
    h.update(f"|{seed}|{files_per_kind}|{l2}|{CODE_VERSION}".encode())
    for module in ("mapper.py", "profile.py", "synth.py", "text.py"):
        h.update((Path(__file__).absolute().parent / module).read_bytes())
    return h.hexdigest()[:8]


def load_corpus(path: Path | None = None) -> dict:
    return json.loads((path or DATA_DIR / "header_corpus.json").read_text(encoding="utf-8"))


def _ngrams(text: str, lo: int = 2, hi: int = 4) -> list[str]:
    padded = f" {text} "
    return [padded[i:i + n] for n in range(lo, hi + 1) for i in range(len(padded) - n + 1)]


# ======================================================================================= heading index
class HeadingIndex:
    """Known labels per field (from the training corpus) with fuzzy and character n-gram TF-IDF similarity."""

    def __init__(self, corpus: dict):
        seen: set[tuple[str, int]] = set()
        norms: list[str] = []
        labels: list[int] = []

        def add(heading: str, label: int) -> None:
            n = normalize(heading)
            if n and (n, label) not in seen:
                seen.add((n, label))
                norms.append(n)
                labels.append(label)

        for li, name in enumerate(FIELD_LABELS):
            for h in corpus[name]:
                add(h, li)
        for item in corpus["IGNORE"]:
            add(item["h"], IGNORE)

        self.norms = norms
        self.labels = np.array(labels)
        self.by_norm: dict[str, list[int]] = {}
        for i, n in enumerate(norms):
            self.by_norm.setdefault(n, []).append(i)

        grams = [_ngrams(n) for n in norms]
        df = Counter(g for gs in grams for g in set(gs))
        self.vocab = {g: i for i, g in enumerate(df)}
        n_docs = len(norms)
        self.idf = np.array([math.log((1 + n_docs) / (1 + df[g])) + 1.0 for g in self.vocab])
        self.matrix = sparse.vstack([self._vector(gs) for gs in grams]).tocsr()

        self.tok_label = [Counter() for _ in LABELS]
        self.tok_total: Counter = Counter()
        for n, label in zip(norms, labels):
            for t in set(n.split()):
                self.tok_label[label][t] += 1
                self.tok_total[t] += 1

    def _vector(self, grams: list[str]) -> sparse.csr_matrix:
        counts = Counter(g for g in grams if g in self.vocab)
        if not counts:
            return sparse.csr_matrix((1, len(self.vocab)))
        cols = np.array([self.vocab[g] for g in counts])
        data = np.array([(1.0 + math.log(c)) * self.idf[j] for c, j in zip(counts.values(), cols)])
        data /= np.linalg.norm(data)
        return sparse.csr_matrix((data, (np.zeros(len(cols), dtype=int), cols)), shape=(1, len(self.vocab)))

    @property
    def n_features(self) -> int:
        return len(LABELS) * 3 + len(LABELS) + 2

    def features(self, heading: str, exclude_self: bool = False) -> np.ndarray:
        """
        Per label: best token-set similarity, best plain similarity, best n-gram cosine; per label: keyword purity;
        plus how many words are known. With exclude_self the heading's own corpus entry is hidden (leave-one-out), so
        training sees the same 'never seen this exact heading' situation as a real upload.
        """
        n = normalize(heading)
        excluded = set(self.by_norm.get(n, [])) if exclude_self else set()
        keep = np.ones(len(self.norms), dtype=bool)
        for i in excluded:
            keep[i] = False

        sets = process.cdist([n], self.norms, scorer=fuzz.token_set_ratio)[0] / 100.0
        ratio = process.cdist([n], self.norms, scorer=fuzz.ratio)[0] / 100.0
        cos = np.asarray((self.matrix @ self._vector(_ngrams(n)).T).todense()).ravel()

        feats: list[float] = []
        for li in range(len(LABELS)):
            idx = (self.labels == li) & keep
            feats += [float(sets[idx].max()), float(ratio[idx].max()), float(cos[idx].max())] if idx.any() else [0.0, 0.0, 0.0]

        adj_label: Counter = Counter()
        adj_total: Counter = Counter()
        for i in excluded:
            for t in set(self.norms[i].split()):
                adj_label[(int(self.labels[i]), t)] += 1
                adj_total[t] += 1
        toks = n.split()
        known = 0
        purity = [0.0] * len(LABELS)
        for t in toks:
            total = self.tok_total[t] - adj_total[t]
            if total <= 0:
                continue
            known += 1
            for li in range(len(LABELS)):
                purity[li] += (self.tok_label[li][t] - adj_label[(li, t)]) / total
        denom = max(1, len(toks))
        feats += [p / denom for p in purity]
        feats += [known / denom, min(len(toks), 6) / 6.0]
        return np.array(feats, dtype=float)

    def best_alias(self, heading: str, label: int) -> tuple[str, float]:
        """Closest known label for a field and its similarity (0-100), used only to explain a suggestion."""
        idx = np.where(self.labels == label)[0]
        if len(idx) == 0:
            return "", 0.0
        n = normalize(heading)
        choices = [self.norms[i] for i in idx]
        match = process.extractOne(n, choices, scorer=fuzz.WRatio)
        return (match[0], float(match[1])) if match else ("", 0.0)


# ======================================================================================= features
def featurize_file(index: HeadingIndex, kind: str, headings: list[str], values: list[list[str]],
                   exclude_self: bool = False, cache: dict | None = None) -> tuple[np.ndarray, list[dict], list[dict]]:
    """Feature matrix (one row per column) plus per-column side statistics and named value features."""
    profiles, stats = [], []
    for col in values:
        p, s = prof.profile(col)
        profiles.append(p)
        stats.append(s)
    context = prof.file_context(stats, profiles)
    kind_vec = np.array([1.0 if kind == k else 0.0 for k in KINDS])
    rows = []
    for i, heading in enumerate(headings):
        key = normalize(heading)
        if cache is not None and exclude_self and key in cache:
            hf = cache[key]
        else:
            hf = index.features(heading, exclude_self)
            if cache is not None and exclude_self:
                cache[key] = hf
        rows.append(np.concatenate([hf, profiles[i], context[i], kind_vec]))
    named = [dict(zip(prof.NAMES, p)) | {"rel_mean": float(context[i][0]), "money_rank": float(context[i][1])}
             for i, p in enumerate(profiles)]
    return np.vstack(rows), stats, named


# ======================================================================================= model
class SoftmaxRegression:
    """Multinomial logistic regression with L2 regularisation, fitted with L-BFGS (NumPy + SciPy)."""

    def __init__(self, l2: float = 0.02, max_iter: int = 400):
        self.l2 = l2
        self.max_iter = max_iter
        self.mu = self.sd = self.W = self.b = None

    def fit(self, X: np.ndarray, y: np.ndarray, n_classes: int) -> "SoftmaxRegression":
        self.mu = X.mean(axis=0)
        sd = X.std(axis=0)
        self.sd = np.where(sd < 1e-6, 1.0, sd)        # constant features contribute nothing
        Z = (X - self.mu) / self.sd
        n, d = Z.shape
        Y = np.zeros((n, n_classes))
        Y[np.arange(n), y] = 1.0

        def objective(w: np.ndarray):
            W = w[: d * n_classes].reshape(d, n_classes)
            b = w[d * n_classes:]
            logits = Z @ W + b
            lse = logsumexp(logits, axis=1)
            loss = -(logits[np.arange(n), y] - lse).mean() + 0.5 * self.l2 * float((W ** 2).sum())
            G = (np.exp(logits - lse[:, None]) - Y) / n
            return loss, np.concatenate([(Z.T @ G + self.l2 * W).ravel(), G.sum(axis=0)])

        res = minimize(objective, np.zeros(d * n_classes + n_classes), jac=True, method="L-BFGS-B",
                       options={"maxiter": self.max_iter})
        self.W = res.x[: d * n_classes].reshape(d, n_classes)
        self.b = res.x[d * n_classes:]
        return self

    def predict_proba(self, X: np.ndarray) -> np.ndarray:
        Z = np.clip((X - self.mu) / self.sd, -6.0, 6.0)   # no wild extrapolation on unusual data
        return softmax(Z @ self.W + self.b, axis=1)


def build_training_set(index: HeadingIndex, pool: HeadingPool, seed: int, files_per_kind: int):
    rng = random.Random(seed)
    cache: dict = {}
    X, y, groups = [], [], []
    for kind in KINDS:
        for _ in range(files_per_kind):
            f = make_file(rng, kind, pool)
            xs, _, _ = featurize_file(index, kind, [c.heading for c in f.columns], [c.values[:30] for c in f.columns], True, cache)
            for row, col in zip(xs, f.columns):
                X.append(row)
                y.append(LABELS.index(col.label))
                groups.append(normalize(col.heading))
    return np.vstack(X), np.array(y), groups


@dataclass
class ModelInfo:
    name: str
    version: str
    algorithm: str
    training_rows: int
    training_files: int
    corpus_headings: int
    train_seconds: float
    source: str = "trained"


class ColumnMapper:
    """Trained column ranker. Deterministic: the same corpus and seed always give the same model and version."""

    def __init__(self, corpus: dict | None = None, seed: int = 7, files_per_kind: int = 220, l2: float = 0.02,
                 use_cache: bool = False):
        started = time.perf_counter()
        self.corpus = corpus or load_corpus()
        self.index = HeadingIndex(self.corpus)
        self.pool = HeadingPool(self.corpus)
        version = _version(self.corpus, seed, files_per_kind, l2)
        cache_file = CACHE_DIR / f"{MODEL_NAME}-{version}.npz"
        self.X = self.y = self.groups = None
        if use_cache and cache_file.exists():
            blob = np.load(cache_file)
            self.model = SoftmaxRegression(l2=l2)
            self.model.mu, self.model.sd, self.model.W, self.model.b = blob["mu"], blob["sd"], blob["W"], blob["b"]
            rows, source = int(blob["rows"]), "loaded from cache"
        else:
            self.X, self.y, self.groups = build_training_set(self.index, self.pool, seed, files_per_kind)
            self.model = SoftmaxRegression(l2=l2).fit(self.X, self.y, len(LABELS))
            rows, source = len(self.y), "trained"
            if use_cache:
                CACHE_DIR.mkdir(parents=True, exist_ok=True)
                np.savez(cache_file, mu=self.model.mu, sd=self.model.sd, W=self.model.W, b=self.model.b, rows=rows)
        self.info = ModelInfo(MODEL_NAME, version, "multinomial logistic regression (L-BFGS, NumPy/SciPy)", rows,
                              files_per_kind * len(KINDS), len(self.index.norms), round(time.perf_counter() - started, 2), source)

    # ---------------------------------------------------------------------------- inference
    def probabilities(self, kind: str, headings: list[str], values: list[list[str]]):
        X, stats, named = featurize_file(self.index, kind, headings, values)
        P = self.model.predict_proba(X)
        mask = np.array([1.0 if (LABELS[i] in ALLOWED[kind] or i == IGNORE) else 0.0 for i in range(len(LABELS))])
        P = P * mask
        totals = P.sum(axis=1, keepdims=True)
        P = np.where(totals > 0, P / np.where(totals > 0, totals, 1.0), np.eye(len(LABELS))[IGNORE])
        return P, stats, named

    def assign(self, kind: str, P: np.ndarray) -> dict[int, int]:
        """One field per column and one column per field (Hungarian), keeping only assignments above the threshold."""
        fields = [i for i in range(IGNORE) if LABELS[i] in ALLOWED[kind]]
        rows, cols = linear_sum_assignment(-P[:, fields])
        chosen = {c: IGNORE for c in range(P.shape[0])}
        for r, c in zip(rows, cols):
            if P[r, fields[c]] >= TAU_ASSIGN:
                chosen[r] = fields[c]
        return chosen

    def suggest(self, kind: str, headings: list[str], values: list[list[str]]) -> list[dict]:
        P, stats, named = self.probabilities(kind, headings, values)
        chosen = self.assign(kind, P)
        taken = {lab for lab in chosen.values() if lab != IGNORE}
        out = []
        for c, heading in enumerate(headings):
            label = chosen[c]
            options = [IGNORE] + [i for i in range(IGNORE) if LABELS[i] in ALLOWED[kind] and (i not in taken or i == label)]
            denom = float(sum(P[c, o] for o in options)) or 1.0
            confidence = float(P[c, label] / denom)
            alts = sorted(((i, float(P[c, i])) for i in range(len(LABELS)) if i != label and (LABELS[i] in ALLOWED[kind] or i == IGNORE)),
                          key=lambda t: -t[1])
            out.append({
                "columnIndex": c,
                "header": heading,
                "field": None if label == IGNORE else LABELS[label],
                "confidence": round(confidence, 4),
                "band": next(name for floor, name in BANDS if confidence >= floor),
                "reasons": self._reasons(heading, label, confidence, named[c], stats[c], P[c]),
                "alternatives": [{"field": None if i == IGNORE else LABELS[i], "confidence": round(p, 4)} for i, p in alts[:3] if p >= 0.02],
            })
        return out

    # ---------------------------------------------------------------------------- explanations
    def _reasons(self, heading: str, label: int, confidence: float, f: dict, stats: dict, probs: np.ndarray) -> list[str]:
        """Plain-language evidence drawn from measured features. These describe the data; they are not model internals."""
        reasons: list[str] = []
        alias, sim = self.index.best_alias(heading, label)
        if sim >= 85:
            reasons.append(f'Heading "{heading}" closely matches the known label "{alias}" ({sim:.0f}% similar).')
        elif sim >= 60:
            reasons.append(f'Heading "{heading}" resembles the known label "{alias}" ({sim:.0f}% similar).')
        elif label != IGNORE:
            reasons.append(f'Heading "{heading}" does not resemble a known {FIELD_TITLES[LABELS[label]]} label; this rests mainly on the values.')

        pct = lambda x: f"{x:.0%}"
        name = LABELS[label]
        if name == "TransactionRef" and f["frac_ref_like"] >= 0.8:
            reasons.append(f"{pct(f['frac_ref_like'])} of values look like reference codes" + (", nearly all different (an order-level reference)." if f["distinct_ratio"] >= 0.9 else "."))
        elif name == "SettlementRef" and f["frac_ref_like"] >= 0.8:
            reasons.append("Values are reference codes" + (" shared by several rows, typical of a payout reference that groups payments." if f["distinct_ratio"] < 0.6 else ", each different, typical of a bank credit reference."))
        elif name == "Amount" and f["frac_money"] >= 0.9:
            extra = " and it is the largest money column." if f["rel_mean"] >= 0.99 and f["money_rank"] >= 0.99 else "."
            reasons.append(f"{pct(f['frac_money'])} of values are amounts with at most 2 decimals{extra}")
        elif name == "Fees" and f["frac_money"] >= 0.9 and f["rel_mean"] > 0:
            reasons.append(f"Amounts are small compared with the main amount column (about {pct(min(1.0, f['rel_mean']))} of its size), typical of fees.")
        elif name == "TxnDate" and f["frac_date"] >= 0.9:
            reasons.append(f"{pct(f['frac_date'])} of values parse as dates.")
        elif name == "Currency" and f["frac_cur3"] >= 0.9:
            reasons.append("Values are currency codes.")
        elif name == "Status" and f["frac_wordlike"] >= 0.8 and stats["distinct"] <= 8:
            reasons.append(f"Only {stats['distinct']} different text value(s), reading like status words.")
        elif name == "IGNORE":
            if f["m_mono"] >= 0.9 and f["frac_money"] >= 0.8:
                reasons.append("Values grow steadily down the column, like a running balance.")
            elif f["m_zero_frac"] >= 0.9 and f["frac_money"] >= 0.8:
                reasons.append("Values are almost all zero, typical of an unused debit column.")
            elif f["frac_space"] >= 0.5:
                reasons.append("Values are free text, not an identifier or an amount.")
            elif f["frac_phone"] >= 0.8 or f["frac_email"] >= 0.8:
                reasons.append("Values look like personal contact details.")
            elif f["frac_date"] >= 0.8:
                reasons.append("Values are dates, but another column fits the transaction date better.")
            elif f["frac_money"] >= 0.8:
                reasons.append("Amounts, but not the main amount or fee column.")

        runner_up = max((p, i) for i, p in enumerate(probs) if i != label)
        if confidence < 0.5:
            reasons.append(f"Low confidence: {LABELS[runner_up[1]] if runner_up[1] != IGNORE else 'ignoring the column'} is also plausible. Please check this column yourself.")
        return reasons[:4]


_MAPPER: ColumnMapper | None = None


def get_mapper() -> ColumnMapper:
    global _MAPPER
    if _MAPPER is None:
        _MAPPER = ColumnMapper(use_cache=True)
    return _MAPPER
