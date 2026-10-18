from pathlib import Path
import subprocess, json, hashlib, zipfile, xml.etree.ElementTree as ET
root=Path('/tmp/auditcore-dev12-release').resolve()
results=Path('/tmp/auditcore-dev12-results'); out=Path('/tmp/auditcore-dev12-packages'); out.mkdir(exist_ok=True)
manifest=json.loads((results/'source-manifest.json').read_text())
for name,digest in manifest['files'].items():
 assert hashlib.sha256((root/name).read_bytes()).hexdigest()==digest, f'Source changed: {name}'
import subprocess
expected_commit=manifest['baselineCommit']
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip()==expected_commit, 'Commit changed; reconcile before packing'
tracked=subprocess.check_output(['git','ls-files','-z','--cached','--others','--exclude-standard'],cwd=root).decode().split('\0')
inputs={name for name in tracked if name and (name.startswith(('src/','tests/')) or name in ['Directory.Build.props','Directory.Build.targets','global.json','NuGet.Config','nuget.config']) and (root/name).is_file()}
assert inputs==set(manifest['files']), 'Build inputs added or removed after validation'
projects=[root/'src'/name/(name+'.csproj') for name in ['MillWorks.AuditCore.Abstractions','MillWorks.AuditCore.EntityFramework','MillWorks.AuditCore.Providers','MillWorks.AuditCore.Services','MillWorks.AuditCore.AspNetCore']]
for project in projects:
 dll=project.parent/'bin/Release/net10.0'/(project.stem+'.dll')
 assert ('1.15.4-dev.12+'+expected_commit).encode() in dll.read_bytes(), 'Assembly is not built against the selected commit: '+str(dll)
assert not list(out.iterdir()), 'Refuse to overwrite staged packages'

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
  assert field('version')=='1.15.4-dev.12'
  assert field('releaseNotes').startswith('v1.15.4-dev.12:')
  for dep in meta.findall('.//{*}dependency'):
   if dep.attrib['id'].startswith('MillWorks.AuditCore'):assert dep.attrib['version']=='1.15.4-dev.12', dep.attrib
  dlls={}
  for n in z.namelist():
   if n.endswith('.dll'):
    owners=list((root/'src').glob('*/bin/Release/net10.0/'+Path(n).name))
    # Compare with owning project output, avoiding copied dependency outputs.
    own=next(q for q in owners if q.parts[-5]==Path(n).stem)
    assert z.read(n)==own.read_bytes(),n
    assert ('1.15.4-dev.12+'+expected_commit).encode() in z.read(n), n
    dlls[n]=hashlib.sha256(z.read(n)).hexdigest()
  if field('id')=='MillWorks.AuditCore.EntityFramework':
   data=z.read('lib/net10.0/MillWorks.AuditCore.EntityFramework.dll')
   for migration in [b'20261016210852_AddOutboxOwnershipEvidence',b'20261016211640_RetainOutboxFailureSources',b'20261016230118_AddAuditEnvelopeIdentities']:assert migration in data
  repository=meta.find('.//{*}repository')
  assert repository is not None and repository.attrib.get('commit')==expected_commit, 'Nuspec source commit mismatch'
  packages.append({'file':p.name,'id':field('id'),'version':field('version'),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'dlls':dlls})
assert len(packages)==5
assert len(list(out.glob('*.snupkg')))==5
manifest={'baselineCommit':expected_commit,'provenanceKind':'fingerprinted-working-tree','sourceManifestSha256':hashlib.sha256((results/'source-manifest.json').read_bytes()).hexdigest(),'commands':commands,'packages':packages,'artifacts':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(out.iterdir()) if p.suffix in ['.nupkg','.snupkg']}}
print('Verified five packages, five symbol packages, metadata, internal versions, DLL hashes and all three H1/H3a migrations and source commit.')

for symbols in out.glob('*.snupkg'):
 with zipfile.ZipFile(symbols) as z:
  for n in z.namelist():
   if n.endswith('.pdb'):
    owners=list((root/'src').glob('*/bin/Release/net10.0/'+Path(n).name))
    own=next(q for q in owners if q.parts[-5]==Path(n).stem)
    assert z.read(n)==own.read_bytes(), n
print('Verified symbol PDBs equal the owning Release outputs.')

(results/'package-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
