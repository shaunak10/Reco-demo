"""
ReconAI local AI service (FastAPI).

Runs on localhost only.  The C# application calls it for column mapping (Step 4), plain-English rule drafting
(Step 5) and evidence-linked exception analysis (Step 7).  Every output is validated server-side by the C#
application, and the deterministic reconciliation engine works when this service is down.

Step 2 scope: the service skeleton and /health only - no model logic yet.
"""
from fastapi import FastAPI
from starlette.middleware.trustedhost import TrustedHostMiddleware

SERVICE = "reconai-ai"
VERSION = "0.1.0"

app = FastAPI(title="ReconAI local AI service", version=VERSION)

# Restrict the service: refuse requests whose Host header is not loopback (basic DNS-rebinding / exposure guard).
app.add_middleware(TrustedHostMiddleware, allowed_hosts=["127.0.0.1", "localhost", "testserver"])


@app.get("/health")
def health() -> dict:
    return {
        "status": "ok",
        "service": SERVICE,
        "version": VERSION,
        # Honest capability flags: they flip to true only when the capability really exists.
        "capabilities": {"column_mapping": False, "rule_drafting": False, "exception_analysis": False},
    }
