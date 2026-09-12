"""Summarize saved Stage 2 evidence without re-reading sample recordings."""
import argparse
import json
import statistics
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--directory", type=Path, default=Path(".artifacts/stage2"))
parser.add_argument("--seconds", type=int, default=7200)
parser.add_argument("--nodes", type=int, default=16)
a = parser.parse_args()
stem = a.directory / f"benchmark-{a.nodes}-{a.seconds}"
summary = json.loads(Path(str(stem) + "-summary.json").read_text())
with Path(str(stem) + "-metrics.jsonl").open() as stream:
    telemetry = [json.loads(line) for line in stream]
steady = [t for t in telemetry if t["elapsed_seconds"] >= min(60, a.seconds / 5)]
if len(steady) < 3:
    raise SystemExit("Insufficient telemetry")
third = max(1, len(steady) // 3)
memory = {}
for key, limit in (("managed_bytes", 32 * 1024**2), ("working_set_bytes", 64 * 1024**2)):
    values = [t["host"]["memory"][key] for t in steady]
    first, last = statistics.median(values[:third]), statistics.median(values[-third:])
    memory[key] = dict(first_third_median=first, last_third_median=last,
                       growth=last-first, peak=max(values), allowed_growth=limit)
r, v, source = summary["recorded"], summary["verification"], summary["source"]["units"]
produced = sum(u["produced_rows"] for u in source)
by_id = {u["unit"].lower(): u for u in source}
checks = {
    "duration": telemetry[-1]["elapsed_seconds"] >= a.seconds - 1,
    "scenario": summary["scenario"]["Nodes"] == a.nodes and summary["scenario"]["Seconds"] == a.seconds,
    "verification": v["Status"] == "complete" and not v["Errors"] and v["SampleErrors"] == 0,
    "recording": r["state"] == "complete" and not r["errors"] and r["queued_bytes"] == 0,
    "totals": v["Rows"] == produced and produced > 0,
    "units": len(source) == a.nodes and len(r["units"]) == a.nodes and all(
        u["written_rows"] == u["flushed_rows"] == u["committed_next_sample"] == by_id[u["unit"].lower()]["produced_rows"] for u in r["units"]),
    "source": all(u["dropped_rows"] == 0 and u["failures"] == 0 and u["done"] for u in source),
    "continuity": all(t["host"]["errors"] == 0 and all(u["missing_rows"] == 0 and u["sample_errors"] == 0 for u in t["host"]["units"]) for t in telemetry),
    "queues": r["peak_queued_bytes"] <= r["queue_limit"] and all(u["peak_bytes"] <= r["per_unit_limit"] for u in r["units"]) and all(u["peak_bytes"] <= u["buffer_limit"] for u in source),
    "memory_growth": all(m["growth"] <= m["allowed_growth"] for m in memory.values()),
}
report = dict(scope="loopback; simulator and receiver share a process; OS flush, not physical power-loss qualification",
              nodes=a.nodes, seconds=a.seconds, rows=v["Rows"], scalar_samples=3*v["Rows"],
              segment_count=len(v["Files"]), log_bytes=sum(f["ValidBytes"] for f in v["Files"]),
              build=r["software"]["build"], peak_queue_bytes=r["peak_queued_bytes"],
              memory=memory, checks=checks, passed=all(checks.values()))
report["full_duration_gate"] = report["passed"] and a.seconds >= 7200 and a.nodes >= 16
output = a.directory / f"qualification-{a.nodes}-{a.seconds}.json"
output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(json.dumps(report, indent=2))
raise SystemExit(0 if report["passed"] else 1)
