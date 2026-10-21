"""
ReconAI local AI service (FastAPI).

Runs on localhost only. The C# application calls it for column mapping (Step 4), plain-English rule drafting (Step 5)
and evidence-linked exception analysis (Step 7). Every output is validated server-side by the C# application, and the
deterministic reconciliation engine works when this service is down.

Privacy: sample values arrive in the request body only to be profiled in memory. They are never logged or stored here.
"""
from __future__ import annotations

import logging
import time
from contextlib import asynccontextmanager
from typing import Literal

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field, field_validator
from starlette.middleware.trustedhost import TrustedHostMiddleware

from . import mapper as column_mapper

SERVICE = "reconai-ai"
VERSION = "0.2.0"
log = logging.getLogger("reconai.ai")

_state: dict = {"mapper": None, "error": None}


@asynccontextmanager
async def lifespan(_: FastAPI):
    try:
        _state["mapper"] = column_mapper.get_mapper()
        info = _state["mapper"].info
        log.info("column ranker %s v%s ready (%s, %.1fs)", info.name, info.version, info.source, info.train_seconds)
    except Exception as exc:  # noqa: BLE001 - the service must still start so /health can report the problem
        _state["error"] = f"{type(exc).__name__}: {exc}"
        log.exception("column ranker failed to load")
    yield


app = FastAPI(title="ReconAI local AI service", version=VERSION, lifespan=lifespan)

# Restrict the service: refuse requests whose Host header is not loopback (basic DNS-rebinding / exposure guard).
app.add_middleware(TrustedHostMiddleware, allowed_hosts=["127.0.0.1", "localhost", "testserver"])


class ColumnIn(BaseModel):
    header: str = Field(max_length=100)
    samples: list[str] = Field(default_factory=list, max_length=50)

    @field_validator("samples")
    @classmethod
    def _trim(cls, values: list[str]) -> list[str]:
        return [(v or "")[:200] for v in values]


class MapRequest(BaseModel):
    sourceKind: Literal["Ledger", "Gateway", "Bank"]
    columns: list[ColumnIn] = Field(min_length=1, max_length=60)


def _model_block() -> dict | None:
    m = _state["mapper"]
    if m is None:
        return None
    i = m.info
    return {"name": i.name, "version": i.version, "algorithm": i.algorithm, "trainingRows": i.training_rows, "source": i.source}


@app.get("/health")
def health() -> dict:
    ready = _state["mapper"] is not None
    return {
        "status": "ok" if ready else "degraded",
        "service": SERVICE,
        "version": VERSION,
        # Honest capability flags: they are true only when the capability really exists and is loaded.
        "capabilities": {"column_mapping": ready, "rule_drafting": False, "exception_analysis": False},
        "columnMapping": _model_block(),
        "error": _state["error"],
    }


@app.post("/v1/map-columns")
def map_columns(req: MapRequest) -> dict:
    m = _state["mapper"]
    if m is None:
        raise HTTPException(status_code=503, detail="Column ranker is not loaded")
    started = time.perf_counter()
    suggestions = m.suggest(req.sourceKind, [c.header for c in req.columns], [c.samples for c in req.columns])
    latency = int((time.perf_counter() - started) * 1000)
    log.info("map-columns kind=%s columns=%d latencyMs=%d", req.sourceKind, len(req.columns), latency)   # counts only, never values
    return {"model": _model_block(), "latencyMs": latency, "suggestions": suggestions}
