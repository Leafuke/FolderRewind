"""Export the two predeclared primary restore comparisons; retain all summary groups as JSON."""
import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

parser = argparse.ArgumentParser()
parser.add_argument('--core', type=Path, required=True)
parser.add_argument('--native', type=Path, required=True)
parser.add_argument('--world', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
data = {key: json.loads(path.read_text(encoding='utf-8-sig'))
        for key, path in [('core', args.core), ('native', args.native), ('world', args.world)]}
for group in data.values():
    if not all(row['valid'] for row in group['comparisons']):
        raise ValueError('Incomplete or incorrect cohort cannot be plotted as an accepted comparison.')

fig, axes = plt.subplots(1, 2, figsize=(11, 4.5), layout='constrained')
for axis, group, scenario, title in zip(axes, ['core', 'native'], ['core-smart-20000', 'native'],
                                      ['Core: 20k files, six-layer Smart', 'Native: modern world, six-layer chain']):
    rows = sorted((row for row in data[group]['comparisons'] if row['scenario'] == scenario), key=lambda row: row['round'])
    values = [row['fileReadyMs'][version] / 1000 for row in rows for version in ['baseline', 'candidate']]
    bars = axis.bar(range(4), values, color=['#94a3b8', '#1677a5'] * 2, width=0.62)
    axis.bar_label(bars, fmt='%.2f s', padding=4, fontsize=10)
    axis.set_xticks(range(4), ['R1 baseline', 'R1 candidate', 'R2 baseline', 'R2 candidate'], rotation=15)
    axis.set_ylabel('Median API return time (seconds)')
    axis.set_title(title, fontsize=12, pad=15)
    axis.set_ylim(0, max(values) * 1.25)
    axis.spines[['top', 'right']].set_visible(False)
    axis.yaxis.grid(True, alpha=0.15)
    axis.set_axisbelow(True)
    axis.text(0.5, 0.94, ' / '.join(f"R{row['round']}: {row['fileReadyMs']['percent']:.1f}%" for row in rows),
              transform=axis.transAxes, ha='center', color='#14506b', weight='bold')
fig.suptitle('FolderRewind restore optimization — fixed baseline 4c857a1', fontsize=15)
fig.supxlabel('5 samples per cohort; reverse order in round 2; cache uncontrolled; correctness verified', fontsize=9)
args.output.parent.mkdir(parents=True, exist_ok=True)
fig.savefig(args.output, dpi=170)
args.output.with_suffix('.json').write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
print(args.output)
