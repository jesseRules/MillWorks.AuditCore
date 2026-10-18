from pathlib import Path
import importlib.util,json,os,uuid,datetime
root=Path('/Users/jesse/RiderProjects/MillWorks');out=Path('/tmp/auditcore-dev11-host-acceptance/live-http.json')
spec=importlib.util.spec_from_file_location('fixture_http',root/'scripts/e2e/prediction_outcome_fixture/client.py');http=importlib.util.module_from_spec(spec);spec.loader.exec_module(http)
report={'startedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'origin':'https://localhost:4200','checks':[],'sessions':[]}
def record(label,call):
 try:r=call();report['checks'].append({'check':label,'httpStatus':200});return r
 except http.ApiError as e:report['checks'].append({'check':label,'httpStatus':e.status,'code':e.code});return None
missing=str(uuid.uuid4());anon=http.Client(report['origin'],trust_local_certificate=True)
record('anonymous_recovery_inspect_denied',lambda:anon.request('GET','/audit/outbox/failures/'+missing))
record('anonymous_recovery_replay_denied',lambda:anon.request('POST','/audit/outbox/failures/'+missing+'/replay',{'operationId':str(uuid.uuid4()),'failureCycleId':str(uuid.uuid4()),'reason':'bounded acceptance check'}))
for persona in ('admin','user'):
 os.environ['MILLWORKS_FIXTURE_EMAIL']=os.environ['MILLWORKS_ACCEPTANCE_'+persona.upper()+'_EMAIL'];os.environ['MILLWORKS_FIXTURE_PASSWORD']=os.environ['MILLWORKS_ACCEPTANCE_'+persona.upper()+'_PASSWORD']
 session=http.Client(report['origin'],trust_local_certificate=True)
 try:
  session.login();report['sessions'].append({'persona':persona,'login':'authenticated'})
  print(persona+' authenticated',flush=True)
  record(persona+'_recovery_missing_row',lambda:session.request('GET','/audit/outbox/failures/'+missing))
  if persona=='admin':
   listing=record('admin_company_inventory',lambda:session.request('GET','/crm/companies?page=1&pageSize=1'))
   rows=listing.get('items',[]) if isinstance(listing,dict) else []
   if rows:
    selected=rows[0].get('id');uuid.UUID(selected)
    report['crmCompanyId']=selected
    for step in (1,2):record('admin_crm_hub_observation_'+str(step),lambda:session.request('GET','/crm/companies/'+selected+'/hub'))
   else:report['crmPublication']='no_accessible_company_fixture'
 except http.ApiError as e:report['sessions'].append({'persona':persona,'login':'unavailable','httpStatus':e.status,'code':e.code})
 except Exception as e:report['sessions'].append({'persona':persona,'errorType':type(e).__name__})
 finally:
  try:report['sessions'].append({'persona':persona,'cleanup':session.logout()})
  except Exception as e:report['sessions'].append({'persona':persona,'cleanup':'unverified','errorType':type(e).__name__})
  out.write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(report,indent=2))
