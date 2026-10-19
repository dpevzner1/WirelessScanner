"""Tool-agnostic attestation ledger for reviews, audits and verifications.

Every material step of a review is appended to an append-only, hash-chained ledger
(`ATTESTATION-LEDGER.jsonl` in the review directory) that records WHEN it happened (UTC and local
time), WHO performed it (tool, tool version, model, operator, role) and WHAT it touched (input and
output files with SHA-256, executed command with exit code and captured output hashes).

Any tool can use it: Claude Code, Codex, another assistant, a CI job or a person. Identity is
self-declared unless a record carries a verified cryptographic signature (`ssh-keygen -Y sign`
with a key the operator supplies). Without a signature the ledger detects accidental drift and
broken chains; it is not tamper-proof, because anyone with write access can rewrite every hash.

Commands
  init    create a review directory ledger with scope, write roots and optional protected manifest
  record  append one attested step (read, hash check, judgment batch, decision, write, note ...)
  run     execute a command, capture stdout/stderr inside the review directory and attest it
  verify  check chain, record hashes, timestamps, identities, write roots, file coverage, signatures
  seal    final record: artifact inventory, protected-manifest drift; no record may follow it

Actor identity comes from --actor <profile.json>, from REVIEW_ACTOR_* environment variables or from
individual --actor-* flags (flags win). See docs/REVIEW_ATTESTATION_STANDARD.md.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import fnmatch
import hashlib
import json
import os
import platform
import shlex
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

SCHEMA = 'review_attestation_record.v1'
LEDGER = 'ATTESTATION-LEDGER.jsonl'
LOCK = '.attestation.lock'
ROLES = ('reviewer', 'implementer', 'custodian', 'auditor', 'human_reviewer', 'automation', 'other')
KINDS = ('init', 'read', 'hash_check', 'execute', 'write', 'judgment', 'decision', 'freeze', 'note', 'seal')
SIGN_NAMESPACE = 'review-attestation'
DEFAULT_EXCLUDES = (LEDGER, LOCK, '*.pyc', '__pycache__/*', '*/__pycache__/*')
ACTOR_FIELDS = ('tool', 'tool_version', 'model', 'operator', 'role')
ROOT = Path(__file__).resolve().parents[1]


class AttestationError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def canonical(record: dict) -> bytes:
    body = {k: v for k, v in record.items() if k not in ('record_sha256', 'signature')}
    return json.dumps(body, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


def now_pair() -> tuple[str, str]:
    utc = _dt.datetime.now(_dt.timezone.utc)
    return utc.isoformat(), utc.astimezone().isoformat()


def rel(path: Path, base: Path = ROOT) -> str:
    p = Path(path).resolve()
    try:
        return p.relative_to(base.resolve()).as_posix()
    except ValueError:
        return p.as_posix()


def read_ledger(review_dir: Path) -> list[dict]:
    path = review_dir / LEDGER
    if not path.exists():
        return []
    records = []
    for n, line in enumerate(path.read_text(encoding='utf-8').splitlines(), 1):
        if line.strip():
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError as exc:
                raise AttestationError(f'Ledger line {n} is not valid JSON: {exc}') from None
    return records


def actor_from(args) -> dict:
    actor = {k: None for k in ACTOR_FIELDS}
    if getattr(args, 'actor', None):
        profile = json.loads(Path(args.actor).read_text(encoding='utf-8'))
        actor.update({k: profile.get(k) for k in ACTOR_FIELDS if k in profile})
    for k in ACTOR_FIELDS:
        env = os.environ.get('REVIEW_ACTOR_' + k.upper())
        if env:
            actor[k] = env
        flag = getattr(args, 'actor_' + k, None)
        if flag:
            actor[k] = flag
    for k in ('tool_version', 'model', 'operator'):
        actor[k] = actor[k] or 'unknown'
    if not actor['tool']:
        raise AttestationError('Actor tool is required (--actor-tool, REVIEW_ACTOR_TOOL or --actor profile)')
    if actor['role'] not in ROLES:
        raise AttestationError(f'Actor role must be one of {ROLES}')
    actor['identity_basis'] = 'self_declared'
    return actor


def file_entries(paths, review_dir: Path) -> list[dict]:
    out = []
    for p in paths or []:
        path = Path(p)
        if not path.is_absolute():
            path = (Path.cwd() / path)
        if not path.exists():
            raise AttestationError(f'Attested file does not exist: {p}')
        if path.is_dir():
            for f in sorted(x for x in path.rglob('*') if x.is_file()):
                out.append({'path': rel(f), 'sha256': sha256_file(f)})
        else:
            out.append({'path': rel(path), 'sha256': sha256_file(path)})
    return out


def manifest_drift(manifest_path: str | None) -> dict | None:
    if not manifest_path:
        return None
    mpath = Path(manifest_path)
    data = json.loads(mpath.read_text(encoding='utf-8'))
    entries = data.get('files_sha256') or data.get('files') or data
    entries = {k: (v if isinstance(v, str) else v.get('sha256')) for k, v in entries.items()
               if isinstance(v, str) and len(v) == 64 or isinstance(v, dict) and 'sha256' in v}
    drift, missing = [], []
    for path, expected in entries.items():
        f = ROOT / path
        if not f.is_file():
            missing.append(path)
        elif sha256_file(f) != expected:
            drift.append(path)
    return {'manifest': rel(mpath), 'manifest_sha256': sha256_file(mpath), 'entries': len(entries), 'drift': drift, 'missing': missing}


def ssh_sign(data: bytes, key: str) -> str:
    with tempfile.TemporaryDirectory() as tmp:
        msg = Path(tmp) / 'record.bin'
        msg.write_bytes(data)
        res = subprocess.run(['ssh-keygen', '-Y', 'sign', '-f', key, '-n', SIGN_NAMESPACE, str(msg)],
                             capture_output=True, text=True)
        if res.returncode != 0:
            raise AttestationError('ssh-keygen signing failed: ' + res.stderr.strip()[:300])
        return (Path(str(msg) + '.sig')).read_text(encoding='utf-8')


def ssh_verify(data: bytes, signature: str, signer: str, allowed_signers: str) -> bool:
    with tempfile.TemporaryDirectory() as tmp:
        sig = Path(tmp) / 'record.sig'
        sig.write_text(signature, encoding='utf-8')
        res = subprocess.run(['ssh-keygen', '-Y', 'verify', '-f', allowed_signers, '-I', signer, '-n', SIGN_NAMESPACE, '-s', str(sig)],
                             input=data, capture_output=True)
        return res.returncode == 0


def append(review_dir: Path, record: dict, sign_key: str | None = None, signer: str | None = None) -> dict:
    lock = review_dir / LOCK
    try:
        fd = os.open(lock, os.O_WRONLY | os.O_CREAT | os.O_EXCL)
    except FileExistsError:
        raise AttestationError(f'Ledger lock exists ({lock}); another writer may be active. Verify before removing it.') from None
    try:
        os.write(fd, str(os.getpid()).encode())
        records = read_ledger(review_dir)
        if records and records[-1]['step']['kind'] == 'seal':
            raise AttestationError('Ledger is sealed; start a new review directory for further work')
        if not records and record['step']['kind'] != 'init':
            raise AttestationError('First record must be kind=init')
        record['schema'] = SCHEMA
        record['sequence'] = len(records) + 1
        record['record_id'] = f"ATT-{record['sequence']:05d}"
        record['previous_record_sha256'] = records[-1]['record_sha256'] if records else None
        utc, local = now_pair()
        if records and utc < records[-1]['recorded_at_utc']:
            raise AttestationError('System clock is earlier than the previous record; refusing to append')
        record['recorded_at_utc'], record['recorded_at_local'] = utc, local
        body = canonical(record)
        record['record_sha256'] = hashlib.sha256(body).hexdigest()
        if sign_key:
            record['signature'] = {'method': 'ssh', 'namespace': SIGN_NAMESPACE, 'signer': signer or 'unknown', 'value': ssh_sign(body, sign_key),
                                   'covers': 'canonical record without record_sha256 and signature'}
        else:
            record['signature'] = {'method': 'none', 'limit': 'Hash-chained self-declared attestation; not a cryptographic signature'}
        with open(review_dir / LEDGER, 'a', encoding='utf-8', newline='\n') as f:
            f.write(json.dumps(record, ensure_ascii=False, sort_keys=True) + '\n')
        return record
    finally:
        os.close(fd)
        lock.unlink(missing_ok=True)


def base_record(args, kind: str, phase: str, step_id: str, description: str) -> dict:
    if kind not in KINDS:
        raise AttestationError(f'kind must be one of {KINDS}')
    return {'review_id': None, 'actor': actor_from(args),
            'step': {'phase': phase, 'id': step_id, 'kind': kind, 'description': description},
            'command': None, 'inputs': [], 'outputs': [], 'limits': list(getattr(args, 'limit', None) or [])}


def cmd_init(args) -> dict:
    d = Path(args.dir)
    d.mkdir(parents=True, exist_ok=True)
    if read_ledger(d):
        raise AttestationError('Ledger already initialised in this directory')
    rec = base_record(args, 'init', 'setup', 'init', args.purpose)
    write_roots = [rel(Path(r)) for r in (args.write_root or [d])]
    rec['review_id'] = args.review_id
    rec['session'] = {'purpose': args.purpose, 'authorized_scope': args.authorized_scope or [], 'prohibited': args.prohibited or [],
                      'exceptions': args.exception or [], 'write_roots': write_roots,
                      'coverage_excludes': list(DEFAULT_EXCLUDES) + (args.exclude or []),
                      'platform': platform.platform(), 'python': platform.python_version(), 'working_directory': rel(Path.cwd()),
                      'protected_manifest_at_init': manifest_drift(args.protected_manifest)}
    return append(d, rec, args.sign_key, args.signer)


def review_meta(d: Path) -> dict:
    records = read_ledger(d)
    if not records:
        raise AttestationError('Ledger not initialised')
    return records[0]


def cmd_record(args) -> dict:
    d = Path(args.dir)
    meta = review_meta(d)
    rec = base_record(args, args.kind, args.phase, args.step_id, args.description)
    rec['review_id'] = meta['review_id']
    rec['inputs'] = file_entries(args.input, d)
    rec['outputs'] = file_entries(args.output, d)
    if args.command:
        rec['command'] = {'argv': args.command, 'exit_code': args.exit_code,
                          'stdout': file_entries([args.stdout], d)[0] if args.stdout else None,
                          'stderr': file_entries([args.stderr], d)[0] if args.stderr else None, 'executed_by_this_tool': False}
    return append(d, rec, args.sign_key, args.signer)


def cmd_run(args) -> dict:
    # Refuse missing/invalid execution identity before any subprocess or capture
    # side effects. Post-execution record construction validates it again.
    actor_from(args)
    d = Path(args.dir)
    meta = review_meta(d)
    argv = args.argv[1:] if args.argv and args.argv[0] == '--' else args.argv
    if not argv:
        raise AttestationError('No command given after --')
    outdir = d / 'executions'
    outdir.mkdir(exist_ok=True)
    stamp = _dt.datetime.now(_dt.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    out, err = outdir / f'{args.step_id}.{stamp}.stdout.txt', outdir / f'{args.step_id}.{stamp}.stderr.txt'
    started = now_pair()[0]
    exe = argv[0]
    if Path(exe).exists():
        exe = str(Path(exe).resolve())
    else:
        exe = shutil.which(exe) or exe
    launch_error = None
    try:
        res = subprocess.run([exe] + list(argv[1:]), capture_output=True)
        out.write_bytes(res.stdout)
        err.write_bytes(res.stderr)
        code = res.returncode
    except OSError as exc:
        # A command that could not start is still an attempted execution and is attested as a failure.
        out.write_bytes(b'')
        err.write_bytes(f'{type(exc).__name__}: {exc}'.encode('utf-8'))
        code, launch_error = None, f'{type(exc).__name__}: {exc}'
    rec = base_record(args, 'execute', args.phase, args.step_id, args.description)
    rec['review_id'] = meta['review_id']
    rec['inputs'] = file_entries(args.input, d)
    present = [p for p in (args.output or []) if Path(p).exists()]
    rec['outputs'] = file_entries(present + [out, err], d)
    rec['command'] = {'argv': ' '.join(shlex.quote(a) for a in argv), 'resolved_executable': exe, 'exit_code': code, 'launch_error': launch_error,
                      'started_at_utc': started, 'stdout': {'path': rel(out), 'sha256': sha256_file(out)},
                      'stderr': {'path': rel(err), 'sha256': sha256_file(err)}, 'executed_by_this_tool': True,
                      'declared_outputs_missing': [p for p in (args.output or []) if not Path(p).exists()]}
    rec = append(d, rec, args.sign_key, args.signer)
    echo = out.read_bytes().decode('utf-8', 'replace')[-4000:]
    encoding = getattr(sys.stdout, 'encoding', None) or 'utf-8'
    sys.stdout.write(echo.encode(encoding, 'backslashreplace').decode(encoding))
    return rec


def excluded(path: str, patterns) -> bool:
    return any(fnmatch.fnmatch(path, p) for p in patterns)


def verify(d: Path, allowed_signers: str | None = None, require_sealed: bool = False) -> dict:
    records = read_ledger(d)
    errors, warnings = [], []
    if not records:
        return {'valid': False, 'errors': ['Ledger missing or empty'], 'warnings': [], 'records': 0}
    if records[0]['step']['kind'] != 'init':
        errors.append('First record is not kind=init')
    session = records[0].get('session', {})
    roots = session.get('write_roots') or [rel(d)]
    excludes = session.get('coverage_excludes') or list(DEFAULT_EXCLUDES)
    prev, last_utc, sealed_at = None, '', None
    latest = {}
    actors = set()
    for i, r in enumerate(records, 1):
        tag = r.get('record_id', f'line {i}')
        if r.get('schema') != SCHEMA:
            errors.append(f'{tag}: unknown schema')
        if r.get('sequence') != i:
            errors.append(f'{tag}: sequence {r.get("sequence")} expected {i}')
        if r.get('previous_record_sha256') != prev:
            errors.append(f'{tag}: chain broken (previous hash mismatch)')
        if hashlib.sha256(canonical(r)).hexdigest() != r.get('record_sha256'):
            errors.append(f'{tag}: record hash mismatch (record altered after writing)')
        if r.get('recorded_at_utc', '') < last_utc:
            errors.append(f'{tag}: timestamp earlier than previous record')
        last_utc = r.get('recorded_at_utc', '')
        a = r.get('actor') or {}
        if not a.get('tool') or a.get('role') not in ROLES:
            errors.append(f'{tag}: actor tool/role missing or invalid')
        actors.add((a.get('tool'), a.get('model'), a.get('role')))
        if r.get('step', {}).get('kind') not in KINDS:
            errors.append(f'{tag}: invalid step kind')
        if sealed_at:
            errors.append(f'{tag}: record appended after seal {sealed_at}')
        if r['step']['kind'] == 'seal':
            sealed_at = tag
        sig = r.get('signature') or {}
        if sig.get('method') == 'ssh':
            if not allowed_signers:
                warnings.append(f'{tag}: ssh signature present but no allowed-signers file given; not verified')
            elif not ssh_verify(canonical(r), sig.get('value', ''), sig.get('signer', ''), allowed_signers):
                errors.append(f'{tag}: ssh signature verification failed')
        elif sig.get('method') != 'none':
            errors.append(f'{tag}: unknown signature method')
        for e in (r.get('outputs') or []) + [x for x in [(r.get('command') or {}).get('stdout'), (r.get('command') or {}).get('stderr')] if x]:
            if not any(Path(e['path']).resolve().is_relative_to(Path(root).resolve()) for root in roots):
                errors.append(f'{tag}: output outside declared write roots: {e["path"]}')
            latest[e['path']] = (e['sha256'], tag)
        prev = r.get('record_sha256')
    # current files versus their latest attestation, and coverage of everything in the review directory
    for path, (h, tag) in latest.items():
        f = ROOT / path
        if not f.is_file():
            errors.append(f'{path}: attested by {tag} but now missing')
        elif sha256_file(f) != h:
            errors.append(f'{path}: changed after its latest attestation ({tag})')
    base = rel(d)
    for f in sorted(x for x in d.rglob('*') if x.is_file()):
        rp = rel(f)
        inner = rp[len(base) + 1:] if rp.startswith(base + '/') else rp
        if excluded(inner, excludes) or excluded(rp, excludes):
            continue
        if rp not in latest:
            errors.append(f'{rp}: present in the review directory but never attested')
    if require_sealed and not sealed_at:
        errors.append('Ledger is not sealed')
    return {'valid': not errors, 'errors': errors, 'warnings': warnings, 'records': len(records), 'sealed': bool(sealed_at),
            'actors': sorted([list(x) for x in actors]), 'review_id': records[0].get('review_id'),
            'signature_methods': sorted({(r.get('signature') or {}).get('method', '?') for r in records}),
            'limit': 'Self-declared identities and hash chain detect accidental drift and broken sequences; only verified signatures attest who wrote a record.'}


def cmd_seal(args) -> dict:
    d = Path(args.dir)
    meta = review_meta(d)
    pre = verify(d)
    if not pre['valid']:
        raise AttestationError('Cannot seal: ' + '; '.join(pre['errors'][:8]))
    rec = base_record(args, 'seal', 'closure', 'seal', args.description or 'Final inventory and seal')
    rec['review_id'] = meta['review_id']
    excludes = meta['session'].get('coverage_excludes') or list(DEFAULT_EXCLUDES)
    base = rel(d)
    inventory = []
    for f in sorted(x for x in d.rglob('*') if x.is_file()):
        rp = rel(f)
        inner = rp[len(base) + 1:] if rp.startswith(base + '/') else rp
        if not (excluded(inner, excludes) or excluded(rp, excludes)):
            inventory.append({'path': rp, 'sha256': sha256_file(f)})
    rec['seal'] = {'inventory_count': len(inventory), 'inventory_sha256': hashlib.sha256(json.dumps(inventory, sort_keys=True).encode()).hexdigest(),
                   'protected_manifest_at_seal': manifest_drift(args.protected_manifest), 'verified_before_seal': pre}
    rec['outputs'] = inventory
    return append(d, rec, args.sign_key, args.signer)


def parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)

    def actor_opts(p):
        p.add_argument('--actor', help='JSON profile with tool, tool_version, model, operator, role')
        for k in ACTOR_FIELDS:
            p.add_argument('--actor-' + k.replace('_', '-'), dest='actor_' + k)
        p.add_argument('--sign-key', help='Private key for ssh-keygen -Y sign (optional; operator supplied)')
        p.add_argument('--signer', help='Signer identity for the allowed-signers file')
        p.add_argument('--limit', action='append', help='Scope or interpretation limit of this step (repeatable)')

    p = sub.add_parser('init'); p.add_argument('--dir', required=True); p.add_argument('--review-id', required=True); p.add_argument('--purpose', required=True)
    p.add_argument('--authorized-scope', action='append'); p.add_argument('--prohibited', action='append'); p.add_argument('--exception', action='append')
    p.add_argument('--write-root', action='append'); p.add_argument('--exclude', action='append'); p.add_argument('--protected-manifest'); actor_opts(p)
    p = sub.add_parser('record'); p.add_argument('--dir', required=True); p.add_argument('--phase', required=True); p.add_argument('--step-id', required=True)
    p.add_argument('--kind', required=True, choices=KINDS); p.add_argument('--description', required=True)
    p.add_argument('--input', action='append'); p.add_argument('--output', action='append')
    p.add_argument('--command'); p.add_argument('--exit-code', type=int); p.add_argument('--stdout'); p.add_argument('--stderr'); actor_opts(p)
    p = sub.add_parser('run'); p.add_argument('--dir', required=True); p.add_argument('--phase', required=True); p.add_argument('--step-id', required=True)
    p.add_argument('--description', required=True); p.add_argument('--input', action='append'); p.add_argument('--output', action='append'); actor_opts(p)
    p.add_argument('argv', nargs=argparse.REMAINDER)
    p = sub.add_parser('verify'); p.add_argument('--dir', required=True); p.add_argument('--allowed-signers'); p.add_argument('--require-sealed', action='store_true')
    p = sub.add_parser('seal'); p.add_argument('--dir', required=True); p.add_argument('--description'); p.add_argument('--protected-manifest'); actor_opts(p)
    return ap


def main(argv=None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.cmd == 'verify':
            result = verify(Path(args.dir), args.allowed_signers, args.require_sealed)
            print(json.dumps(result, indent=2))
            return 0 if result['valid'] else 1
        rec = {'init': cmd_init, 'record': cmd_record, 'run': cmd_run, 'seal': cmd_seal}[args.cmd](args)
        summary = {'record_id': rec['record_id'], 'record_sha256': rec['record_sha256'], 'recorded_at_utc': rec['recorded_at_utc'],
                   'actor': rec['actor']['tool'], 'kind': rec['step']['kind']}
        if args.cmd == 'run':
            summary['command_exit_code'] = rec['command']['exit_code']
            print('\n' + json.dumps(summary), file=sys.stderr)
        else:
            print(json.dumps(summary))
        return 0
    except AttestationError as exc:
        print('attestation error: ' + str(exc), file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
