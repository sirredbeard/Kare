#!/usr/bin/env python3
"""Benchmark Kare against GitHub Copilot CLI auto. See issue #8.

Two arms, one frozen task set, three separate questions:

1. Does Kare preserve answer quality? (pass rate per arm)
2. Does Kare improve responsiveness? (time to first token and total time)
3. Does Kare reduce cost? (credits or tokens when the tool reports them,
   otherwise recorded as unknown, never estimated into existence)

The three answers are reported side by side. They are not combined into
one score. A cheap wrong answer is still wrong.

Arms:
  kare  POSTs each task to a Kare /v1 endpoint with streaming and times
        the first chunk and the last chunk.
  auto  Runs `copilot -p TASK --model auto` and times the process.

Usage:
  benchmark.py --tasks tasks.json --arms kare,auto --out results/
  benchmark.py --tasks tasks.json --dry-run

Dry run swaps both arms for a local stub so the harness itself can be
checked without a model, a device, or an account.
"""

import argparse
import json
import statistics
import subprocess
import sys
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


def score(text, expect_any):
    if not expect_any:
        return None
    lowered = text.lower()
    return any(fragment.lower() in lowered for fragment in expect_any)


def run_kare_arm(task, base_url, model, api_key, timeout):
    payload = json.dumps({
        "model": model,
        "stream": True,
        "messages": [{"role": "user", "content": task["prompt"]}],
    }).encode()
    request = urllib.request.Request(
        base_url.rstrip("/") + "/v1/chat/completions",
        data=payload,
        headers={
            "Content-Type": "application/json",
            **({"Authorization": f"Bearer {api_key}"} if api_key else {}),
        },
        method="POST",
    )
    started = time.perf_counter()
    first_token = None
    chunks = []
    usage = None
    with urllib.request.urlopen(request, timeout=timeout) as response:
        for raw in response:
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:"):
                continue
            data = line[len("data:"):].strip()
            if data == "[DONE]":
                break
            try:
                event = json.loads(data)
            except json.JSONDecodeError:
                continue
            if event.get("usage"):
                usage = event["usage"]
            for choice in event.get("choices", []):
                content = (choice.get("delta") or {}).get("content")
                if content:
                    if first_token is None:
                        first_token = time.perf_counter()
                    chunks.append(content)
    total = time.perf_counter() - started
    return {
        "text": "".join(chunks),
        "ttft_seconds": None if first_token is None else round(first_token - started, 4),
        "total_seconds": round(total, 4),
        "input_tokens": (usage or {}).get("prompt_tokens"),
        "output_tokens": (usage or {}).get("completion_tokens"),
        "credits": None,
    }


def run_auto_arm(task, copilot_bin, timeout):
    started = time.perf_counter()
    completed = subprocess.run(
        [copilot_bin, "-p", task["prompt"], "--model", "auto"],
        capture_output=True, text=True, timeout=timeout, check=False,
    )
    total = time.perf_counter() - started
    text = completed.stdout or ""
    credits = None
    for line in text.splitlines():
        if "credit" in line.lower():
            for token in line.replace(",", "").split():
                try:
                    credits = float(token)
                except ValueError:
                    continue
    return {
        "text": text,
        "ttft_seconds": None,
        "total_seconds": round(total, 4),
        "input_tokens": None,
        "output_tokens": None,
        "credits": credits,
        "exit_code": completed.returncode,
    }


def run_stub_arm(task, arm):
    time.sleep(0.01)
    expected = (task.get("expect_any") or ["ok"])[0]
    return {
        "text": f"stub answer for {arm}: {expected}",
        "ttft_seconds": 0.005,
        "total_seconds": 0.01,
        "input_tokens": 10,
        "output_tokens": 5,
        "credits": None,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tasks", required=True, help="JSON file with the frozen task set")
    parser.add_argument("--arms", default="kare,auto", help="comma list: kare,auto")
    parser.add_argument("--out", default="benchmark-results", help="output directory")
    parser.add_argument("--kare-base-url", default="http://127.0.0.1:5285")
    parser.add_argument("--kare-model", default="kare")
    parser.add_argument("--kare-api-key", default=None, help="or set KARE_BENCH_API_KEY; never put a key in the task file")
    parser.add_argument("--copilot-bin", default="copilot")
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--dry-run", action="store_true", help="stub both arms to check the harness itself")
    args = parser.parse_args()

    import os
    api_key = args.kare_api_key or os.environ.get("KARE_BENCH_API_KEY")
    tasks = json.loads(Path(args.tasks).read_text())
    if isinstance(tasks, dict):
        tasks = tasks["tasks"]
    arms = [arm.strip() for arm in args.arms.split(",") if arm.strip()]

    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    out_dir = Path(args.out) / stamp
    out_dir.mkdir(parents=True, exist_ok=True)
    rows = []
    for task in tasks:
        for arm in arms:
            row = {
                "run": stamp,
                "task_id": task["id"],
                "category": task.get("category"),
                "arm": arm,
                "recorded_at": datetime.now(timezone.utc).isoformat(),
            }
            try:
                if args.dry_run:
                    result = run_stub_arm(task, arm)
                elif arm == "kare":
                    result = run_kare_arm(task, args.kare_base_url, args.kare_model, api_key, args.timeout)
                elif arm == "auto":
                    result = run_auto_arm(task, args.copilot_bin, args.timeout)
                else:
                    raise ValueError(f"unknown arm: {arm}")
                row.update({k: v for k, v in result.items() if k != "text"})
                row["passed"] = score(result["text"], task.get("expect_any"))
                row["answer"] = result["text"][:2000]
                row["error"] = None
            except Exception as exc:  # one task failing must not lose the run
                row.update({"passed": False, "error": f"{type(exc).__name__}: {exc}"})
            rows.append(row)
            print(f"{arm:5s} {task['id']:24s} passed={row['passed']} total={row.get('total_seconds')}")

    with open(out_dir / "results.jsonl", "w") as handle:
        for row in rows:
            handle.write(json.dumps(row) + "\n")

    summary = {}
    for arm in arms:
        arm_rows = [r for r in rows if r["arm"] == arm]
        scored = [r for r in arm_rows if r.get("passed") is not None]
        totals = [r["total_seconds"] for r in arm_rows if r.get("total_seconds")]
        ttfts = [r["ttft_seconds"] for r in arm_rows if r.get("ttft_seconds")]
        summary[arm] = {
            "tasks": len(arm_rows),
            "passed": sum(1 for r in scored if r["passed"]),
            "pass_rate": None if not scored else round(sum(1 for r in scored if r["passed"]) / len(scored), 3),
            "median_total_seconds": None if not totals else round(statistics.median(totals), 4),
            "median_ttft_seconds": None if not ttfts else round(statistics.median(ttfts), 4),
            "credits_total": None,
            "note": "quality, latency, and cost stay separate. No combined score.",
        }
        credits = [r["credits"] for r in arm_rows if r.get("credits") is not None]
        if credits:
            summary[arm]["credits_total"] = round(sum(credits), 4)
    (out_dir / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    print(f"wrote {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
