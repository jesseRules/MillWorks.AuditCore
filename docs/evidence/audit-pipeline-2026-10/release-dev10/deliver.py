from pathlib import Path
import json,hashlib,datetime
out=Path('/tmp/auditcore-dev10-packages');results=Path('/tmp/auditcore-dev10-results')
m=json.loads((results/'package-manifest.json').read_text())
feeds=[Path('/Users/jesse/LocalNuGetPackages'),Path('/Users/jesse/RiderProjects/MillWorks/local-packages')]
# Preflight every destination before any publication. Never replace an existing artifact.
for feed in feeds:
 assert feed.is_dir(),str(feed)
 for name,digest in m['artifacts'].items():
  assert not (feed/name).exists(),f'Already exists: {feed/name}'
  assert hashlib.sha256((out/name).read_bytes()).hexdigest()==digest
previous=json.loads((results/'feed-inventory-before.json').read_text())
for name,digest in previous.items():
 assert hashlib.sha256(Path(name).read_bytes()).hexdigest()==digest, 'Existing artifact changed: '+name
receipts=[]
for feed in feeds:
 for name,digest in m['artifacts'].items():
  dest=feed/name
  with dest.open('xb') as f:f.write((out/name).read_bytes())
  assert hashlib.sha256(dest.read_bytes()).hexdigest()==digest
  receipts.append({'path':str(dest),'sha256':digest})
(results/'local-feed-delivery.json').write_text(json.dumps({'recordedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'version':'1.15.4-dev.10','deliveries':receipts},indent=2)+'\n')
for name,digest in previous.items():
 assert hashlib.sha256(Path(name).read_bytes()).hexdigest()==digest, 'Existing artifact changed: '+name
print('Preserved '+str(len(previous))+' existing feed artifacts.')
print('Published and hash-verified 10 artifacts in each of the two local feeds.')
