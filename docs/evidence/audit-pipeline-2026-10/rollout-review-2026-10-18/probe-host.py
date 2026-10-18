from pathlib import Path
import subprocess,json,datetime,os,importlib.util,hashlib
root=Path(__file__).resolve().parents[4].parent / 'MillWorks'
out=Path(__file__).resolve().parent
r={'observedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'readOnly':True}
p=subprocess.run(['ps','-axo','pid=,lstart=,comm='],text=True,capture_output=True,check=True)
r['hostProcesses']=[line.strip() for line in p.stdout.splitlines() if any(n in line for n in ['MillWorks.Api','MillWorks.ScanWorker','MillWorks.Foundant','AceDs.ApiService','dotnet'])]
r['dotnetHostMappings']=[]
for line in r['hostProcesses']:
 pid=line.split()[0]
 mapped=subprocess.run(['lsof','-p',pid,'-Fn'],capture_output=True,text=True,timeout=8)
 matches=[v[1:] for v in mapped.stdout.splitlines() if v.startswith('n') and any(k in v for k in ['MillWorks.Api.dll','MillWorks.ScanWorker.dll','AceDs.ApiService.dll','MillWorks.AuditCore.Services.dll'])]
 r['dotnetHostMappings'].append({'pid':int(pid),'inspectionExitCode':mapped.returncode,'mappedHostOrAuditModules':sorted(set(matches))})
r['containers']=subprocess.run(['docker','ps','--format','{{.Names}} {{.Ports}}'],text=True,capture_output=True,check=True).stdout.splitlines()
r['auditDlls']=[]
for p in sorted((root/'MillWorks.Api/bin/Debug/net10.0').glob('MillWorks.AuditCore*.dll')):
 r['auditDlls'].append({'name':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'modifiedUtc':datetime.datetime.fromtimestamp(p.stat().st_mtime,datetime.timezone.utc).isoformat()})
spec=importlib.util.spec_from_file_location('observer',root/'scripts/observe-shared-sql.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
try:
 c=m.configured_connection();env=os.environ.copy();env['SQLCMDPASSWORD']=c.get('password',c.get('pwd',''));env['GODEBUG']='x509negativeserial=1'
 sql=(out / 'host-readiness.sql').read_text()
 q=subprocess.run(['sqlcmd','-S','127.0.0.1,1433','-d','MillWorks','-U',c.get('user id',c.get('uid','sa')),'-C','-b','-l','3','-t','12','-h','-1','-y','0','-w','65535','-Q',sql],env=env,capture_output=True,text=True,timeout=20)
 if q.returncode:r['databaseProbe']={'status':'unavailable','exitCode':q.returncode,'detail':'Configured local application SQL on port 1433 could not complete the read-only query; raw output omitted.'}
 else:r['databaseProbe']={'status':'completed','result':json.loads(''.join(q.stdout.splitlines()).strip() or '{}')}
except Exception as e:r['databaseProbe']={'status':'unavailable','errorType':type(e).__name__,'detail':'No database state or migration conclusion is inferred.'}
(out/'host-runtime.json').write_text(json.dumps(r,indent=2)+'\n')
print(json.dumps(r,indent=2))
