"""Validate delivery documents and package evidence; does not test the app."""
import hashlib
import json
from pathlib import Path
import re
import zipfile

ROOT=Path(__file__).resolve().parents[2]
docs=['IMPLEMENTATION-PLAN.md','TEST-MATRIX.md','AGENT-PROMPT.md']
texts={name:(ROOT/name).read_text(encoding='utf-8') for name in docs}
ids=re.findall(r'^\| (T\d\d) \|',texts['TEST-MATRIX.md'],re.M)
assert ids == [f'T{i:02}' for i in range(1,63)], 'Matrix must contain each T01..T62 exactly once'
targets=re.findall(r'^\| ((?:ru|vpn)-[^ ]+) \|.*?\| (https://[^ ]+) \| ([^ ]+) \|$',texts['IMPLEMENTATION-PLAN.md'],re.M)
assert len(targets)==14
assert sum(t[0].startswith('ru-') for t in targets)==7
assert sum(t[0].startswith('vpn-') for t in targets)==7
assert len({t[0] for t in targets})==14
research=ROOT/'tests/network-research-2026-09-06'
fault_path=research/'local-faults-20260906-230550/results.json'
protocol_path=research/'protocols-20260906-230357/samples.json'
faults=json.loads(fault_path.read_text())
protocols=json.loads(protocol_path.read_text())
assert faults['passed'] and len(faults['cases'])==7
assert all(c['passed'] and c['curl_exit']==c['expected_exit'] for c in faults['cases'])
assert len(protocols)==12 and all(p['https_reachable'] and p['icmp_status']=='Success' and p['tcp_status']=='connected' for p in protocols)
assert not list(research.glob('**/scratch-*')), 'Temporary test folders remain'
manifest={
    'scope':'Document/evidence validation only. Net Lights is not implemented.',
    'document_version':re.search(r'Версия: ([0-9.]+)',texts['IMPLEMENTATION-PLAN.md']).group(1), 'date':'2026-09-07',
    'documents':{name:hashlib.sha256((ROOT/name).read_bytes()).hexdigest() for name in docs},
    'planned_acceptance_cases':len(ids), 'configured_targets':len(targets),
    'additional_live_protocol_comparisons':len(protocols),
    'controlled_protocol_fault_cases_passed':len(faults['cases']),
    'production_tests_run':False,
    'observed_tests':[str(fault_path.relative_to(ROOT)),str(protocol_path.relative_to(ROOT))],
    'checks_passed':True}
delivery=ROOT/'handoff';delivery.mkdir(exist_ok=True)
(delivery/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
# Include reports, scripts and already bounded evidence archives. Do not recurse into .git.
files=[ROOT/name for name in docs]+[delivery/'manifest.json']
files += [p for p in research.rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.name!='research-bundle.zip']
archive=delivery/'net-lights-implementation-handoff.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
    for path in files:z.write(path,path.relative_to(ROOT))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert set(docs).issubset(z.namelist())
print(json.dumps(manifest,ensure_ascii=False))
print('Archive:',archive)
