import subprocess,json
from pathlib import Path
root=Path('/Users/jesse/RiderProjects/MillWorks');results=Path('/tmp/auditcore-dev9-results')
cmd=['dotnet','restore',str(root/'MillWorks.Api/MillWorks.Api.csproj'),'--disable-parallel']
with (results/'host-restore.log').open('w') as log:
 r=subprocess.run(cmd,cwd=root,stdout=log,stderr=subprocess.STDOUT)
if r.returncode:raise SystemExit(r.returncode)
p=root/'MillWorks.Api/obj/project.assets.json';d=json.loads(p.read_text())
resolved={name:value.get('sha512') for name,value in d['libraries'].items() if name.startswith('MillWorks.AuditCore')}
assert len(resolved)==5 and all(name.endswith('/1.15.4-dev.9') for name in resolved),resolved
(results/'host-resolution.json').write_text(json.dumps({'operation':'restore only; no API build/start','resolved':resolved},indent=2)+'\n')
print('Restored MillWorks.Api dependencies without building; all five AuditCore packages resolve to dev.9.')
