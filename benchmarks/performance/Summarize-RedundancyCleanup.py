"""Compare frozen baseline and candidate measurements, requiring complete rounds."""
import json
import pathlib
import statistics
import sys

run = pathlib.Path(sys.argv[1])
session = pathlib.Path(sys.argv[2])
rows = []
for variant, root in [('baseline', run / 'baseline'), ('candidate', session)]:
    for file in root.glob('history-[123]/run-*/results.json'):
        report = json.loads(file.read_text('utf-8-sig'))
        rows.extend(dict(row, variant=variant) for row in report['rows'])
groups = []
for scenario, size in sorted({(x['scenario'], x['initialPacks']) for x in rows}):
    values = {v: [x for x in rows if x['variant'] == v and x['scenario'] == scenario and x['initialPacks'] == size]
              for v in ['baseline', 'candidate']}
    if any(len(xs) < 3 for xs in values.values()):
        raise RuntimeError('Missing measurement rounds')
    groups.append(dict(scenario=scenario, packs=size, **{
        v: dict(elapsedMsMedian=statistics.median(x['elapsedMs'] for x in xs),
                allocatedBytesMedian=statistics.median(x['allocatedBytes'] for x in xs), observations=len(xs))
        for v, xs in values.items()}))
indexes = []
for size in [500, 2000]:
    values = {v: [p.stat().st_size for p in root.glob(f'history-[123]/run-*/packs-{size}/repository/index/history-index.db')]
              for v, root in [('baseline', run / 'baseline'), ('candidate', session)]}
    if any(len(xs) != 3 for xs in values.values()):
        raise RuntimeError('Missing index measurements')
    medians = {v: statistics.median(xs) for v, xs in values.items()}
    if medians['candidate'] >= medians['baseline']:
        raise RuntimeError('Index footprint did not improve')
    indexes.append(dict(packs=size, **medians))
micro = {v: json.loads((session / f'{v}-micro/results.json').read_text('utf-8-sig')) for v in ['baseline', 'candidate']}
if micro['baseline']['regionSha256'] != micro['candidate']['regionSha256']:
    raise RuntimeError('Different region inputs')
comparisons = []
for scenario in ['deleted-chunk-capture', 'loaded-state-delta', 'object-closure-import']:
    values = {v: [r for r in report['rows'] if r['scenario'] == scenario and not r['warmup']] for v, report in micro.items()}
    metrics = ['elapsedMs', 'allocatedBytes', 'stateBytes'] if scenario != 'object-closure-import' else [
        'elapsedMs', 'allocatedBytes', 'temporaryBundlePeakBytes']
    comparison = {v: {metric: statistics.median(r[metric] for r in rs) for metric in metrics} for v, rs in values.items()}
    if any(len(rs) < 3 for rs in values.values()):
        raise RuntimeError('Insufficient micro rounds')
    if scenario == 'deleted-chunk-capture' and comparison['candidate']['stateBytes'] >= comparison['baseline']['stateBytes']:
        raise RuntimeError('State accounting bytes did not decrease')
    if scenario == 'object-closure-import' and comparison['candidate']['temporaryBundlePeakBytes'] >= comparison['baseline']['temporaryBundlePeakBytes']:
        raise RuntimeError('Import temporary bundle work did not decrease')
    comparisons.append(dict(scenario=scenario, **comparison))
if not any(x['candidate']['allocatedBytesMedian'] < x['baseline']['allocatedBytesMedian'] for x in groups):
    raise RuntimeError('No allocation reduction observed')
result = dict(success=True, session=str(session), rounds=3, warmup=True, history=groups,
              indexBytes=indexes, micro=comparisons,
              limitations='Warm OS cache; synthetic metadata and closure workload plus one frozen real region. Timings are medians; no fixed speedup threshold.')
(run / 'performance/summary.json').write_text(json.dumps(result, indent=2), 'utf-8')
