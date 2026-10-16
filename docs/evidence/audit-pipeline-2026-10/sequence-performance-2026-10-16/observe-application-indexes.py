import importlib.util,json,os,subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('mw_observer','/Users/jesse/RiderProjects/MillWorks/scripts/observe-shared-sql.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);parts=module.configured_connection()
sql="""
SET NOCOUNT ON; SET LOCK_TIMEOUT 2000;
SELECT SYSUTCDATETIME() AS ObservedUtc, DB_NAME() AS DatabaseName,
 CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')) AS SqlVersion,
 (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id=DB_ID()) AS RcsiEnabled,
 (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id=OBJECT_ID('audit.AuditIntegrity') AND p.index_id IN (0,1)) AS IntegrityRows,
 JSON_QUERY((SELECT i.name,i.type_desc,i.is_unique,i.is_disabled,i.has_filter,
 JSON_QUERY((SELECT c.name,ic.key_ordinal,ic.is_included_column FROM sys.index_columns ic
 JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
 WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id ORDER BY ic.index_column_id FOR JSON PATH)) AS Columns
 FROM sys.indexes i WHERE i.object_id=OBJECT_ID('audit.AuditIntegrity') FOR JSON PATH)) AS IntegrityIndexes
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
"""
env=os.environ.copy();env['SQLCMDPASSWORD']=parts.get('password',parts.get('pwd',''));env['GODEBUG']='x509negativeserial=1'
r=subprocess.run(['sqlcmd','-S','127.0.0.1,1433','-d','MillWorks','-U',parts.get('user id',parts.get('uid','sa')),'-C','-b','-l','3','-t','5','-h','-1','-y','0','-Q',sql],env=env,capture_output=True,text=True,timeout=12)
if r.returncode:raise RuntimeError('Read-only index inventory failed; output omitted.')
data=json.loads(r.stdout.strip());data['ConfiguredPooling']=parts.get('pooling','true (SqlClient default)');data['ConfigurationIsNotLoadedRuntimeProof']=True
out=Path('/tmp/audit-sequence-performance-20261016');out.mkdir(exist_ok=True);(out/'application-index-inventory.json').write_text(json.dumps(data,indent=2)+'\n');print(json.dumps(data,indent=2))
