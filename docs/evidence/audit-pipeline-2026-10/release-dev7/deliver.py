from pathlib import Path
import json,hashlib,datetime
out=Path('/tmp/auditcore-dev7-packages');results=Path('/tmp/auditcore-dev7-results')
m=json.loads((results/'package-manifest.json').read_text())
feeds=[Path('/Users/jesse/LocalNuGetPackages'),Path('/Users/jesse/RiderProjects/MillWorks/local-packages')]
# Preflight every destination before any publication. Never replace an existing artifact.
for feed in feeds:
 assert feed.is_dir(),str(feed)
 for name,digest in m['artifacts'].items():
  assert not (feed/name).exists(),f'Already exists: {feed/name}'
  assert hashlib.sha256((out/name).read_bytes()).hexdigest()==digest
receipts=[]
for feed in feeds:
 for name,digest in m['artifacts'].items():
  dest=feed/name
  with dest.open('xb') as f:f.write((out/name).read_bytes())
  assert hashlib.sha256(dest.read_bytes()).hexdigest()==digest
  receipts.append({'path':str(dest),'sha256':digest})
(results/'local-feed-delivery.json').write_text(json.dumps({'recordedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'version':'1.15.4-dev.7','deliveries':receipts},indent=2)+'\n')
print('Published and hash-verified 10 artifacts in each of the two local feeds.')
