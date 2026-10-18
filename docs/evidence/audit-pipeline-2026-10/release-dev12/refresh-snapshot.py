from pathlib import Path
import json,hashlib,shutil,datetime
shared=Path('/Users/jesse/RiderProjects/MillWorks.AuditCore');root=Path('/tmp/auditcore-dev12-release');results=Path('/tmp/auditcore-dev12-results')
p=results/'source-manifest.json';m=json.loads(p.read_text())
changed=[name for name,digest in m['files'].items() if hashlib.sha256((shared/name).read_bytes()).hexdigest()!=digest]
expected='tests/MillWorks.AuditCore.Tests/Integration/SqlServer/OutboxDrainerIntegrationTests.cs'
assert changed==[expected], changed
shutil.copy2(shared/expected,root/expected)
m['files'][expected]=hashlib.sha256((root/expected).read_bytes()).hexdigest()
m['recordedUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
m['reviewFollowup']='Replaced the retry-exhaustion integration test fixed 800ms delay with terminal-state polling, 10s cancellation bound and guaranteed worker shutdown.'
for dest in [p,shared/'docs/evidence/audit-pipeline-2026-10/release-dev12/source-manifest.json']:dest.write_text(json.dumps(m,indent=2)+'\n')
for name in ['release-tests.log','release-dev12.trx']:
 src=results/name
 if src.exists():src.rename(results/(name.replace('.log','-first.log').replace('.trx','-first.trx')))
print('Refreshed only the reviewed test fix in the frozen snapshot.')
