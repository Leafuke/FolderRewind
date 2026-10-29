"""Summarize unchanged Core Smart scenarios; preserve missing diagnostics as null."""
import argparse
import json
import pathlib
import statistics


def milliseconds(value):
    h, m, s = value.split(":")
    return (int(h) * 3600 + int(m) * 60 + float(s)) * 1000


def median(values):
    return statistics.median(values) if values else None


def groups(root):
    result = {}
    samples = 0
    for path in sorted(root.glob("*/host-backup.json")):
        rows = json.loads(path.read_text(encoding="utf-8-sig"))["results"]
        samples += len(rows)
        for row in rows:
            result.setdefault(f"{path.parent.name}/{row['mode']}/layer-{row['layer']}", []).append(row)
            if row["mode"] == "Smart" and row["layer"] > 0:
                result.setdefault(f"{path.parent.name}/Smart/all-deltas", []).append(row)
    for path in sorted(root.glob("*/analysis-probe.json")):
        rows = json.loads(path.read_text(encoding="utf-8-sig"))["rows"]
        samples += len(rows)
        last_payload = {}
        for row in rows:
            row["payloadDeltaBytes"] = row["totalPayloadBytes"] - last_payload.get(row["method"], 0)
            last_payload[row["method"]] = row["totalPayloadBytes"]
            if row["kind"] == "unchanged" and (row["CreatedNewArchive"] or row["payloadCount"] != 1
                    or row["packCount"] != row["iteration"] + 1):
                raise ValueError(f"No-change archive/history semantics differ: {path}")
        if path.parent.name == "history-growth":
            for end in [5, 50, 100, 150, 200, 250, 300]:
                result[f"history-growth/unchanged/{end-4}-{end}"] = [
                    r for r in rows if r["kind"] == "unchanged" and end - 4 <= r["iteration"] <= end]
        else:
            for row in rows:
                result.setdefault(f"{path.parent.name}/{row['method']}/{row['kind']}", []).append(row)
        if any(not r["correct"] for r in rows):
            raise ValueError(f"Incorrect probe result: {path}")
    if samples != 405 or any(not rows or any(not r["correct"] for r in rows) for rows in result.values()):
        raise ValueError(f"Incomplete or incorrect cohort: {root}, samples={samples}")
    return result


def summarize(rows):
    times = [r["fileReadyMs"] for r in rows]
    phase_names = sorted({p["Code"] for r in rows for p in r["timing"]["Phases"]})
    counter_names = sorted({k for r in rows for k in r["diagnostic"]["Counters"]})
    inclusive_names = sorted({k for r in rows for k in r["diagnostic"]["InclusivePhases"]})
    return dict(samples=len(rows), medianMs=median(times), firstMs=times[0], minMs=min(times), maxMs=max(times),
        allocatedMiB=median([r["allocatedBytes"] / 1048576 for r in rows if "allocatedBytes" in r]),
        scanMs=median([r["activityStages"]["source.scan"] for r in rows if "source.scan" in r.get("activityStages", {})]),
        payloadDeltaBytes=median([r["payloadDeltaBytes"] for r in rows if "payloadDeltaBytes" in r]),
        phases={k: median([milliseconds(p["Elapsed"]) for r in rows for p in r["timing"]["Phases"] if p["Code"] == k]) for k in phase_names},
        counters={k: median([r["diagnostic"]["Counters"][k] for r in rows if k in r["diagnostic"]["Counters"]]) for k in counter_names},
        inclusivePhases={k: median([milliseconds(r["diagnostic"]["InclusivePhases"][k]) for r in rows if k in r["diagnostic"]["InclusivePhases"]]) for k in inclusive_names})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path)
    args = parser.parse_args()
    comparisons = []
    for round_number in [1, 2]:
        before_root = args.root / f"r{round_number}-before"
        after_root = args.root / f"r{round_number}-after"
        if not (before_root / "run.json").exists() or not (after_root / "run.json").exists():
            continue
        before = groups(before_root)
        after = groups(after_root)
        if before.keys() != after.keys():
            raise ValueError("Scenario sets differ")
        for key in before:
            b, a = summarize(before[key]), summarize(after[key])
            delta = a["medianMs"] - b["medianMs"]
            eligible = b["samples"] >= 5 and a["samples"] >= 5 and not key.endswith("all-deltas")
            row = dict(round=round_number, scenario=key, before=b, after=a,
                deltaMs=delta, deltaPct=100 * delta / b["medianMs"],
                eligibleForRegressionGate=eligible,
                regression=eligible and delta > 20 and a["medianMs"] > b["medianMs"] * 1.1)
            comparisons.append(row)
            if "all-deltas" in key or "unchanged" in key or "tiny-change" in key or "one-byte" in key or "/Full/" in key:
                print(f"r{round_number} {key:65} {b['medianMs']:8.2f} -> {a['medianMs']:8.2f} ms {row['deltaPct']:+6.1f}%")
    persistent = [r["scenario"] for r in comparisons if r["round"] == 1 and r["regression"]
        and any(s["round"] == 2 and s["scenario"] == r["scenario"] and s["regression"] for s in comparisons)]
    completed_rounds = sorted({r["round"] for r in comparisons})
    complete = completed_rounds == [1, 2]
    output = args.output or args.root / "comparison.json"
    output.write_text(json.dumps(dict(complete=complete, completedRounds=completed_rounds,
        comparisons=comparisons, persistentRegressions=persistent if complete else None), ensure_ascii=False, indent=2), encoding="utf-8")
    print("Persistent regressions:", persistent if complete else "pending the complete two-round matrix")


if __name__ == "__main__":
    main()
