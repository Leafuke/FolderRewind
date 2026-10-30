"""Summarize fixed restore comparisons without treating absent diagnostics as zero."""
import argparse
import json
import re
import statistics
from collections import defaultdict
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('root', type=Path)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
groups = defaultdict(list)
for file in sorted(args.root.rglob('*.json')):
    match = re.fullmatch(r'r([12])-(baseline|candidate)-(.+)', file.parent.name if file.name == 'measurements.json' else file.stem)
    if not match:
        continue
    data = json.loads(file.read_text(encoding='utf-8-sig'))
    if 'rows' not in data:
        continue
    for row in data['rows']:
        key = (int(match[1]), match[2], match[3], row.get('count'), row.get('nested'))
        groups[key].append(row)

def describe(values):
    return dict(median=statistics.median(values), min=min(values), max=max(values), first=values[0])

summaries = []
for key, rows in groups.items():
    item = dict(zip(['round', 'version', 'scenario', 'files', 'nested'], key))
    item.update(samples=len(rows), correct=all(r.get('correct', False) for r in rows))
    for metric in ['fileReadyMs', 'elapsedMs', 'totalMs', 'allocatedBytes']:
        values = [r[metric] for r in rows if metric in r]
        if values: item[metric] = describe(values)
    codes = set(k for r in rows for k in (r.get('diagnostic') or {}).get('Counters', {}))
    item['counters'] = {code: describe([r['diagnostic']['Counters'][code] for r in rows if code in (r.get('diagnostic') or {}).get('Counters', {})]) for code in sorted(codes)}
    stages = set(k for r in rows for k in r.get('stages', {}))
    item['stages'] = {stage: {metric: describe([r['stages'][stage][metric] for r in rows if stage in r.get('stages', {})])
        for metric in ['calls', 'milliseconds']} for stage in sorted(stages)}
    summaries.append(item)
comparisons = []
for before in summaries:
    if before['version'] != 'baseline': continue
    after = next((v for v in summaries if v['version'] == 'candidate' and all(v[k] == before[k] for k in ['round', 'scenario', 'files', 'nested'])), None)
    if after is None: continue
    comparison = {k: before[k] for k in ['round', 'scenario', 'files', 'nested']}
    comparison['valid'] = before['correct'] and after['correct'] and before['samples'] == after['samples'] == 5
    for metric in ['fileReadyMs', 'elapsedMs', 'totalMs', 'allocatedBytes']:
        if metric not in before or metric not in after: continue
        a, b = before[metric]['median'], after[metric]['median']
        comparison[metric] = dict(baseline=a, candidate=b, percent=(b / a - 1) * 100 if a else None)
    comparisons.append(comparison)
    clock = comparison.get('fileReadyMs', comparison.get('elapsedMs'))
    print(f"r{before['round']} {before['scenario']} n={before['files']} nested={before['nested']}: "
          f"{clock['baseline']:.2f} -> {clock['candidate']:.2f} ms ({clock['percent']:+.1f}%), valid={comparison['valid']}")
regressions = []
for first in comparisons:
    if first['round'] != 1: continue
    second = next((v for v in comparisons if v['round'] == 2 and all(v[k] == first[k] for k in ['scenario', 'files', 'nested'])), None)
    if second is None or not first['valid'] or not second['valid']: continue
    for metric in ['fileReadyMs', 'elapsedMs', 'totalMs']:
        if all(metric in row and row[metric]['percent'] > 10 and row[metric]['candidate'] - row[metric]['baseline'] > 20 for row in (first, second)):
            regressions.append({**{k: first[k] for k in ['scenario', 'files', 'nested']}, 'metric': metric})
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(dict(samples=summaries, comparisons=comparisons, sustainedRegressions=regressions), ensure_ascii=False, indent=2), encoding='utf-8')
print('Sustained regressions:', regressions)
