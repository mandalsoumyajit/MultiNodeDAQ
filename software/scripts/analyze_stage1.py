"""Check saved Stage 1 loopback evidence; standard-library only."""
import argparse, json, statistics
from pathlib import Path
p = argparse.ArgumentParser()
p.add_argument("--seconds", type=int, default=1800)
p.add_argument("--directory", type=Path, default=Path(".artifacts/stage1"))
a = p.parse_args()
report = {"duration_seconds": a.seconds, "scope": "loopback; receiver and simulator share one process", "runs": []}
for nodes in (2, 16):
    stem = a.directory / f"baseline-{nodes}-{a.seconds}"
    data = [json.loads(line) for line in Path(str(stem)+"-metrics.jsonl").read_text().splitlines()]
    summary = json.loads(Path(str(stem)+"-summary.json").read_text())
    warmup = min(60, a.seconds / 5)
    steady = [d for d in data if d["elapsed_seconds"] >= warmup]
    n = max(1, len(steady)//3)
    memory = {}
    for key, limit in (("managed_bytes", 32*1024**2), ("working_set_bytes", 64*1024**2)):
        values = [d["host"]["memory"][key] for d in steady]
        first = statistics.median(values[:n]); last = statistics.median(values[-n:])
        memory[key] = {"first_third_median": first, "last_third_median": last, "growth": last-first, "peak": max(values), "allowed_growth": limit}
    hs = summary["host"]["units"]; ss = summary["simulator"]["units"]
    by_id = {s["unit"]: s for s in ss}
    continuity = len(hs)==nodes and summary["host"]["errors"]==0 and all(
        h["sample_errors"]==0 and h["missing_rows"]==0 and h["rows"]==by_id[h["unit"]]["produced_rows"] and h["rows"]>0 and by_id[h["unit"]]["dropped_rows"]==0 for h in hs)
    bounded = all(s["peak_bytes"]<=s["buffer_limit"] for s in ss) and all(h["recent_hashes"]<=1024 and h["gap_intervals"]<=1024 for h in hs)
    growth_ok = all(m["growth"]<=m["allowed_growth"] for m in memory.values())
    report["runs"].append({"nodes": nodes, "rows": sum(h["rows"] for h in hs), "payload_bytes": sum(h["payload_bytes"] for h in hs), "memory": memory, "continuity_pass": continuity, "bounds_pass": bounded, "memory_growth_pass": growth_ok, "pass": continuity and bounded and growth_ok})
report["pass"] = all(r["pass"] for r in report["runs"])
report["full_duration_gate"] = report["pass"] and a.seconds>=1800
(a.directory/"qualification.json").write_text(json.dumps(report, indent=2)+"\n")
print(json.dumps(report, indent=2))
raise SystemExit(0 if report["pass"] else 1)
