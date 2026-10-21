from fastapi.testclient import TestClient

from app.main import app


def test_health_reports_only_the_capabilities_that_exist():
    with TestClient(app) as client:
        r = client.get("/health")
        assert r.status_code == 200
        body = r.json()
        assert body["status"] == "ok"
        assert body["service"] == "reconai-ai"
        # column mapping is built (Step 4); rule drafting (Step 5) and exception analysis (Step 7) are not - nothing is overclaimed
        assert body["capabilities"] == {"column_mapping": True, "rule_drafting": False, "exception_analysis": False}
        model = body["columnMapping"]
        assert model["name"] == "reconai-column-ranker"
        assert len(model["version"]) == 8 and model["trainingRows"] > 1000


def test_rejects_non_loopback_host_header():
    with TestClient(app) as client:
        r = client.get("/health", headers={"host": "evil.example.com"})
        assert r.status_code == 400
