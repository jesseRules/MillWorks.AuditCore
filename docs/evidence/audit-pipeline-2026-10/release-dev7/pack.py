from pathlib import Path
import subprocess, json, hashlib, zipfile, xml.etree.ElementTree as ET
root=Path('/Users/jesse/RiderProjects/MillWorks.AuditCore')
results=Path('/tmp/auditcore-dev7-results'); out=Path('/tmp/auditcore-dev7-packages'); out.mkdir(exist_ok=True)
manifest=json.loads((results/'source-manifest.json').read_text())
for name,digest in manifest['files'].items():
 assert hashlib.sha256((root/name).read_bytes()).hexdigest()==digest, f'Source changed: {name}'
projects=sorted((root/'src').glob('*/*.csproj'))
commands=[]
for project in projects:
 cmd=['dotnet','pack',str(project),'--configuration','Release','--no-build','--no-restore','--output',str(out),'-p:BuildInParallel=false','-p:UseSharedCompilation=false']
 commands.append(cmd);subprocess.run(cmd,cwd=root,check=True)
packages=[]
for p in sorted(out.glob('*.nupkg')):
 with zipfile.ZipFile(p) as z:
  meta=ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
  ns={'n':'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'}
  def field(tag): return meta.find('.//{*}'+tag).text
  assert field('version')=='1.15.4-dev.7'
  assert field('releaseNotes').startswith('v1.15.4-dev.7:')
  for dep in meta.findall('.//{*}dependency'):
   if dep.attrib['id'].startswith('MillWorks.AuditCore'):assert dep.attrib['version']=='1.15.4-dev.7', dep.attrib
  dlls={}
  for n in z.namelist():
   if n.endswith('.dll'):
    owners=list((root/'src').glob('*/bin/Release/net10.0/'+Path(n).name))
    # Compare with owning project output, avoiding copied dependency outputs.
    own=next(q for q in owners if q.parts[-5]==Path(n).stem)
    assert z.read(n)==own.read_bytes(),n
    dlls[n]=hashlib.sha256(z.read(n)).hexdigest()
  if field('id')=='MillWorks.AuditCore.EntityFramework':
   data=z.read('lib/net10.0/MillWorks.AuditCore.EntityFramework.dll')
   for migration in [b'20261016210852_AddOutboxOwnershipEvidence',b'20261016211640_RetainOutboxFailureSources']:assert migration in data
  packages.append({'file':p.name,'id':field('id'),'version':field('version'),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'dlls':dlls})
assert len(packages)==5
assert len(list(out.glob('*.snupkg')))==5
manifest={'commands':commands,'packages':packages,'artifacts':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(out.iterdir()) if p.suffix in ['.nupkg','.snupkg']}}
(results/'package-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print('Verified five packages, five symbol packages, metadata, internal versions, DLL hashes and both H1 migrations.')
