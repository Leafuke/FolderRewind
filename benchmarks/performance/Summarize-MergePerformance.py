"""Summarize paired real Analyze and emitted-change initialization; absent/timeout results fail gates."""
import argparse
import json
import statistics
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("root", type=Path)
parser.add_argument("--rerun-root", type=Path, help="Use matching completed rerun files without overwriting interrupted raw results")
args = parser.parse_args()
root = args.root


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def median(values):
    return statistics.median(values) if len(values) == 3 else None


def compare(label, baseline, candidate, required_reduction=None, small=False):
    old = median([r["seconds"] for r in baseline])
    new = median([r["seconds"] for r in candidate])
    complete = old is not None and new is not None
    reduction = 1 - new / old if complete and old else None
    regression_ok = complete and (not small or new - old <= max(old * .10, .020))
    memory_ok = complete and max(r["peak"] for r in candidate) <= max(r["peak"] for r in baseline) + 64 * 1024 * 1024
    equal = complete and all(b["identity"] == c["identity"] for b, c in zip(baseline, candidate))
    return dict(case=label, baseline_seconds=old, candidate_seconds=new, reduction=reduction,
                baseline_peak_bytes=max((r["peak"] for r in baseline), default=None),
                candidate_peak_bytes=max((r["peak"] for r in candidate), default=None),
                complete=complete, equivalent=equal, memory_ok=memory_ok, small_regression_ok=regression_ok,
                passed=complete and equal and regression_ok and memory_ok and
                (required_reduction is None or reduction >= required_reduction))


cases = []
for corpus in ("modern154", "plugin-small-normal", "plugin-small-embedded"):
    for policy in ("conservative", "terrain-first"):
        rows = {"baseline": [], "candidate": []}
        for variant in rows:
            for round_number in range(1, 4):
                path = root / f"plugin-{corpus}-{policy}-r{round_number}-{variant}.stdout.json"
                if args.rerun_root is not None and (args.rerun_root / path.name).exists():
                    path = args.rerun_root / path.name
                if not path.exists():
                    continue
                try:
                    result = load(path)
                    rows[variant].append(dict(seconds=result["AnalysisElapsedSeconds"], peak=result["PeakWorkingSetBytes"],
                                              identity=result["TranscriptSha256"]))
                except (json.JSONDecodeError, KeyError):
                    pass
        cases.append(compare(f"{corpus}/{policy}", rows["baseline"], rows["candidate"],
                             .50 if corpus == "modern154" else None, corpus != "modern154"))

for count in (1000, 10000, 20000):
    rows = {"baseline": [], "candidate": []}
    for variant in rows:
        for round_number in range(1, 4):
            matches = list((root / f"host-n{count}-r{round_number}-{variant}").glob(f"*/n{count}.json"))
            if len(matches) != 1:
                continue
            result = load(matches[0])
            rows[variant].append(dict(seconds=result["ImportTotal"]["ElapsedMs"] / 1000,
                                      peak=result["PeakWorkingSetBytes"],
                                      identity=(result["SpatialTotalRows"], result["SpatialStatusCounts"],
                                                result["SpatialMatchingRows"], result["SpatialNonSpatialRows"])))
    cases.append(compare(f"index/{count}", rows["baseline"], rows["candidate"], .70 if count == 20000 else None,
                         count != 20000))

cancel_path = root / "plugin-candidate-cancel.stdout.json"
cancel = load(cancel_path) if cancel_path.exists() else {}
cancel_ok = cancel.get("Kind") == "plugin-analyze-cancel" and cancel.get("DrainMs", float("inf")) <= 2000
host_cancel_paths = list((root / "host-n20000-candidate-cancel").glob("*/n20000.json"))
host_cancel = load(host_cancel_paths[0]) if len(host_cancel_paths) == 1 else {}
host_cancel_ok = host_cancel.get("ImportCanceled", False) and (host_cancel.get("CancellationDrainMs") or 0) <= 2000
report = dict(cases=cases, rerun_root=str(args.rerun_root) if args.rerun_root else None,
              plugin_cancel=cancel, plugin_cancel_passed=cancel_ok,
              host_cancel_drain_ms=host_cancel.get("CancellationDrainMs"), host_cancel_passed=host_cancel_ok,
              performance_passed=all(c["passed"] for c in cases) and cancel_ok and host_cancel_ok,
              limitation="Performance gates only; correctness tests and UI acceptance are separate required gates.")
(root / "summary.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
print(json.dumps(report, indent=2, ensure_ascii=False))
