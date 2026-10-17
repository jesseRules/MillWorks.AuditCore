import importlib.util,os,subprocess,json,hashlib,uuid
from pathlib import Path
spec=importlib.util.spec_from_file_location('observer','/Users/jesse/RiderProjects/MillWorks/scripts/observe-shared-sql.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);p=m.configured_connection()
sql="""SET NOCOUNT ON; SET LOCK_TIMEOUT 2000;
SELECT TOP (100) l.EnvelopeId,l.AdditionalData FROM audit.AuditLogs l
WHERE l.EntityType='IdentityRoleClaim`1' AND l.Action=1 AND ISJSON(l.AdditionalData)=1
AND NOT EXISTS(SELECT 1 FROM audit.AuditEnvelopeIdentities i WHERE i.Channel='entity' AND i.LogicalId=l.EnvelopeId)
AND EXISTS(SELECT 1 FROM [identity].AspNetRoleClaims c WHERE c.RoleId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(l.AdditionalData,'$.RoleId'))
AND c.ClaimType=JSON_VALUE(l.AdditionalData,'$.ClaimType') AND c.ClaimValue=JSON_VALUE(l.AdditionalData,'$.ClaimValue'))
ORDER BY l.CreatedAt DESC FOR JSON PATH;
"""
env=os.environ.copy();env['SQLCMDPASSWORD']=p.get('password',p.get('pwd',''));env['GODEBUG']='x509negativeserial=1'
r=subprocess.run(['sqlcmd','-S','127.0.0.1,1433','-d','MillWorks','-U',p.get('user id',p.get('uid','sa')),'-C','-b','-l','3','-t','15','-h','-1','-y','0','-w','65535','-Q',sql],env=env,capture_output=True,text=True,timeout=22)
if r.returncode: raise RuntimeError('Read-only claim identity check failed; output omitted.')
# sqlcmd may wrap JSON output across lines. Do not retain or print claim payloads.
rows=json.loads(''.join(r.stdout.splitlines()).strip() or '[]')
matched=0;temp=0
for row in rows:
 raw=row['AdditionalData']; key=json.loads(raw).get('Id'); inp=f'IdentityRoleClaim`1||{key}|Created||{raw}'
 recomputed=str(uuid.UUID(bytes_le=hashlib.sha256(inp.encode()).digest()[:16]))
 matched+=recomputed.lower()==row['EnvelopeId'].lower(); temp+=str(key).startswith('-')
report={'sampleLimit':100,'historicalSnapshotsMatchingCurrentRoleClaims':len(rows),'recomputedEnvelopeIdsMatch':matched,'temporaryNegativeKeySnapshots':temp,'queryWasReadOnly':True,'payloadsRetained':False,'doesNotIdentifyExactRuntimeExceptionId':True}
Path('/tmp/millworks-audit-dev8-integration/claim-identity-collision.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
