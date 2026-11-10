from pathlib import Path
import subprocess,sys,os,json,re,hashlib,datetime,zipfile,base64
root=Path('/Users/jesse/RiderProjects/MillWorks.AuditCore');host=Path('/Users/jesse/RiderProjects/MillWorks');out=root/'docs/evidence/audit-pipeline-2026-10/release-dev15';stage=Path('/private/tmp/auditcore-dev15-packages');consumer=Path('/private/tmp/auditcore-dev15-consumer');version='1.15.4-dev.15'
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def preflight(label):
 raw=subprocess.check_output(['ps','-axo','pid,ppid,etime,%cpu,%mem,rss,command'],text=True);mem=subprocess.check_output(['memory_pressure','-Q'],text=True);free=int(re.search(r'free percentage:\s*(\d+)%',mem)[1]);cpu=sum(float(l.split(None,6)[3]) for l in raw.splitlines()[1:] if len(l.split(None,6))==7);(out/(label+'-preflight.json')).write_text(json.dumps({'free_percent':free,'aggregate_cpu_percent':round(cpu,1),'arguments_saved':False},indent=2)+'\n');print(f'{label}: free {free}%, CPU {cpu:.1f}%; arguments private.',flush=True)
 if free<20 or cpu>1000:sys.exit('Resource preflight failed')
preflight('adopt-admitted' if '--admitted' in sys.argv else 'adopt-queued')
if '--admitted' not in sys.argv:sys.exit(subprocess.call(['python3',str(host/'scripts/dev-work.py'),'run','--kind','build','--','python3',__file__,'--admitted']+(['--resume-delivery'] if '--resume-delivery' in sys.argv else []),cwd=root))
env=os.environ.copy();env['DOTNET_PROCESSOR_COUNT']='1'
def run(label,args,cwd):
 preflight(label)
 with (out/(label+'.log')).open('w') as f:r=subprocess.run(args,cwd=cwd,env=env,stdout=f,stderr=subprocess.STDOUT)
 print(label,'exit',r.returncode,flush=True)
 if r.returncode:print((out/(label+'.log')).read_text()[-3500:]);sys.exit(r.returncode)
if '--resume-delivery' not in sys.argv:
 run('consumer-build',['dotnet','build','Smoke.csproj','-c','Release','-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false'],consumer)
 run('consumer-run',['dotnet',str(consumer/'bin/Release/net10.0/Smoke.dll')],consumer)
 print((out/'consumer-run.log').read_text(),flush=True)
 project=Path('/private/tmp/auditcore-dev15-release/src/MillWorks.AuditCore.EntityFramework/MillWorks.AuditCore.EntityFramework.csproj')
 env['AUDIT_MIGRATION_CONNECTION_STRING']='Server=localhost;Database=AuditPagingScriptModelOnly;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=1'
 for label,start,end in [('paging-up','AddAuditEventWordSearch','AddAuditEventPagingIndex'),('paging-down','AddAuditEventPagingIndex','AddAuditEventWordSearch')]:
  run(label,['dotnet','ef','migrations','script',start,end,'--project',str(project),'--startup-project',str(project),'--context','AuditDbContext','--configuration','Release','--no-build','--output',str(out/(label+'.sql'))],project.parent)
else:
 assert 'PASS:' in (out/'consumer-run.log').read_text()
 assert 'CREATE INDEX [IX_AuditEvents_Date_EventId]' in (out/'paging-up.sql').read_text()
 assert 'DROP INDEX [IX_AuditEvents_Date_EventId]' in (out/'paging-down.sql').read_text()
m=json.loads((out/'package-manifest.json').read_text());manifest=json.loads((out/'source-manifest.json').read_text())['files']
for n,h in manifest.items():assert digest(root/n)==h, 'Source changed: '+n
pattern=re.compile(r'(<PackageReference\b[^>]*\bInclude="MillWorks\.AuditCore(?:\.[^"]+)?"[^>]*\bVersion=")1\.15\.4-dev\.14(")')
edits=[]
for p in sorted(host.rglob('*.csproj')):
 if any(x in p.relative_to(host).parts for x in ['Plan','docs','bin','obj','local-packages','.git']):continue
 old=p.read_bytes();text=old.decode();new,count=pattern.subn(r'\g<1>'+version+r'\2',text)
 if count:edits.append((p,old,new.encode(),count))
assert len(edits)==22, 'Reconcile active consumer inventory: '+str(len(edits))
feeds=[Path('/Users/jesse/LocalNuGetPackages'),host/'local-packages'];deliveries=[]
for feed in feeds:
 for n,h in m['artifacts'].items():assert not (feed/n).exists() and digest(stage/n)==h, 'Refuse artifact overwrite/mismatch: '+n
for feed in feeds:
 for n,h in m['artifacts'].items():
  p=feed/n
  with p.open('xb') as f:f.write((stage/n).read_bytes())
  assert digest(p)==h;deliveries.append({'path':str(p),'sha256':h})
changed=[]
for p,old,new,count in edits:
 assert p.read_bytes()==old, 'Consumer changed: '+str(p)
 p.write_bytes(new);changed.append({'path':str(p.relative_to(host)),'references_updated':count,'before_sha256':hashlib.sha256(old).hexdigest(),'after_sha256':digest(p)})
(out/'delivery.json').write_text(json.dumps({'version':version,'deliveries':deliveries,'consumer_edits':changed,'existing_artifacts_overwritten':False},indent=2)+'\n')
print('Published packages and symbols to both local feeds; updated22 active consumer project files.',flush=True)
run('api-restore',['dotnet','restore','MillWorks.Api/MillWorks.Api.csproj','--disable-parallel','-p:BuildInParallel=false'],host)
assets=json.loads((host/'MillWorks.Api/obj/project.assets.json').read_text());resolved=[]
for name,data in assets['libraries'].items():
 if name.lower().startswith('millworks.auditcore'):
  package_id,v=name.split('/');assert v==version,(name,'old package resolved')
  match=next(p for p in stage.glob('*.nupkg') if p.name.lower()==(package_id+'.'+v+'.nupkg').lower());sha=base64.b64encode(hashlib.sha512(match.read_bytes()).digest()).decode();assert data['sha512']==sha,(name,'package hash mismatch');resolved.append({'package':name,'sha512':sha})
assert len(resolved)==5
(out/'host-resolution.json').write_text(json.dumps({'resolved':resolved,'api_compiled':False,'restore_exit_code':0},indent=2)+'\n');print('API restore resolves all five dev.15 packages with matching published SHA512 hashes. API not compiled.',flush=True)
