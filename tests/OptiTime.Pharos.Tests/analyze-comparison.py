"""Validate and summarize only the six boots named by one Pharos execution."""
import argparse
import hashlib
import json
import re
import statistics
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def summarize(results):
    expected = ['vanilla', 'mod-disabled-before-boot', 'mod-enabled-before-boot',
                'mod-enabled-before-boot', 'mod-disabled-before-boot', 'vanilla']
    rows, evidence, controls, seen = [], [], None, set()
    reference_hours = None
    summary = read(results / 'summary.json')
    if isinstance(summary, dict):
        summary = [summary]
    for index in range(6):
        records = [row for row in summary if row['Run'] == f'performance-{index}']
        if len(records) != 1 or any(records[0][key] != value for key,value in
                {'ExitCode':0,'Total':1,'Passed':1,'Failed':0}.items()):
            raise ValueError(f'performance-{index}: test did not complete successfully')
    for index, variant in enumerate(expected):
        log = results / f'performance-{index}.log'
        matches = re.findall(r'render-step measurements written to (.+)', log.read_text(encoding='utf-8-sig'))
        if len(matches) != 1:
            raise ValueError(f'{log.name}: expected exactly one completed boot')
        directory = Path(matches[0].strip())
        artifact_root = Path(__file__).parent / 'bin/Release/net10.0/artifacts/boot-performance'
        if directory.resolve().parent != artifact_root.resolve():
            raise ValueError(f'{log.name}: measurement directory outside boot artifacts')
        data = read(directory / 'measurements.json')
        if not re.fullmatch('[0-9a-f]{32}', data['run']) or directory.name != data['run']:
            raise ValueError(f'{log.name}: invalid run identifier/directory')
        if data['run'] in seen or data['variant'] != variant:
            raise ValueError(f'{log.name}: repeated boot or wrong variant')
        seen.add(data['run'])
        requested = None if variant == 'vanilla' else variant == 'mod-enabled-before-boot'
        if data['requestedEnabled'] is not requested:
            raise ValueError(f'{log.name}: requested state mismatch')
        if not re.fullmatch('[0-9A-F]{64}', data['modPackageHash']):
            raise ValueError(f'{log.name}: invalid package hash')
        if requested is None:
            if data['configHash'] is not None:
                raise ValueError(f'{log.name}: vanilla has a mod configuration')
        else:
            snapshot = (directory / 'config-after-boot.json').read_bytes().decode('utf-8-sig')
            if not snapshot.endswith('\n') or hashlib.sha256(snapshot[:-1].encode('utf-8')).hexdigest().upper() != data['configHash']:
                raise ValueError(f'{log.name}: configuration snapshot hash mismatch')
            if not requested:
                fixture = read(Path(__file__).parent / 'Fixtures' / 'AllDisabled.json')
                config = json.loads(snapshot)
                if any(config.get(key) is not False for key,value in fixture.items() if value is False):
                    raise ValueError(f'{log.name}: disabled configuration contains enabled options')
        current = {key: data[key] for key in ('pose', 'settings', 'seed', 'playStyle', 'worldType', 'modPackageHash')}
        if controls is not None and controls != current:
            raise ValueError(f'{log.name}: unmatched scene/settings/package')
        controls = current
        if data['worldLitFraction'] < .1 or data['worldColors'] < 32:
            raise ValueError(f'{log.name}: invalid world scene')
        samples = data['samples']
        if len(samples) != 5 or {item['sample'] for item in samples} != set(range(5)):
            raise ValueError(f'{log.name}: incomplete sample windows')
        for sample in samples:
            if reference_hours is None:
                reference_hours = sample['hoursBefore']
            if sample['hoursBefore'] != reference_hours:
                raise ValueError(f'{log.name}: unmatched absolute calendar time')
            if sample['Frames'] != 600 or sample['hoursBefore'] != sample['hoursAfter']:
                raise ValueError(f'{log.name}: interrupted window or calendar drift')
            if sample['positionBefore'] != sample['positionAfter'] or sample['positionBefore'] != data['pose']['Eye']:
                raise ValueError(f'{log.name}: camera drift')
            if not sample['stages']:
                raise ValueError(f'{log.name}: missing measured render stages')
        rows.append(dict(boot=index, variant=variant, run=data['run'],
            medianMs=statistics.mean(item['medianMs'] for item in samples),
            p95Ms=statistics.mean(item['p95Ms'] for item in samples),
            p99Ms=statistics.mean(item['p99Ms'] for item in samples),
            workMeanMs=statistics.mean(item['workMeanMs'] for item in samples),
            clientBytesPerFrame=statistics.mean(item['AllocatedBytesPerFrame'] for item in samples),
            processBytesPerFrame=statistics.mean(item['processAllocatedBytesPerFrame'] for item in samples),
            gc=[sum(item[f'Gen{generation}Collections'] for item in samples) for generation in range(3)],
            maxPendingUpload=max(item[key] for item in samples for key in ('uploadBefore','uploadAfter'))))
        for path in [log, *directory.glob('*')]:
            if path.is_file():
                evidence.append(dict(path=str(path), sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    aggregates = []
    for variant in expected[:3]:
        boots = [row for row in rows if row['variant'] == variant]
        if len(boots) != 2:
            raise ValueError('Each variant must have two independent boots')
        aggregates.append(dict(variant=variant, boots=2, **{
            key: statistics.mean(row[key] for row in boots)
            for key in ('medianMs','p95Ms','p99Ms','workMeanMs','clientBytesPerFrame','processBytesPerFrame')}))
    return dict(controls=controls, rows=rows, aggregate=aggregates, evidence=evidence,
        measurement='Elapsed wall-clock work on the client thread; includes submission/waits, not native FPS, GPU duration or CPU utilization',
        replication='Independent boots, not the five windows within each boot')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('results', type=Path)
    args = parser.parse_args()
    result = summarize(args.results)
    (args.results / 'performance-summary.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    for row in result['aggregate']:
        print(f"{row['variant']:26} median={row['medianMs']:.3f}ms P95={row['p95Ms']:.3f}ms P99={row['p99Ms']:.3f}ms client={row['clientBytesPerFrame']:.0f}B/frame")
