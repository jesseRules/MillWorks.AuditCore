from pathlib import Path
import subprocess,os,json,hashlib
results=Path('/tmp/auditcore-dev9-results');root=Path('/Users/jesse/RiderProjects/MillWorks.AuditCore')
import xml.etree.ElementTree as ET
trx=ET.parse(results/'release-dev9.trx');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
c=trx.find('.//t:Counters',ns);assert c is not None and int(c.attrib['failed'])==0 and int(c.attrib['passed'])>2600,c.attrib if c is not None else 'Missing results'
subprocess.run(['python3',str(results/'pack.py')],check=True,cwd=root)
env=os.environ.copy();env['DOTNET_PROCESSOR_COUNT']='2'
consumer=Path('/tmp/auditcore-dev9-consumer')
cmd=['dotnet','build',str(consumer/'Smoke.csproj'),'--configuration','Release','-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false']
with (results/'consumer-build.log').open('w') as f:subprocess.run(cmd,cwd=consumer,env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
with (results/'consumer-run.log').open('w') as f:subprocess.run(['dotnet',str(consumer/'bin/Release/net10.0/Smoke.dll')],cwd=consumer,env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
subprocess.run(['python3',str(results/'deliver.py')],cwd=root,check=True)
print('Package-only consumer passed before delivery; published only after verification.')
