from pathlib import Path
import subprocess,sys,os,json,re,hashlib,shutil,datetime,zipfile,xml.etree.ElementTree as E
source=Path('/Users/jesse/RiderProjects/MillWorks.AuditCore');root=Path('/private/tmp/auditcore-dev14-release');out=source/'docs/evidence/audit-pipeline-2026-10/release-dev14';out.mkdir(exist_ok=True,parents=True)
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def preflight(label):
 raw=subprocess.check_output(['ps','-axo','pid,ppid,etime,%cpu,%mem,rss,command'],text=True);mem=subprocess.check_output(['memory_pressure','-Q'],text=True);free=int(re.search(r'free percentage:\s*(\d+)%',mem)[1]);cpu=sum(float(l.split(None,6)[3]) for l in raw.splitlines()[1:] if len(l.split(None,6))==7);(out/(label+'-preflight.json')).write_text(json.dumps({'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'free_percent':free,'aggregate_cpu_percent':round(cpu,1),'arguments_saved':False},indent=2)+'\n');print(f'{label}: free {free}%, CPU {cpu:.1f}%; arguments private.',flush=True)
 if free<20 or cpu>1000:sys.exit('Resource preflight failed')
preflight('admitted' if '--admitted' in sys.argv else 'queued')
if '--admitted' not in sys.argv:sys.exit(subprocess.call(['python3','/Users/jesse/RiderProjects/MillWorks/scripts/dev-work.py','run','--kind','build','--','python3',__file__,'--admitted'],cwd=source))
assert not root.exists();root.mkdir()
for name in ['src','tests']:
 shutil.copytree(source/name,root/name,ignore=shutil.ignore_patterns('bin','obj'))
for p in source.iterdir():
 if p.is_file() and (p.suffix in ['.props','.targets','.sln','.slnx'] or p.name.lower() in ['nuget.config','global.json']):shutil.copy2(p,root/p.name)
files=[p for p in root.rglob('*') if p.is_file()];manifest={str(p.relative_to(root)):digest(p) for p in files};(out/'source-manifest.json').write_text(json.dumps({'baseline_commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=source,text=True).strip(),'files':manifest},indent=2)+'\n')
env=os.environ.copy();env['DOTNET_PROCESSOR_COUNT']='1'
def run(label,args):
 preflight(label)
 with (out/(label+'.log')).open('w') as f:r=subprocess.run(args,cwd=root,env=env,stdout=f,stderr=subprocess.STDOUT)
 print(label, 'exit',r.returncode,flush=True)
 if r.returncode:print((out/(label+'.log')).read_text()[-4500:]);sys.exit(r.returncode)
filter='FullyQualifiedName~AuditEventPagingTests|FullyQualifiedName~AuditWordSearchTests|FullyQualifiedName~AuditSearchServiceTests|FullyQualifiedName~AuditSearchServiceEdgeCaseTests|FullyQualifiedName~AuditSearchNullableDurationTests|FullyQualifiedName~AuditServiceUnitTests|FullyQualifiedName~SecurityFindingsFixTests|FullyQualifiedName~SearchAndQueryIntegrationTests'
run('release-tests',['dotnet','test','tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj','-c','Release','-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false','--filter',filter,'--logger','trx;LogFileName=release.trx','--results-directory',str(out)])
# Tests do not reference the AspNetCore delivery facade; build that owning library as well.
run('facade-build',['dotnet','build','src/MillWorks.AuditCore.AspNetCore/MillWorks.AuditCore.AspNetCore.csproj','-c','Release','-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false'])
packages=Path('/private/tmp/auditcore-dev14-packages');packages.mkdir()
for name in ['Abstractions','EntityFramework','Providers','Services','AspNetCore']:
 full='MillWorks.AuditCore.'+name
 run('pack-'+name,['dotnet','pack',str(root/'src'/full/(full+'.csproj')),'-c','Release','--no-build','--no-restore','-m:1','-o',str(packages),'-p:BuildInParallel=false'])
assert len(list(packages.glob('*.nupkg')))==5
receipt=[]
for p in packages.glob('*.nupkg'):
 with zipfile.ZipFile(p) as z:
  meta=E.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))));assert meta.find('.//{*}version').text=='1.15.4-dev.14'
  for dep in meta.findall('.//{*}dependency'):
   if dep.get('id').startswith('MillWorks.AuditCore'):assert dep.get('version')=='1.15.4-dev.14',dep.attrib
  dlls={}
  for n in z.namelist():
   if n.endswith('.dll'):
    b=z.read(n);assert b==(root/'src'/Path(n).stem/'bin/Release/net10.0'/Path(n).name).read_bytes();dlls[n]=hashlib.sha256(b).hexdigest()
    if Path(n).stem=='MillWorks.AuditCore.EntityFramework':assert b'AuditEventPaging' in b and b'AddAuditEventWordSearch' in b
    if Path(n).stem=='MillWorks.AuditCore.Services':assert b'AuditEventPaging' in b
  receipt.append({'file':p.name,'sha256':digest(p),'dlls':dlls})
for n,h in manifest.items():assert digest(root/n)==h and digest(source/n)==h, 'Build input changed: '+n
(out/'package-manifest.json').write_text(json.dumps({'version':'1.15.4-dev.14','packages':receipt,'artifacts':{p.name:digest(p) for p in packages.iterdir()}},indent=2)+'\n')
print('Verified five dev.14 packages, dependencies, DLLs, paging helper and migration. Source unchanged.',flush=True)
