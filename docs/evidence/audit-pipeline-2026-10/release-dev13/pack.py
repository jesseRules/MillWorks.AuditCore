from pathlib import Path
import subprocess,hashlib,json,zipfile,xml.etree.ElementTree as ET
root=Path('/private/tmp/auditcore-dev13-release'); results=Path('/private/tmp/auditcore-dev13-results'); out=Path('/private/tmp/auditcore-dev13-packages'); out.mkdir(exist_ok=True)
source=json.loads((results/'source-manifest.json').read_text())
for name,digest in source['files'].items():
 assert hashlib.sha256((root/name).read_bytes()).hexdigest()==digest, name
# Retry uses a fresh staging directory; completed initial artifacts remain untouched.
out=Path('/private/tmp/auditcore-dev13-packages-final');out.mkdir();
projects=['MillWorks.AuditCore.Abstractions','MillWorks.AuditCore.EntityFramework','MillWorks.AuditCore.Providers','MillWorks.AuditCore.Services','MillWorks.AuditCore.AspNetCore']
for name in projects:
 cmd=['dotnet','pack',str(root/'src'/name/(name+'.csproj')),'-c','Release','--no-build','--no-restore','-o',str(out),'-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false','-p:RepositoryCommit='+source['baselineCommit']]
 subprocess.run(cmd,cwd=root,check=True)
packages=[]
for p in sorted(out.glob('*.nupkg')):
 with zipfile.ZipFile(p) as z:
  meta=ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
  assert meta.find('.//{*}version').text=='1.15.4-dev.13'
  for dep in meta.findall('.//{*}dependency'):
   if dep.attrib['id'].startswith('MillWorks.AuditCore'): assert dep.attrib['version']=='1.15.4-dev.13',dep.attrib
  dlls={}
  for n in z.namelist():
   if n.endswith('.dll'):
    expected=root/'src'/Path(n).stem/'bin/Release/net10.0'/Path(n).name
    assert z.read(n)==expected.read_bytes(),n
    dlls[n]=hashlib.sha256(z.read(n)).hexdigest()
  packages.append({'file':p.name,'id':meta.find('.//{*}id').text,'version':'1.15.4-dev.13','dlls':dlls})
assert len(packages)==5 and len(list(out.glob('*.snupkg')))==5
for p in out.glob('*.snupkg'):
 with zipfile.ZipFile(p) as z:
  for n in z.namelist():
   if n.endswith('.pdb'): assert z.read(n)==(root/'src'/Path(n).stem/'bin/Release/net10.0'/Path(n).name).read_bytes()
(results/'package-manifest.json').write_text(json.dumps({'baselineCommit':source['baselineCommit'],'provenanceKind':'fingerprinted-working-tree','packages':packages,'artifacts':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(out.iterdir())}},indent=2)+'\n')
print('Verified five fresh packages, five symbols, internal dependency versions and owning DLL/PDB bytes.')
