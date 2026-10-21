from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


def test_health_reports_service_and_no_capabilities_yet():
    r = client.get("/health")
    assert r.status_code == 200
    body = r.json()
    assert body["status"] == "ok"
    assert body["service"] == "reconai-ai"
    assert not any(body["capabilities"].values())   # nothing is claimed before it is built


def test_rejects_non_loopback_host_header():
    r = client.get("/health", headers={"host": "evil.example.com"})
    assert r.status_code == 400
