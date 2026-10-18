from pathlib import Path
import subprocess,json,hashlib,base64,datetime,xml.etree.ElementTree as ET
r=Path('/Users/jesse/RiderProjects/MillWorks');d=Path('/tmp/auditcore-dev11-results');delivery=json.loads((d/'local-feed-delivery.json').read_text());assert len(delivery['deliveries'])==20
for x in delivery['deliveries']:assert hashlib.sha256(Path(x['path']).read_bytes()).hexdigest()==x['sha256']
changed=[]
for name in subprocess.check_output(['rg','--files','-g','*.csproj','-g','!**/bin/**','-g','!**/obj/**'],cwd=r,text=True).splitlines():
 p=r/name
 if any(part.lower() in ('evidence','reports') for part in p.parts):continue
 s=p.read_text();tree=ET.fromstring(s)
 if any(n.tag.rsplit('}',1)[-1]=='PackageReference' and (n.get('Include') or '').startswith('MillWorks.AuditCore') and n.get('Version') in ('1.15.4-dev.9','1.15.4-dev.10') for n in tree.iter()):
  import re
  new=re.sub(r'(<PackageReference\s+Include="MillWorks\.AuditCore[^"]*"\s+Version=")1\.15\.4-dev\.(?:9|10)(")',r'\g<1>1.15.4-dev.11\2',s)
  assert new!=s,name
  p.write_text(new);changed.append(name)
cmd=['dotnet','restore','MillWorks.Api/MillWorks.Api.csproj','--disable-parallel','-p:BuildInParallel=false'];env=__import__('os').environ.copy();env['DOTNET_PROCESSOR_COUNT']='2'
with (d/'api-restore.log').open('w') as f:subprocess.run(cmd,cwd=r,env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
a=json.loads((r/'MillWorks.Api/obj/project.assets.json').read_text());packages={k:v for k,v in a['libraries'].items() if k.startswith('MillWorks.AuditCore')};assert len(packages)==5 and all(k.endswith('/1.15.4-dev.11') for k in packages),list(packages)
verified={}
for k,v in packages.items():
 package,version=k.split('/');p=Path('/tmp/auditcore-dev11-packages')/(package+'.'+version+'.nupkg');digest=base64.b64encode(hashlib.sha512(p.read_bytes()).digest()).decode();assert v['sha512']==digest,k;verified[k]={'packageSha512':digest,'matchesPublishedArtifact':True}
(d/'host-resolution.json').write_text(json.dumps({'verifiedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'command':cmd,'exitCode':0,'updatedProjects':changed,'auditPackages':verified,'backgroundJobsPackages':[k for k in a['libraries'] if k.startswith('MillWorks.BackgroundJobs')],'apiBuiltOrRestarted':False},indent=2)+'\n')
print('Updated',len(changed),'project files; API restore resolves all five verified dev.11 packages.')
