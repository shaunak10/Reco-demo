"""Heading text normalisation shared by training, evaluation and inference."""
from __future__ import annotations

import re

_CAMEL = re.compile(r"(?<=[a-z0-9])(?=[A-Z])")
_NON_ALNUM = re.compile(r"[^a-z0-9]+")


def normalize(heading: str) -> str:
    """'TxnRef' -> 'txn ref', 'Value (INR)' -> 'value inr', 'bank_reference_no' -> 'bank reference no'."""
    spaced = _CAMEL.sub(" ", heading or "")
    return _NON_ALNUM.sub(" ", spaced.lower()).strip()


def tokens(heading: str) -> list[str]:
    return normalize(heading).split()
