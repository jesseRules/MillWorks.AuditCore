from pathlib import Path
import zipfile,hashlib,json,datetime,subprocess
host=Path('/Users/jesse/RiderProjects/MillWorks');out=Path('/tmp/auditcore-dev11-host-acceptance');b=host/'MillWorks.Api/bin/Debug/net10.0';deps=json.loads((b/'MillWorks.Api.deps.json').read_text());libs=sorted(k for k in deps['libraries'] if k.startswith('MillWorks.AuditCore'));records=[]
for item in libs:
 name,version=item.split('/');assert version=='1.15.4-dev.11'
 package=Path('/Users/jesse/LocalNuGetPackages')/(name+'.'+version+'.nupkg')
 with zipfile.ZipFile(package) as z:
  names=[n for n in z.namelist() if n.startswith('lib/net10.0/') and n.endswith('.dll')];assert len(names)==1
  raw=z.read(names[0]);target=b/Path(names[0]).name;assert raw==target.read_bytes();records.append({'package':item,'dllName':target.name,'dllSha256':hashlib.sha256(raw).hexdigest(),'matchesPublishedPackage':True})
report={'observedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'processId':97343,'processStartedLocal':'2026-10-18 16:33:21 America/Chicago','hostBuiltByUser':True,'agentBuiltOrRestartedHost':False,'auditAssemblies':records,'backgroundJobsPackages':sorted(k for k in deps['libraries'] if k.startswith('MillWorks.BackgroundJobs'))}
for route in ('live','ready'):
 p=subprocess.run(['curl','--insecure','--silent','--show-error','--max-time','15','--write-out','\n%{http_code}','https://127.0.0.1:7204/health/'+route],text=True,capture_output=True,check=True);body,status=p.stdout.rsplit('\n',1);report.setdefault('health',{})[route]={'status':body,'httpStatus':int(status)}
(out/'host-artifacts-and-health.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps({'artifactMatches':len(records),'health':report['health']},indent=2))
