from pathlib import Path
import subprocess,sys,os,re,json,hashlib
root=Path('/Users/jesse/RiderProjects/MillWorks.AuditCore');out=root/'docs/evidence/audit-pipeline-2026-10/release-dev15';project=root/'src/MillWorks.AuditCore.EntityFramework/MillWorks.AuditCore.EntityFramework.csproj'
def preflight(label):
 raw=subprocess.check_output(['ps','-axo','pid,ppid,etime,%cpu,%mem,rss,command'],text=True);mem=subprocess.check_output(['memory_pressure','-Q'],text=True);free=int(re.search(r'free percentage:\s*(\d+)%',mem)[1]);cpu=sum(float(l.split(None,6)[3]) for l in raw.splitlines()[1:] if len(l.split(None,6))==7);(out/(label+'-preflight.json')).write_text(json.dumps({'free_percent':free,'aggregate_cpu_percent':round(cpu,1),'arguments_saved':False},indent=2)+'\n');print(label,free,'free',round(cpu),'CPU',flush=True)
 if free<20 or cpu>1000:sys.exit('Resource preflight failed')
preflight('migration-admitted' if '--admitted' in sys.argv else 'migration-queued')
if '--admitted' not in sys.argv:sys.exit(subprocess.call(['python3','/Users/jesse/RiderProjects/MillWorks/scripts/dev-work.py','run','--kind','build','--','python3',__file__,'--admitted'],cwd=root))
env=os.environ.copy();env['DOTNET_PROCESSOR_COUNT']='1';env['MILLWORKS_SQL_TESTS_ENABLED']='1'
preflight('paging-index-tests')
args=['dotnet','test',str(root/'tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj'),'-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false','--filter','FullyQualifiedName~AuditEventPagingIndexSqlServerTests|FullyQualifiedName~AuditEventPagingTests','--logger','trx;LogFileName=paging-index.trx','--results-directory',str(out)]
with (out/'paging-index-tests.log').open('w') as f:r=subprocess.run(args,cwd=root,env=env,stdout=f,stderr=subprocess.STDOUT)
print((out/'paging-index-tests.log').read_text()[-4000:]);sys.exit(r.returncode)
