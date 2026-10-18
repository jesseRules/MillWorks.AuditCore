from pathlib import Path
import subprocess,os,json,hashlib,xml.etree.ElementTree as ET,datetime,zipfile
root=Path('/Users/jesse/RiderProjects/MillWorks.AuditCore');results=Path('/tmp/auditcore-dev10-results');consumer=Path('/tmp/auditcore-dev10-consumer')
trx=ET.parse(results/'release-dev10.trx');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
c=trx.find('.//t:Counters',ns)
assert c is not None and int(c.attrib['failed'])==1 and int(c.attrib['passed'])>2000,c.attrib
failed=[x.attrib['testName'] for x in trx.findall('.//t:UnitTestResult',ns) if x.attrib['outcome']=='Failed']
assert failed==['ConsumerRollback_AtomicallyRollsBackOutboxRow'],failed
final=ET.parse(results/'release-dev10-final.trx');fc=final.find('.//t:Counters',ns)
assert fc is not None and int(fc.attrib['failed'])==0 and int(fc.attrib['passed'])>40,fc.attrib if fc is not None else 'Missing focused results'
assert any(x.attrib['testName']=='ConsumerRollback_AtomicallyRollsBackOutboxRow' and x.attrib['outcome']=='Passed' for x in final.findall('.//t:UnitTestResult',ns))
fixtures={x.find('t:TestMethod',ns).attrib['className'] for x in final.findall('.//t:UnitTest',ns)}
for required in ['AuditAdministrationServiceTests','AuditAdministrationSqliteTests','AuditTenantRestoreIntegrationTests','AuditArchivalServiceCompressionTests']:
 assert any(x.endswith('.'+required) for x in fixtures),'New fixture missing: '+required
subprocess.run(['python3',str(results/'pack.py')],cwd=root,check=True)
env=os.environ.copy();env['DOTNET_PROCESSOR_COUNT']='2';env['NUGET_AUDIT_MODE']='direct'
commands=[['dotnet','restore',str(consumer/'Smoke.csproj'),'--configfile',str(consumer/'NuGet.Config'),'--packages',str(consumer/'packages'),'-p:NuGetAudit=false'],['dotnet','build',str(consumer/'Smoke.csproj'),'--configuration','Release','--no-restore','-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false'],['dotnet',str(consumer/'bin/Release/net10.0/Smoke.dll')]]
for i,cmd in enumerate(commands):
 with (results/f'consumer-{i}.log').open('w') as f:subprocess.run(cmd,cwd=consumer,env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
assets=json.loads((consumer/'obj/project.assets.json').read_text());packages=sorted(k for k in assets['libraries'] if k.startswith('MillWorks.AuditCore'))
assert len(packages)==5 and all(k.endswith('/1.15.4-dev.10') for k in packages),packages
verification={'recordedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'broadTestsBeforeFixtureCorrection':c.attrib,'finalFocusedTests':fc.attrib,'productionSourceChangedAfterBroadRun':False,'packageOnlyConsumer':{'commands':commands,'auditPackages':packages,'newAdministrationApiAvailable':True,'databaseOpened':False}}
(results/'verification.json').write_text(json.dumps(verification,indent=2)+'\n')
subprocess.run(['python3',str(results/'deliver.py')],cwd=root,check=True)
print('Fresh-cache consumer restored all five dev.10 packages, compiled and ran before delivery to both feeds.')
