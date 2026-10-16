import importlib.util,json,os,subprocess
from pathlib import Path
spec=importlib.util.spec_from_file_location('mw_observer','/Users/jesse/RiderProjects/MillWorks/scripts/observe-shared-sql.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
parts=module.configured_connection()
sql="""
SET NOCOUNT ON; SET LOCK_TIMEOUT 2000;
SELECT SYSUTCDATETIME() AS ObservedUtc, DB_NAME() AS DatabaseName,
 CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')) AS SqlVersion,
 (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id=DB_ID()) AS RcsiEnabled,
 (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id=OBJECT_ID('audit.AuditIntegrity') AND p.index_id IN (0,1)) AS IntegrityRows,
 JSON_QUERY((SELECT TOP (10) qs.last_execution_time AS LastExecutionTimeServerLocal,
 qs.execution_count AS Executions,qs.last_elapsed_time / 1000.0 AS LastElapsedMs,
 qs.last_logical_reads AS LastLogicalReads,qs.last_rows AS LastRows,
 SUBSTRING(st.text,qs.statement_start_offset/2+1,
 (CASE WHEN qs.statement_end_offset=-1 THEN DATALENGTH(st.text) ELSE qs.statement_end_offset END-qs.statement_start_offset)/2+1) AS Statement
 FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
 WHERE EXISTS (SELECT 1 FROM sys.dm_exec_plan_attributes(qs.plan_handle) pa WHERE pa.attribute='dbid' AND CONVERT(int,pa.value)=DB_ID()) AND st.text LIKE '%AuditIntegrity%'
 AND st.text LIKE '%COUNT_BIG%' AND st.text LIKE '%DISTINCT%'
 AND st.text NOT LIKE '%dm_exec_query_stats%'
 ORDER BY qs.last_execution_time DESC FOR JSON PATH)) AS CachedAggregates
FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
"""
env=os.environ.copy();env['SQLCMDPASSWORD']=parts.get('password',parts.get('pwd',''));env['GODEBUG']='x509negativeserial=1'
result=subprocess.run(['sqlcmd','-S','127.0.0.1,1433','-d','MillWorks','-U',parts.get('user id',parts.get('uid','sa')),'-C','-b','-l','3','-t','5','-h','-1','-y','0','-Q',sql],env=env,capture_output=True,text=True,timeout=12)
if result.returncode: raise RuntimeError('Read-only application SQL observation failed; output omitted to protect configuration.')
text=result.stdout.strip();data=json.loads(text)
path=Path('/tmp/audit-sequence-sqlserver-20261016/application-cache-observation.json');path.write_text(json.dumps(data,indent=2)+'\n')
print(json.dumps(data,indent=2))
