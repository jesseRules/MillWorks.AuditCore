from pathlib import Path
import subprocess,sys,os,re,json,hashlib
root=Path('/Users/jesse/RiderProjects/MillWorks.AuditCore');out=root/'docs/evidence/audit-pipeline-2026-10/release-dev15';project=root/'src/MillWorks.AuditCore.EntityFramework/MillWorks.AuditCore.EntityFramework.csproj'
def preflight(label):
 raw=subprocess.check_output(['ps','-axo','pid,ppid,etime,%cpu,%mem,rss,command'],text=True);mem=subprocess.check_output(['memory_pressure','-Q'],text=True);free=int(re.search(r'free percentage:\s*(\d+)%',mem)[1]);cpu=sum(float(l.split(None,6)[3]) for l in raw.splitlines()[1:] if len(l.split(None,6))==7);(out/(label+'-preflight.json')).write_text(json.dumps({'free_percent':free,'aggregate_cpu_percent':round(cpu,1),'arguments_saved':False},indent=2)+'\n');print(label,free,'free',round(cpu),'CPU',flush=True)
 if free<20 or cpu>1000:sys.exit('Resource preflight failed')
preflight('migration-admitted' if '--admitted' in sys.argv else 'migration-queued')
if '--admitted' not in sys.argv:sys.exit(subprocess.call(['python3','/Users/jesse/RiderProjects/MillWorks/scripts/dev-work.py','run','--kind','build','--','python3',__file__,'--admitted'],cwd=root))
before=json.loads((out/'migration-before.json').read_text());history=json.loads((out/'history-before.json').read_text());ids={Path(n).stem for n in before if re.match(r'^\d{14}_',Path(n).name) and not n.endswith('.Designer.cs')};assert ids=={x['MigrationId'] for x in history['migrations']},'Migration chain mismatch'
env=os.environ.copy();env['DOTNET_PROCESSOR_COUNT']='1';env['AUDIT_MIGRATION_CONNECTION_STRING']='Server=localhost;Database=AuditPageIndexModelOnly;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=1'
for label,args in [('index-library-build',['dotnet','build',str(project),'-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false']),('index-migration-generate',['dotnet','ef','migrations','add','AddAuditEventPagingIndex','--project',str(project),'--startup-project',str(project),'--context','AuditDbContext','--output-dir','Migrations','--no-build'])]:
 preflight(label)
 with (out/(label+'.log')).open('w') as f:r=subprocess.run(args,cwd=root,env=env,stdout=f,stderr=subprocess.STDOUT)
 print(label,r.returncode,flush=True)
 if r.returncode:print((out/(label+'.log')).read_text()[-2200:]);sys.exit(r.returncode)
for n,h in before.items():
 if n.endswith('ModelSnapshot.cs'):continue
 assert hashlib.sha256((root/n).read_bytes()).hexdigest()==h,n
print('Generated new migration; all20 previous migration bodies/designers preserved.',flush=True)
