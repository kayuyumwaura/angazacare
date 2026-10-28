"""
Evaluate candidate models on labelled replies, through the same AI service the app uses.

  # one run per model; the service reads LLM_MODEL / LLM_BASE_URL
  LLM_MODEL=qwen3:32b uvicorn app:app --port 8001   (in ai/)
  python run_eval.py --set starter_set.csv --service http://localhost:8001 --label qwen3-32b

Columns: text, language (sw|en), expected (sign codes joined by |, WELL, or UNCLEAR),
variety (en|sw|sheng), note. "WELL|UNCLEAR" means either answer is acceptable.

The number that matters most is danger-sign RECALL: a missed sign is the costly error.
Results are printed and appended to results.csv so runs can be compared.
"""
import argparse
import csv
import statistics
import time
from collections import defaultdict
from pathlib import Path

import httpx

SIGNS = {"BLEED", "CONVULS", "HEADVIS", "FETALMOV", "ABDPAIN", "FLUID", "BREATH", "FEVER", "SWELL"}


def predicted(resp: dict, sign_threshold: float, well_threshold: float) -> set[str]:
    signs = {s["code"] for s in resp["signs"] if s["confidence"] >= sign_threshold}
    if signs:
        return signs
    if resp["wellbeing"] == "well" and resp["wellbeing_confidence"] >= well_threshold:
        return {"WELL"}
    return {"UNCLEAR"}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--set", default="starter_set.csv")
    ap.add_argument("--service", default="http://localhost:8001")
    ap.add_argument("--label", required=True, help="name for this run, e.g. the model")
    ap.add_argument("--sign-threshold", type=float, default=0.6)
    ap.add_argument("--well-threshold", type=float, default=0.85)
    args = ap.parse_args()

    rows = list(csv.DictReader(open(args.set, encoding="utf-8")))
    tp = fp = fn = 0
    by_variety = defaultdict(lambda: [0, 0])          # [signs found, signs expected]
    by_sign = defaultdict(lambda: [0, 0])
    false_well = errors = 0
    latencies = []

    for row in rows:
        expected_raw = set(row["expected"].split("|"))
        expected_signs = expected_raw & SIGNS
        t0 = time.perf_counter()
        try:
            r = httpx.post(f"{args.service}/v1/parse", json={"text": row["text"], "language": row["language"]}, timeout=60)
            r.raise_for_status()
            got = predicted(r.json(), args.sign_threshold, args.well_threshold)
        except httpx.HTTPError:
            errors += 1
            got = {"UNCLEAR"}                          # the app sends errors to a person
        latencies.append((time.perf_counter() - t0) * 1000)
        got_signs = got & SIGNS

        tp += len(expected_signs & got_signs)
        fn += len(expected_signs - got_signs)
        fp += len(got_signs - expected_signs)
        by_variety[row["variety"]][0] += len(expected_signs & got_signs)
        by_variety[row["variety"]][1] += len(expected_signs)
        for s in expected_signs:
            by_sign[s][1] += 1
            by_sign[s][0] += s in got_signs
        if expected_signs and got == {"WELL"}:
            false_well += 1                            # the worst error: danger read as well
        mark = "ok " if (expected_signs and expected_signs <= got_signs) or (not expected_signs and got & expected_raw) else "XX "
        print(f"{mark}{row['text'][:48]:<50} expected={row['expected']:<16} got={'|'.join(sorted(got))}")

    recall = tp / (tp + fn) if tp + fn else 0
    precision = tp / (tp + fp) if tp + fp else 0
    print(f"\n{args.label}: recall {recall:.2f}, precision {precision:.2f}, danger read as well {false_well}, "
          f"errors {errors}, median latency {statistics.median(latencies):.0f} ms")
    for v, (found, total) in sorted(by_variety.items()):
        if total:
            print(f"  recall {v:<6} {found}/{total}")
    for s, (found, total) in sorted(by_sign.items()):
        print(f"  recall {s:<9} {found}/{total}")

    out = Path("results.csv")
    new = not out.exists()
    with out.open("a", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        if new:
            w.writerow(["label", "set", "rows", "recall", "precision", "danger_as_well", "errors", "median_ms"])
        w.writerow([args.label, args.set, len(rows), f"{recall:.3f}", f"{precision:.3f}", false_well, errors, f"{statistics.median(latencies):.0f}"])


if __name__ == "__main__":
    main()
