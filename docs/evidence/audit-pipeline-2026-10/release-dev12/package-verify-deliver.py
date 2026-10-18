from pathlib import Path
import subprocess,os,json,hashlib,xml.etree.ElementTree as ET,datetime
root=Path('/tmp/auditcore-dev12-release').resolve();results=Path('/tmp/auditcore-dev12-results');consumer=Path('/tmp/auditcore-dev12-consumer')
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};trx=ET.parse(results/'release-dev12.trx');c=trx.find('.//t:Counters',ns)
assert c is not None and int(c.attrib['passed'])>2700,c.attrib if c is not None else 'Missing results'
failures=[x for x in trx.findall('.//t:UnitTestResult',ns) if x.attrib['outcome']=='Failed']
known_issue=None
if failures:
 assert len(failures)==1 and int(c.attrib['failed'])==1
 failure=failures[0]
 assert failure.attrib['testName']=='VerifyChainIntegrityAsync_OverChainOf10000Rows_ReturnsValidResult'
 message=failure.findtext('.//t:Message',default='',namespaces=ns)
 assert 'Microsoft.Data.SqlClient.SqlException' in message and 'Execution Timeout Expired' in message
 prior=json.loads(Path('/Users/jesse/RiderProjects/MillWorks.AuditCore/docs/evidence/audit-pipeline-2026-10/release-dev11/source-manifest.json').read_text())
 paths=['src/MillWorks.AuditCore.EntityFramework/Repositories/AuditIntegrityRepository.cs','src/MillWorks.AuditCore.Services/TamperDetectionService.cs','tests/MillWorks.AuditCore.Tests/Integration/SqlServer/TamperChain10kSqlServerTests.cs']
 assert all(hashlib.sha256((root/name).read_bytes()).hexdigest()==prior['files'][name] for name in paths)
 known_issue={'test':failure.attrib['testName'],'error':'SQL command timeout','affectedSourceUnchangedFromDev11':paths,'status':'Unresolved; H5 load/resource qualification remains open','releaseDecision':'Publish batch/recovery fixes locally with the existing unrelated timeout recorded; no full-suite green claim.'}
else:
 assert int(c.attrib['failed'])==0
(results/'known-issue.json').write_text(json.dumps(known_issue,indent=2)+'\n')
drainer=[x.attrib for x in trx.findall('.//t:UnitTestResult',ns) if x.attrib['testName'] in ('ConsumerRollback_AtomicallyRollsBackOutboxRow','DrainBatch_MarksCompletedRows','ExhaustedRetries_MarksFailedAndIncrementsMeter','FailedRow_SetsNextRetryAt_AndIsSkippedUntilExpired','BatchFailure_DoesNotDuplicateExplicitEvents','EnvelopeId_PreservedThroughOutboxAndDrainer','ExhaustedRetries_RoutesToDlq')]
assert len(drainer)==7 and all(x['outcome']=='Passed' for x in drainer)

regression_prefixes=('RecoveryRevokedAfterReceiptSave', 'RecoveryReceiptRetryRechecks', 'RecoveryRejectedTargetRechecks', 'RecoveryInspectionRechecks')
regressions=[x.attrib for x in trx.findall('.//t:UnitTestResult',ns) if x.attrib['testName'].startswith(regression_prefixes)]
assert len(regressions)==24 and all(x['outcome']=='Passed' for x in regressions), 'All eight recovery cases must pass on SQLite and SQL RCSI OFF/ON'
(results/'recovery-regressions.json').write_text(json.dumps(regressions,indent=2)+'\n')
subprocess.run(['python3',str(results/'pack.py')],cwd=root,check=True)
env=os.environ.copy();env['DOTNET_PROCESSOR_COUNT']='2'
commands=[['dotnet','restore',str(consumer/'Smoke.csproj'),'--configfile',str(consumer/'NuGet.Config'),'--packages',str(consumer/'packages'),'-p:NuGetAudit=false'],['dotnet','build',str(consumer/'Smoke.csproj'),'--configuration','Release','--no-restore','-m:1','-p:BuildInParallel=false','-p:UseSharedCompilation=false'],['dotnet',str(consumer/'bin/Release/net10.0/Smoke.dll')]]
for i,cmd in enumerate(commands):
 with (results/f'consumer-{i}.log').open('w') as f:subprocess.run(cmd,cwd=consumer,env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
assets=json.loads((consumer/'obj/project.assets.json').read_text());packages=sorted(k for k in assets['libraries'] if k.startswith('MillWorks.AuditCore'))
assert len(packages)==5 and all(k.endswith('/1.15.4-dev.12') for k in packages),packages
(results/'verification.json').write_text(json.dumps({'recordedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'tests':c.attrib,'knownIssue':known_issue,'fullSuiteGreen':not bool(failures),'consumerCommands':commands,'auditPackages':packages,'cleanupCompletedApiAvailable':True,'typedOperationConflictAvailable':True,'databaseOpenedByConsumer':False},indent=2)+'\n')
subprocess.run(['python3',str(results/'deliver.py')],cwd=root,check=True)
print('All five dev.12 packages/symbols delivered; isolated consumer proved new guarded-cleanup API before publication.')
