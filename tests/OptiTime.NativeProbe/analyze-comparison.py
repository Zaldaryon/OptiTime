"""Validate native controls and summarize independent boots, never pooled FPS claims."""
import argparse
import json
import statistics
from pathlib import Path


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int((len(ordered) - 1) * fraction))]


def summarize(directory):
    rows = []
    seen_boots = set()
    reference_hours = None
    reference_poses = {}
    reference_settings = {}
    for path in sorted(directory.glob('[0-9]-*.json')):
        if path.name.endswith(('-config.json', '.phase.json')):
            continue
        record = json.loads(path.read_text(encoding='utf-8-sig'))
        if 'phases' not in record:
            continue
        boot, variant = path.stem.split('-', 1)
        if boot in seen_boots:
            raise ValueError(f'{path.name}: repeated boot identifier')
        seen_boots.add(boot)
        if variant not in ('vanilla', 'disabled', 'enabled'):
            raise ValueError(f'{path.name}: unknown variant')
        expected_modes = {'foreground-uncapped', 'foreground-60fps', 'background'}
        modes = [phase['mode'] for phase in record['phases']]
        if len(modes) != 3 or set(modes) != expected_modes:
            raise ValueError(f'{path.name}: missing or repeated phases')
        for phase in record['phases']:
            mode = phase['mode']
            samples = phase['samples']
            expected_focus = mode != 'background'
            if len(samples) < 100 or any(sample['Focused'] != expected_focus for sample in samples):
                raise ValueError(f'{path.name}/{mode}: invalid focus')
            if reference_hours is None:
                reference_hours = phase['startHours']
            if abs(phase['startHours'] - reference_hours) > 0.0001:
                raise ValueError(f'{path.name}/{mode}: unmatched absolute calendar time')
            if abs(phase['endHours'] - phase['startHours']) > 0.0001:
                raise ValueError(f'{path.name}/{mode}: calendar drift')
            start_pose = phase['startPose']
            reference_poses.setdefault(mode, start_pose)
            for pose in (phase['endPose'], reference_poses[mode]):
                if any(abs(a - b) > 0.01 for a, b in zip(start_pose, pose, strict=True)):
                    raise ValueError(f'{path.name}/{mode}: camera mismatch')
            reference_settings.setdefault(mode, phase['settings'])
            if phase['settings'] != reference_settings[mode]:
                raise ValueError(f'{path.name}/{mode}: graphics/settings mismatch')
            cadence = [sample['CadenceMs'] for sample in samples if sample['CadenceMs'] > 0]
            if len(samples) - len(cadence) != 1 or abs(sum(cadence) / 1000 - phase['seconds']) > .2:
                raise ValueError(f'{path.name}/{mode}: interrupted or incomplete measurement window')
            rows.append(dict(
                boot=int(boot), variant=variant, mode=mode, frames=len(samples),
                mean_fps=1000 / statistics.mean(cadence),
                median_ms=statistics.median(cadence),
                p95_ms=percentile(cadence, .95), p99_ms=percentile(cadence, .99),
                handler_median_ms=statistics.median(sample['HandlerMs'] for sample in samples),
                process_bytes_per_frame=statistics.mean(sample['AllocatedBytes'] for sample in samples),
                gc=phase['gcCollections'], settings=phase['settings'], pose=start_pose,
                platform_cap=phase.get('effectivePlatformMaxFps'),
            ))
    aggregate = []
    for variant in ('vanilla', 'disabled', 'enabled'):
        for mode in reference_poses:
            boots = [row for row in rows if row['variant'] == variant and row['mode'] == mode]
            if len(boots) != 2:
                raise ValueError(f'{variant}/{mode}: expected two independent boots, found {len(boots)}')
            aggregate.append(dict(variant=variant, mode=mode, boots=len(boots),
                mean_fps=statistics.mean(row['mean_fps'] for row in boots),
                mean_boot_median_ms=statistics.mean(row['median_ms'] for row in boots),
                mean_boot_p95_ms=statistics.mean(row['p95_ms'] for row in boots)))
    if not rows:
        raise ValueError('No native measurements found')
    return dict(rows=rows, aggregate=aggregate,
        caveat='Independent boot count is the replication unit. This scene is not a general gameplay/FPS estimate; handler timing includes pacing and swap.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('directory', type=Path)
    args = parser.parse_args()
    result = summarize(args.directory)
    output = args.directory / 'summary.json'
    output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8', newline='\n')
    for row in result['aggregate']:
        print(f"{row['variant']:8} {row['mode']:21} boots={row['boots']} "
              f"FPS={row['mean_fps']:.2f} median={row['mean_boot_median_ms']:.3f}ms P95={row['mean_boot_p95_ms']:.3f}ms")
    print(f'Validated raw controls; summary: {output}')
