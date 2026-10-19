"""Validate reusable finding/correction/retest trails; never certify semantic competence."""
import argparse,copy,datetime,hashlib,json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
DEFAULT=ROOT/'config/pipeline/vector-engine-remediation-control.json'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def eventhash(e):return hashlib.sha256(json.dumps({k:v for k,v in e.items() if k!='sha256'},sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()).hexdigest()
def validate(c,root=ROOT):
 from remediation_audit_rules import audit_errors
 if c.get('schema')=='project_remediation_control.v2':
  extra=audit_errors(c,root)
  if any(x.startswith('Schema ') for x in extra):return extra
  c=copy.deepcopy(c); c['schema']='project_remediation_control.v1'
 else: extra=[]
 errors=list(extra)
 def check(ok,msg):
  if not ok:errors.append(msg)
 check(c.get('schema')=='project_remediation_control.v1','Unknown schema')
 check(bool(c.get('project_id')) and bool(c.get('coverage_statement')),'Project or coverage boundary missing')
 ev=c.get('evidence',[]);find=c.get('findings',[]);tasks=c.get('tasks',[])
 E={e.get('id'):e for e in ev};F={f.get('id'):f for f in find};T={t.get('id'):t for t in tasks}
 for a,m,name in [(ev,E,'evidence'),(find,F,'findings'),(tasks,T,'tasks')]:check(len(a)==len(m) and all(m),'Duplicate or missing '+name+' IDs')
 check(bool(E) and bool(F) and bool(T),'Baseline evidence, findings and tasks required; template must be populated')
 for e in ev:
  p=(root/e.get('path','')).resolve()
  check(p.is_relative_to(root.resolve()) and p.is_file(),'Invalid evidence path '+str(e.get('id')))
  if p.is_relative_to(root.resolve()) and p.is_file():check(sha(p)==e.get('sha256'),'Evidence drift '+str(e.get('id')))
 check(c.get('baseline',{}).get('evidence_id') in E,'Baseline evidence missing')
 check(c.get('baseline',{}).get('immutable') is True,'Baseline preservation not declared')
 for f in find:
  check(f.get('status') in ['open','in_progress','closed','reopened'],'Invalid finding state')
  check(bool(f.get('observed')) and bool(f.get('remediation')),'Finding lacks observation/correction')
  check(bool(f.get('evidence')) and all(x in E for x in f.get('evidence',[])),'Finding evidence missing '+str(f.get('id')))
  check(bool(f.get('task_ids')) and all(x in T for x in f.get('task_ids',[])),'Unmapped finding '+str(f.get('id')))
  if f.get('status')=='closed':check(all(T.get(x,{}).get('status')=='verified' for x in f.get('task_ids',[])),'Closed finding lacks verified tasks '+str(f.get('id')))
 for t in tasks:
  tid=t.get('id');check(t.get('status') in ['planned','in_progress','implemented','verified','blocked'],'Invalid task state')
  check(bool(t.get('acceptance')) and bool(t.get('accomplishes')),'Task acceptance missing '+str(tid))
  check(bool(t.get('finding_ids')) and all(x in F for x in t.get('finding_ids',[])),'Task lacks findings '+str(tid))
  check(all(x in T and x!=tid for x in t.get('depends_on',[])),'Invalid dependency '+str(tid))
  for fid in t.get('finding_ids',[]):check(tid in F.get(fid,{}).get('task_ids',[]),'Gap/task mapping not reciprocal '+str(tid))
  if t.get('status')=='verified':
   check(all(T.get(x,{}).get('status')=='verified' for x in t.get('depends_on',[])),'Dependency not verified '+str(tid))
   for field in ['change_evidence','verification_evidence']:check(bool(t.get(field)) and all(x in E for x in t.get(field,[])),'Verified task lacks '+field+' '+str(tid))
   close=t.get('closure') or {};check(all(close.get(k) for k in ['reviewer','reviewed_at','outcome','regression_evidence','lesson_evidence']),'Closure review incomplete '+str(tid))
   check(all(close.get(k) in E for k in ['regression_evidence','lesson_evidence']),'Closure linked results missing '+str(tid))
   check(bool(t.get('owner')) and 'pending' not in t['owner'].lower() and 'unassigned' not in t['owner'].lower(),'Accountable owner missing '+str(tid))
 visiting=set();done=set()
 def visit(tid):
  if tid in visiting:errors.append('Dependency cycle');return
  if tid in done:return
  visiting.add(tid)
  for d in T.get(tid,{}).get('depends_on',[]):
   if d in T:visit(d)
  visiting.remove(tid);done.add(tid)
 for tid in T:visit(tid)
 previous=None;last=None;ids=set()
 for e in c.get('events',[]):
  check(e.get('id') not in ids and bool(e.get('id')),'Duplicate event ID');ids.add(e.get('id'))
  check(e.get('previous_sha256')==previous and e.get('sha256')==eventhash(e),'Event chain drift')
  check(bool(e.get('details')) and bool(e.get('kind')),'Event details missing')
  check(bool(e.get('evidence_ids')) and all(x in E for x in e.get('evidence_ids',[])),'Event evidence missing')
  try:
   at=datetime.datetime.fromisoformat(e['at']);check(at.tzinfo is not None and (last is None or at>=last),'Event chronology invalid');last=at
  except (ValueError,KeyError,TypeError):errors.append('Event timestamp invalid')
  previous=e.get('sha256')
 check(bool(c.get('events')),'No breadcrumb events')
 q=c.get('qualification',{})
 if q.get('state')=='qualified' or q.get('expertise_claim_allowed'):
  check(all(t.get('status')=='verified' for t in tasks) and all(f.get('status')=='closed' for f in find),'Unresolved work cannot qualify')
  check(bool(q.get('independent_review_evidence')) and all(x in E for x in q.get('independent_review_evidence',[])),'Independent qualification evidence missing')
  target=q.get('target',{});check(isinstance(q.get('actual_passed'),int) and q.get('actual_passed',-1)>=target.get('minimum_passed',10**9),'Success threshold unmet')
  check(q.get('actual_critical')==0,'Critical qualification gate unmet')
  check(bool(q.get('whole_suite_gate_evidence')) and q.get('whole_suite_gate_evidence') in E,'Full rubric/family/both-phase gate evidence missing')
 return errors

def main():
 ap=argparse.ArgumentParser();ap.add_argument('--control',type=Path,default=DEFAULT);ap.add_argument('--self-test',action='store_true');ap.add_argument('--record-event',type=Path);a=ap.parse_args()
 c=json.loads(a.control.read_text(encoding='utf-8'));errors=validate(c);tests=[]
 if a.self_test:
  for name,mutate in [('orphan_finding',lambda d:d['findings'][0].update(task_ids=[])),('evidence_drift',lambda d:d['evidence'][0].update(sha256='0'*64)),('broken_event_chain',lambda d:d['events'][0].update(previous_sha256='0'*64)),('premature_task_closure',lambda d:d['tasks'][0].update(status='verified',closure=None)),('premature_expertise',lambda d:d['qualification'].update(state='qualified',expertise_claim_allowed=True)),('dependency_cycle',lambda d:d['tasks'][0].update(depends_on=[d['tasks'][-1]['id']])),('missing_work_packages',lambda d:d['tasks'][0].update(work_packages=[])),('missing_negative_plan',lambda d:d['tasks'][0]['verification_plan'].update(negative_checks=[])),('unsupported_measured_metric',lambda d:d['optimization']['metrics'][0].update(value=1,evidence_ids=[])),('missing_cause',lambda d:d['findings'][0]['analysis'].update(root_cause='')),('broken_reciprocal_mapping',lambda d:d['tasks'][0].update(finding_ids=['G02'])),('duplicate_sequence',lambda d:d['tasks'][1].update(sequence=1))]:
   if c.get('schema')!='project_remediation_control.v2' and name in {'missing_work_packages','missing_negative_plan','unsupported_measured_metric','missing_cause','broken_reciprocal_mapping','duplicate_sequence'}:continue
   x=copy.deepcopy(c);mutate(x);tests.append({'fixture':name,'rejected':bool(validate(x))})
  if not all(t['rejected'] for t in tests):errors.append('Negative control failed')
 if a.record_event and not errors:
  e=json.loads(a.record_event.read_text(encoding='utf-8'));e['previous_sha256']=c['events'][-1]['sha256'];e['sha256']=eventhash(e)
  proposed=copy.deepcopy(c);proposed['events'].append(e);proposed['updated_at']=datetime.datetime.now(datetime.timezone.utc).isoformat();errors=validate(proposed)
  if not errors:a.control.write_text(json.dumps(proposed,indent=2)+'\n',encoding='utf-8');c=proposed
 from remediation_audit_rules import audit_summary
 report={'audit_summary':audit_summary(c),'recorded_at':datetime.datetime.now(datetime.timezone.utc).isoformat(),'control':str(a.control),'control_valid':not errors,'errors':errors,'negative_controls':tests,'open_findings':sum(f['status']!='closed' for f in c['findings']),'verified_tasks':sum(t['status']=='verified' for t in c['tasks']),'qualification_state':c['qualification']['state'],'semantic_competence_validated':False}
 print(json.dumps(report,indent=2));return 0 if not errors else 1
if __name__=='__main__':raise SystemExit(main())
