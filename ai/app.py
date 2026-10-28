"""
Angaza Care AI parsing service.

One job: read a short free-text reply from a pregnant woman (English, Swahili,
Sheng or mixed) and say which WHO antenatal danger signs it mentions, quoting
the words that show each one. It never writes to the mother and never decides
anything: the .NET app keeps only signs whose quoted evidence is really in the
message, and sends anything uncertain to a person.

Works with any OpenAI-compatible chat endpoint:
  - Ollama:      LLM_BASE_URL=http://localhost:11434/v1   LLM_MODEL=qwen3:32b
  - llama.cpp:   LLM_BASE_URL=http://localhost:8080/v1
  - vLLM, or a hosted provider that serves open-weight models
Only the reply text is sent. No names, numbers or locations.
"""
from __future__ import annotations

import json
import os
import re
import time
from typing import Literal

import httpx
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field, ValidationError

PROMPT_VERSION = "2026-10-16.1"
BASE_URL = os.environ.get("LLM_BASE_URL", "http://localhost:11434/v1").rstrip("/")
MODEL = os.environ.get("LLM_MODEL", "qwen3:32b")
API_KEY = os.environ.get("LLM_API_KEY", "")
TIMEOUT = float(os.environ.get("LLM_TIMEOUT_SECONDS", "8"))

SIGNS = {
    "BLEED": "vaginal bleeding or spotting (damu, kutokwa na damu)",
    "CONVULS": "convulsions or fits (degedege, kifafa)",
    "HEADVIS": "severe headache or blurred vision (kichwa kuuma sana, kuona giza)",
    "FETALMOV": "baby moving less or not at all (mtoto hachezi, hasongi)",
    "ABDPAIN": "severe abdominal pain (maumivu makali ya tumbo)",
    "FLUID": "fluid leaking from the vagina, waters breaking (maji kutoka, chupa kupasuka)",
    "BREATH": "difficulty breathing (shida ya kupumua)",
    "FEVER": "fever or hot body (homa, joto, mwili moto)",
    "SWELL": "swelling of face or hands (kuvimba uso au mikono)",
}

SYSTEM = """You read short SMS replies from pregnant women in Kenya to a weekly health check-in.
Replies may be in English, Swahili, Sheng, or a mix, with spelling mistakes.

Find which of these danger signs the woman says she has NOW:
{signs}

Rules:
- Only report a sign she says she has. A sign she denies ("sina damu", "no bleeding") is NOT present.
- "evidence" must be words copied exactly from her message, never a paraphrase or translation.
- Use confidence below 0.6 if you are unsure. Do not guess.
- wellbeing: "well" only if she clearly says she is fine and mentions no problem; "unwell" if she
  describes any problem, even one not on the list; otherwise "unknown".
- Reply with JSON only, no other text, in exactly this shape:
{{"signs":[{{"code":"BLEED","confidence":0.9,"evidence":"natoka damu"}}],"wellbeing":"unwell","wellbeing_confidence":0.9}}"""

FEW_SHOT = [
    ("niko poa sana, mtoto anacheza vizuri", {"signs": [], "wellbeing": "well", "wellbeing_confidence": 0.95}),
    ("sina damu lakini kichwa inaniuma mbaya na naona nyota",
     {"signs": [{"code": "HEADVIS", "confidence": 0.9, "evidence": "kichwa inaniuma mbaya"}], "wellbeing": "unwell", "wellbeing_confidence": 0.9}),
    ("beby haja kick tangu jana usiku", {"signs": [{"code": "FETALMOV", "confidence": 0.85, "evidence": "beby haja kick"}], "wellbeing": "unwell", "wellbeing_confidence": 0.85}),
    ("nimechoka tu, kazi ni mingi", {"signs": [], "wellbeing": "unwell", "wellbeing_confidence": 0.6}),
    ("my legs are swollen a bit like always", {"signs": [], "wellbeing": "unknown", "wellbeing_confidence": 0.5}),
    ("mwili iko na joto na natetemeka", {"signs": [{"code": "FEVER", "confidence": 0.85, "evidence": "mwili iko na joto"}], "wellbeing": "unwell", "wellbeing_confidence": 0.9}),
]


class ParseRequest(BaseModel):
    text: str = Field(max_length=1000)
    language: Literal["sw", "en"] = "sw"
    allowed_codes: list[str] | None = None


class Sign(BaseModel):
    code: str
    confidence: float = Field(ge=0, le=1)
    evidence: str | None = None


class ModelOutput(BaseModel):
    signs: list[Sign] = []
    wellbeing: Literal["well", "unwell", "unknown"] = "unknown"
    wellbeing_confidence: float = Field(default=0, ge=0, le=1)


class ParseResponse(ModelOutput):
    model: str
    prompt_version: str


app = FastAPI(title="Angaza Care parsing service", version=PROMPT_VERSION)


def build_messages(text: str, codes: list[str]) -> list[dict]:
    signs = "\n".join(f"- {c}: {SIGNS[c]}" for c in codes)
    messages = [{"role": "system", "content": SYSTEM.format(signs=signs)}]
    for example, answer in FEW_SHOT:
        messages.append({"role": "user", "content": example})
        messages.append({"role": "assistant", "content": json.dumps(answer, ensure_ascii=False)})
    messages.append({"role": "user", "content": text})
    return messages


def extract_json(content: str) -> dict:
    # Some models wrap JSON in prose, code fences or <think> blocks; take the last JSON object.
    content = re.sub(r"<think>.*?</think>", "", content, flags=re.S)
    candidates = re.findall(r"\{.*\}", content, flags=re.S)
    if not candidates:
        raise ValueError("no JSON object in model output")
    return json.loads(candidates[-1])


@app.get("/health")
def health() -> dict:
    return {"ok": True, "model": MODEL, "prompt_version": PROMPT_VERSION}


@app.post("/v1/parse", response_model=ParseResponse)
async def parse(req: ParseRequest) -> ParseResponse:
    codes = [c for c in (req.allowed_codes or SIGNS) if c in SIGNS]
    payload = {
        "model": MODEL,
        "messages": build_messages(req.text, codes),
        "temperature": 0,
        "max_tokens": 300,
        "response_format": {"type": "json_object"},
    }
    headers = {"Authorization": f"Bearer {API_KEY}"} if API_KEY else {}
    try:
        async with httpx.AsyncClient(timeout=TIMEOUT) as client:
            res = await client.post(f"{BASE_URL}/chat/completions", json=payload, headers=headers)
            res.raise_for_status()
        content = res.json()["choices"][0]["message"]["content"]
        out = ModelOutput.model_validate(extract_json(content))
    except (httpx.HTTPError, KeyError, ValueError, ValidationError) as exc:
        # The caller treats any error as "a person must read this".
        raise HTTPException(status_code=502, detail=f"model call failed: {type(exc).__name__}") from exc

    # Drop codes the caller did not allow and evidence the model invented.
    lowered = req.text.lower()
    signs = [s for s in out.signs if s.code in codes and s.evidence and s.evidence.lower() in lowered]
    return ParseResponse(signs=signs, wellbeing=out.wellbeing, wellbeing_confidence=out.wellbeing_confidence,
                         model=MODEL, prompt_version=PROMPT_VERSION)
