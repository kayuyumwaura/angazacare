"""Offline tests: the model endpoint is replaced by a stub, so no GPU or network is needed."""
import json

import httpx
import pytest
from fastapi.testclient import TestClient

import app as service


def stub(content: str):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"choices": [{"message": {"content": content}}]})
    return httpx.MockTransport(handler)


@pytest.fixture
def client(monkeypatch):
    def use(content):
        real = httpx.AsyncClient
        monkeypatch.setattr(service.httpx, "AsyncClient", lambda **kw: real(transport=stub(content), **kw))
        return TestClient(service.app)
    return use


def test_keeps_signs_with_real_evidence(client):
    out = {"signs": [{"code": "HEADVIS", "confidence": 0.9, "evidence": "kichwa inaniuma"}], "wellbeing": "unwell", "wellbeing_confidence": 0.9}
    r = client(json.dumps(out)).post("/v1/parse", json={"text": "Kichwa inaniuma tangu jana", "language": "sw"})
    assert r.status_code == 200
    assert [s["code"] for s in r.json()["signs"]] == ["HEADVIS"]


def test_drops_invented_evidence_and_unknown_codes(client):
    out = {"signs": [{"code": "BLEED", "confidence": 0.9, "evidence": "natoka damu"},
                     {"code": "MADEUP", "confidence": 0.9, "evidence": "tired"}], "wellbeing": "unwell", "wellbeing_confidence": 0.7}
    r = client(json.dumps(out)).post("/v1/parse", json={"text": "I am tired", "language": "en"})
    assert r.json()["signs"] == []


def test_reads_json_wrapped_in_think_and_fences(client):
    content = '<think>maybe {"x":1}</think>\n```json\n{"signs":[],"wellbeing":"well","wellbeing_confidence":0.9}\n```'
    r = client(content).post("/v1/parse", json={"text": "niko poa", "language": "sw"})
    assert r.json()["wellbeing"] == "well"


def test_bad_model_output_is_an_error_not_a_guess(client):
    r = client("I think she is fine").post("/v1/parse", json={"text": "niko poa", "language": "sw"})
    assert r.status_code == 502


def test_prompt_lists_only_allowed_codes():
    msgs = service.build_messages("hi", ["BLEED", "FEVER"])
    assert "BLEED" in msgs[0]["content"] and "FEVER" in msgs[0]["content"]
    assert "CONVULS:" not in msgs[0]["content"]
