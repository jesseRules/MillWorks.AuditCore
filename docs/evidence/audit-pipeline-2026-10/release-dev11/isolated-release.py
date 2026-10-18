import subprocess
from pathlib import Path
r=Path('/tmp/auditcore-dev11-release');d=Path('/tmp/auditcore-dev11-results');runner='/Users/jesse/RiderProjects/MillWorks/scripts/dev-work.py'
subprocess.run(['python3',runner,'test',str(r/'tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj'),'--build','--sql','--','--configuration','Release','--logger','trx;LogFileName=release-dev11-current.trx','--results-directory',str(d)],cwd=r,check=True)
subprocess.run(['python3',runner,'run','--kind','build','--','python3',str(d/'package-verify-deliver.py')],cwd=r,check=True)
