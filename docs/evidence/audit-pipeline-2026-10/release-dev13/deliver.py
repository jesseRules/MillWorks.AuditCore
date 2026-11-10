from pathlib import Path
import json,hashlib,re,datetime,subprocess
root=Path('/Users/jesse/RiderProjects/MillWorks.AuditCore'); results=Path('/private/tmp/auditcore-dev13-results'); out=Path('/private/tmp/auditcore-dev13-packages-final'); host=Path('/Users/jesse/RiderProjects/MillWorks')
m=json.loads((results/'package-manifest.json').read_text());source=json.loads((results/'source-manifest.json').read_text())
assert json.loads((results/'consumer-verification.json').read_text())['success']
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip()==source['baselineCommit'], 'Source commit changed'
files=subprocess.check_output(['git','ls-files','-z','--cached','--others','--exclude-standard'],cwd=root).decode().split('\0')
inputs={name for name in files if name and (name.startswith(('src/','tests/')) or name in ['Directory.Build.props','Directory.Build.targets','global.json','NuGet.Config','nuget.config']) and (root/name).is_file()}
assert inputs==set(source['files']), 'Build input inventory changed'
for name,digest in source['files'].items():assert hashlib.sha256((root/name).read_bytes()).hexdigest()==digest, 'Source changed: '+name
feeds=[Path('/Users/jesse/LocalNuGetPackages'),host/'local-packages']
for feed in feeds:
 assert feed.is_dir()
 for name,digest in m['artifacts'].items():
  assert not (feed/name).exists(), 'Refuse package overwrite: '+str(feed/name)
  assert hashlib.sha256((out/name).read_bytes()).hexdigest()==digest
# Prepare and validate every caller edit before publishing or editing.
pattern=re.compile(r'(<PackageReference\b[^>]*\bInclude="MillWorks\.AuditCore(?:\.[^"]+)?"[^>]*\bVersion=")1\.15\.4-dev\.12(")')
changes=[]
for p in sorted(host.rglob('*.csproj')):
 if any(x in p.relative_to(host).parts for x in ['Plan','docs','bin','obj','local-packages','.git']):continue
 old=p.read_bytes();text=old.decode();updated,count=pattern.subn(r'\g<1>1.15.4-dev.13\2',text)
 if count:changes.append((p,old,updated.encode(),count))
assert len(changes)==21, 'Reconcile consumer inventory: '+str(len(changes))
previous={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for feed in feeds for p in feed.iterdir() if p.suffix in ['.nupkg','.snupkg']}
receipts=[]
for feed in feeds:
 for name,digest in m['artifacts'].items():
  dest=feed/name
  with dest.open('xb') as f:f.write((out/name).read_bytes())
  assert hashlib.sha256(dest.read_bytes()).hexdigest()==digest
  receipts.append({'path':str(dest),'sha256':digest})
for name,digest in previous.items():assert hashlib.sha256(Path(name).read_bytes()).hexdigest()==digest, 'Existing artifact changed: '+name
edits=[]
for p,old,new,count in changes:
 assert p.read_bytes()==old, 'Consumer changed during preflight: '+str(p)
 p.write_bytes(new)
 edits.append({'path':str(p.relative_to(host)),'referencesUpdated':count,'beforeSha256':hashlib.sha256(old).hexdigest(),'afterSha256':hashlib.sha256(new).hexdigest()})
(results/'local-feed-delivery.json').write_text(json.dumps({'recordedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'version':'1.15.4-dev.13','preservedExistingArtifacts':len(previous),'deliveries':receipts,'consumerEdits':edits},indent=2)+'\n')
print('Published 10 verified fresh artifacts to each feed; preserved',len(previous),'existing artifacts; updated',len(edits),'active project files.')
