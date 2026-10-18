from pathlib import Path
import subprocess,os,json,hashlib,xml.etree.ElementTree as ET,datetime
root=Path('/tmp/auditcore-dev11-release');results=Path('/tmp/auditcore-dev11-results');consumer=Path('/tmp/auditcore-dev11-consumer')
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};trx=ET.parse(results/'release-dev11-current.trx');c=trx.find('.//t:Counters',ns)
assert c is not None and int(c.attrib['failed'])==0 and int(c.attrib['passed'])>2600,c.attrib if c is not None else 'Missing results'
subprocess.run(['python3',str(results/'pack.py')],cwd=root,check=True)
env=os.environ.copy();env['DOTNET_PROCESSOR_COUNT']='2'
commands=[['dotnet','restore',str(consumer/'Smoke.csproj'),'--configfile',str(consumer/'NuGet.Config'),'--packages',str(consumer/'packages'),'-p:NuGetAudit=false'],['dotnet','build',str(consumer/'Smoke.csproj'),'--configuration','Release','--no-restore','-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false'],['dotnet',str(consumer/'bin/Release/net10.0/Smoke.dll')]]
for i,cmd in enumerate(commands):
 with (results/f'consumer-{i}.log').open('w') as f:subprocess.run(cmd,cwd=consumer,env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
assets=json.loads((consumer/'obj/project.assets.json').read_text());packages=sorted(k for k in assets['libraries'] if k.startswith('MillWorks.AuditCore'))
assert len(packages)==5 and all(k.endswith('/1.15.4-dev.11') for k in packages),packages
(results/'verification.json').write_text(json.dumps({'recordedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'tests':c.attrib,'consumerCommands':commands,'auditPackages':packages,'cleanupCompletedApiAvailable':True,'databaseOpenedByConsumer':False},indent=2)+'\n')
subprocess.run(['python3',str(results/'deliver.py')],cwd=root,check=True)
print('All five dev.11 packages/symbols delivered; isolated consumer proved new guarded-cleanup API before publication.')
