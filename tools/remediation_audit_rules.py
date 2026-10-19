"""V2 audit rules. Structural evidence guard; independent semantic review remains separate."""
import datetime,hashlib,json,math,re
from pathlib import Path
import jsonschema

def fingerprint(r):
 return hashlib.sha256(json.dumps({k:r.get(k) for k in ['input_sha256','engine_sha256','context_sha256','test_sha256']},sort_keys=True,separators=(',',':')).encode()).hexdigest()
def audit_errors(c,root):
 errors=[]
 def check(ok,msg):
  if not ok:errors.append(msg)
 def refs(xs,label,required=False):
  check(isinstance(xs,list) and (not required or bool(xs)) and all(x in E for x in xs),label+' evidence missing')
 def date(x,label):
  try:
   d=datetime.datetime.fromisoformat(x);check(d.tzinfo is not None,label+' timezone missing');return d
  except (ValueError,TypeError):errors.append(label+' timestamp invalid');return None
 schema=json.loads((root/'config/pipeline/project-remediation-control.v2.schema.json').read_text(encoding='utf-8'))
 structural=list(jsonschema.Draft202012Validator(schema).iter_errors(c))
 if structural:return ['Schema '+'.'.join(map(str,e.path))+': '+e.message for e in structural]
 E={e['id']:e for e in c['evidence']};T={t['id']:t for t in c['tasks']};F={f['id']:f for f in c['findings']}
 check(len(c['audit_dimensions'])==len(set(c['audit_dimensions'])),'Duplicate audit dimensions')
 contract=c['control_contract'];check(len(contract.get('mandatory_checkpoints',[]))>=5 and bool(contract.get('enforcement_boundary')),'Audit checkpoints/enforcement boundary absent')
 for e in E.values():
  check(bool(e['producer']) and bool(e['claim_limit']) and e['kind'] in ['baseline_observation','design','control_validation','change','test_result','review','lesson','measurement'],'Evidence provenance/type invalid '+e['id']);date(e['captured_at'],'Evidence '+e['id'])
 sequences=[t['sequence'] for t in T.values()];check(sorted(sequences)==list(range(1,len(T)+1)),'Task sequence must be complete and unique')
 for f in F.values():
  check(f['priority'] in ['P0','P1','P2','P3'],'Finding priority invalid')
  check(all(f['id'] in T[x]['finding_ids'] for x in f['task_ids'] if x in T),'Finding/task links not reciprocal '+f['id'])
  a=f['analysis'];check(all(a.get(k) for k in ['symptom','reproduction','impact','root_cause','cause_confidence','counterexample_or_disconfirmation','recurrence_signature','lesson_ids']),'Granular finding analysis incomplete '+f['id'])
 packages=[]
 for t in T.values():
  check(all(T[d]['sequence']<t['sequence'] for d in t['depends_on'] if d in T),'Dependency sequence inverted '+t['id'])
  for w in t['work_packages']:
   packages.append(w['id']);check(w['status'] in ['planned','in_progress','implemented','verified','blocked'],'Work package state invalid')
   check(bool(w['action']) and bool(w['exit_check']),'Work package action/exit absent');refs(w['evidence_ids'],w['id'],w['status']=='verified')
  plan=t['verification_plan'];check(all(plan.get(k) for k in ['test_ids','test_scope','positive_checks','negative_checks','regression_checks','required_result_fields']),'Verification design incomplete '+t['id']);refs(plan.get('baseline_evidence_ids',[]),t['id'],True)
  check(all(t['risk_and_resume'].get(k) for k in ['risk','containment','rollback','resume_condition','next_action']),'Risk/resume map missing '+t['id'])
  if t['status']=='verified':
   check(all(w['status']=='verified' for w in t['work_packages']),'Task closed with unfinished work packages '+t['id'])
   check(any(t['id'] in i['task_ids'] and i['state']=='completed' and i['outcome'].get('verdict')=='passed' for i in c['iterations']),'Task lacks completed passed iteration '+t['id'])
 check(len(packages)==len(set(packages)),'Duplicate work package IDs')
 seen={};ids=set()
 for i in c['iterations']:
  check(i['id'] not in ids and bool(i['id']),'Duplicate iteration ID');ids.add(i['id'])
  check(i['state'] in ['planned','running','completed','failed','aborted'],'Invalid iteration state')
  check(bool(i['task_ids']) and all(x in T for x in i['task_ids']),'Iteration task link missing')
  check(bool(i['finding_ids']) and all(x in F for x in i['finding_ids']),'Iteration finding link missing')
  check(bool(i['hypothesis'].get('claim')) and bool(i['hypothesis'].get('disconfirming_result')),'Hypothesis/disconfirmation absent')
  check(bool(i['decision'].get('chosen')) and bool(i['decision'].get('why')) and bool(i['decision'].get('options')),'Iteration decision rationale absent')
  check(all(i['next_action'].get(x) for x in ['action','owner','resume_condition']),'Iteration resume map absent')
  if i['state'] not in ['planned']:
   start=date(i['started_at'],i['id']+' start');r=i['reproduction']
   check(bool(r.get('environment')),'Reproduction environment absent');refs(r.get('baseline_evidence_ids',[]),i['id'],True)
   keys=['input_sha256','engine_sha256','context_sha256','test_sha256'];check(all(isinstance(r.get(k),str) and re.fullmatch('[a-f0-9]{64}',r[k]) is not None for k in keys),'Reproduction identity missing')
   check(r.get('fingerprint')==fingerprint(r),'Reproduction fingerprint wrong')
   if r.get('fingerprint') in seen:check(bool(i['reuse'].get('rerun_justification')),'Unjustified duplicate iteration')
   seen[r.get('fingerprint')]=i['id']
  if i['state'] in ['completed','failed','aborted']:
   finish=date(i['finished_at'],i['id']+' finish');check(start is None or finish is None or finish>=start,'Iteration end before start')
   check(i['outcome'].get('verdict') in ['passed','failed','aborted','inconclusive'],'Terminal outcome missing')
   refs(i['test'].get('result_evidence_ids',[]),i['id'],True)
   check(bool(i['test'].get('commands')) and bool(i['test'].get('reviewer')),'Commands/reviewer absent')
   check(bool(i['outcome'].get('what_worked')) or bool(i['outcome'].get('what_failed')),'Learning outcome missing')
   check(bool(i['lessons'].get('record_ids')) and bool(i['lessons'].get('transferable_rule')) and bool(i['lessons'].get('applicability_limits')),'Transferable lesson missing')
  effort=i['effort']
  for k in ['elapsed_seconds','person_seconds','rework_seconds']:
   v=effort.get(k);check(v is None or (type(v) in [int,float] and math.isfinite(v) and v>=0),'Invalid measured effort '+k)
   if v is not None:refs(effort.get('measurement_evidence_ids',[]),'Measured effort',True)
  if effort.get('person_seconds') is not None and effort.get('rework_seconds') is not None:check(effort['rework_seconds']<=effort['person_seconds'],'Rework exceeds total effort')
  check(bool(effort.get('unknown_reason')) or all(effort.get(k) is not None for k in ['elapsed_seconds','person_seconds','rework_seconds']),'Unknown effort unlabeled')
 for m in c['optimization']['metrics']:
  check(all(m.get(k) for k in ['id','definition','unit']),'Metric identity/definition/unit absent')
  v=m.get('value')
  if v is None:check(bool(m.get('unknown_reason')),'Unknown metric unlabeled')
  else:
   check(type(v) in [int,float] and math.isfinite(v) and v>=0,'Metric invalid');refs(m.get('evidence_ids',[]),'Metric '+m['id'],True)
   if m['unit']=='fraction':check(v<=1,'Fraction outside range')
 for proposal in c['optimization']['improvement_proposals']:
  check(all(proposal.get(k) for k in ['id','hypothesis','expected_benefit','potential_harm','controlled_comparison','decision_owner','acceptance','state']),'Optimization proposal incomplete')
  refs(proposal.get('trigger_evidence_ids',[]),'Optimization proposal',True)
  if proposal.get('state')=='validated':refs(proposal.get('outcome_evidence_ids',[]),'Optimization outcome',True)
 for a in c['audit_reviews']:
  check(all(a.get(k) for k in ['id','reviewer','reviewed_at','dimensions_checked','conclusion','next_actions']),'Audit review incomplete');date(a.get('reviewed_at'),'Audit review');refs(a.get('evidence_ids',[]),'Audit review',True)
  check(set(a.get('dimensions_checked',[]))<=set(c['audit_dimensions']),'Unknown audit dimension')
 if c['qualification'].get('state')=='qualified' or c['qualification'].get('expertise_claim_allowed'):
  q=c['qualification'];check(q.get('independent_reviewer') not in [None,''] and q.get('independent_reviewer')!=q.get('implementation_owner'),'Reviewer independence absent')
  gate=E.get(q.get('whole_suite_gate_evidence'))
  if gate:
   try:
    g=json.loads((root/gate['path']).read_text(encoding='utf-8'));check(g.get('passed') is True and g.get('independently_reviewed') is True,'Whole-suite gate report not independently passed')
    check(g.get('whole_cases')==q['target'].get('whole_cases') and g.get('passed_whole_cases')==q.get('actual_passed') and g.get('critical_violations')==0,'Qualification counters/report disagree')
    check(g.get('both_phases_passed') is True and g.get('all_family_gates_passed') is True and g.get('mandatory_obligations_passed') is True,'Partial gate cannot qualify')
   except (OSError,ValueError,TypeError):errors.append('Whole-suite gate evidence unreadable')
 return errors

def audit_summary(c):
 iterations=c.get('iterations',[]);fingerprints=[i.get('reproduction',{}).get('fingerprint') for i in iterations if i.get('reproduction',{}).get('fingerprint')]
 return {'audit_dimensions':len(c.get('audit_dimensions',[])),'work_packages':sum(len(t.get('work_packages',[])) for t in c['tasks']),'iterations':len(iterations),'terminal_iterations':sum(i['state'] in ['completed','failed','aborted'] for i in iterations),'repeat_fingerprints':len(fingerprints)-len(set(fingerprints)),'metrics_measured':sum(m.get('value') is not None for m in c.get('optimization',{}).get('metrics',[])),'efficiency_improvement_established':False,'next_planned_task':next((t['id'] for t in c['tasks'] if t['status']=='planned'),None)}
