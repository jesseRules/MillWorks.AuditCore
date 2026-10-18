from pathlib import Path
import importlib.util,subprocess,os,json,datetime
host=Path('/Users/jesse/RiderProjects/MillWorks');d=Path('/tmp/auditcore-dev11-host-acceptance')
spec=importlib.util.spec_from_file_location('observer',host/'scripts/observe-shared-sql.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);c=m.configured_connection()
env=os.environ.copy();env['SQLCMDPASSWORD']=c.get('password',c.get('pwd',''));env['GODEBUG']='x509negativeserial=1'
p=subprocess.run(['sqlcmd','-S','127.0.0.1,1433','-d','MillWorks','-U',c.get('user id',c.get('uid','sa')),'-C','-b','-l','3','-t','15','-h','-1','-y','0','-w','65535','-i',str(d/'live-audit-observation.sql')],env=env,capture_output=True,text=True,timeout=25)
if p.returncode:
 import re
 print(json.dumps({'sqlErrorNumbers':re.findall(r'Msg ([0-9]+)',p.stdout+p.stderr),'exitCode':p.returncode}));raise SystemExit(1)
data=json.loads(''.join(p.stdout.splitlines()));(d/'live-audit-observation.json').write_text(json.dumps({'readOnly':True,'result':data},indent=2)+'\n');print(json.dumps(data,indent=2))
