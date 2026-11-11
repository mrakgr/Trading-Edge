#!/usr/bin/env python3
"""The ThetaData options downloader, shared between the PC and the VPS (user 2026-11-11).

ONE job list, TWO machines, NEVER at the same time (one Theta Terminal per key):
  --mode pc   runs only INSIDE the trading-hours block (trading days 07:00–16:30 ET, 13:30 on early closes; the PC is
              not left on overnight): starts at the block start, no new request after block end − MARGIN, killed at
              block end − 2 min, then EXITS. Keeps its files (the backtests run on the PC) and uploads COPIES.
  --mode vps  runs only OUTSIDE the block (nights, weekends, holidays) as te-options.service, sleeping through blocks.
              Uploads and DELETES (the VPS disk floor: oms_day.ps1 deletes captures under 40 GB free).
THE SHARED STATE on the storage box, in the run's folder (--dest):
  jobs.tsv         the job list (built once by whichever machine has none, from build_market_jobs.py, then uploaded)
  done_<mode>.tsv  each machine's resume record; before every window a machine downloads the OTHER's and passes it to
                   eod_pull.py --also-done, so nothing on the box is downloaded twice
  <ROOT>__<quarter>.parquet  the data
Exit / stop: eod_pull 0 (all final) -> DONE file; 1 (some not final) -> retried (3 passes with no progress -> ALERT);
3 (deadline) -> the next window.
"""
import argparse, datetime as dt, os, signal, socket, subprocess, sys, threading, time

from market_calendar import MarketCalendar, ET

MARGIN = dt.timedelta(minutes=20)
KILL_BEFORE = dt.timedelta(minutes=2)
START_AFTER = dt.timedelta(minutes=3)        # PC: start a little after the block opens (the VPS has stopped by then)
UPLOAD_EVERY = 300
RETRY_WAIT = 600
FINAL = ('ok', 'nodata', 'empty')


def log(msg):
    print(f"{dt.datetime.now(ET):%Y-%m-%d %H:%M:%S %Z}  {msg}", flush=True)


def port_open(port=25503):
    with socket.socket() as s:
        s.settimeout(1)
        return s.connect_ex(('127.0.0.1', port)) == 0


class Terminal:
    def __init__(self, tdir, logf):
        self.dir, self.logf, self.proc = tdir, logf, None

    def start(self):
        if port_open():
            raise RuntimeError('port 25503 is already in use: another Theta Terminal is running — stop it first')
        env = dict(os.environ)
        cred = os.path.expanduser('~/.config/thetadata/credentials')
        env['THETADATA_API_KEY'] = next(l.split('=', 1)[1].strip() for l in open(cred) if l.startswith('KEY='))
        self.proc = subprocess.Popen(['java', '-Xmx2g', '-jar', 'ThetaTerminalv3.jar'], cwd=self.dir, env=env,
                                     stdout=open(self.logf, 'a'), stderr=subprocess.STDOUT, start_new_session=True)
        for _ in range(90):
            if port_open():
                time.sleep(5); log(f'the Theta Terminal started (pid {self.proc.pid})'); return
            time.sleep(2)
        raise RuntimeError('the Theta Terminal did not open port 25503 within 3 min')

    def stop(self):
        if self.proc and self.proc.poll() is None:
            os.killpg(self.proc.pid, signal.SIGTERM)
            try:
                self.proc.wait(60)
            except subprocess.TimeoutExpired:
                os.killpg(self.proc.pid, signal.SIGKILL)
            log('the Theta Terminal stopped')
        self.proc = None


class Box:
    """The storage box over rsync/ssh: the VPS with its key, the PC with the sub-account password (an askpass script
    that seds PASSWORD= out of the credentials file — never printed)."""
    def __init__(self, dest, key=None, password_file=None):
        self.dest = dest.rstrip('/') + '/'
        self.env = dict(os.environ)
        if key:
            self.ssh = f"ssh -p 23 -i {key} -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new"
        else:
            askpass = os.path.expanduser('~/.config/hetzner-storage-box/askpass.sh')
            self.env.update(SSH_ASKPASS=askpass, SSH_ASKPASS_REQUIRE='force', DISPLAY=':0',
                            SBX_CRED=os.path.expanduser(password_file))
            self.ssh = ("ssh -p 23 -o PubkeyAuthentication=no -o PreferredAuthentications=password,keyboard-interactive "
                        "-o StrictHostKeyChecking=accept-new")

    def rsync(self, *args):
        r = subprocess.run(['rsync', '-a', '--mkpath', '-e', self.ssh, *args], capture_output=True, text=True,
                           env=self.env)
        if r.returncode not in (0, 23):            # 23 = some files missing (e.g. no done file yet on the box)
            log(f"rsync FAILED ({r.returncode}) {' '.join(args[-2:])}: {r.stderr.strip()[:300]}")
        return r.returncode

    def push_data(self, out, remove):
        extra = ['--remove-source-files'] if remove else []
        return self.rsync(*extra, '--include', '*.parquet', '--exclude', '*', f"{out}/", self.dest) == 0

    def push_file(self, local, name):
        return self.rsync(local, self.dest + name) == 0

    def pull_file(self, name, local):
        return self.rsync(self.dest + name, local) == 0 and os.path.exists(local)


def final_count(*paths):
    keys = set()
    for p in paths:
        if p and os.path.exists(p):
            last = {}
            for l in open(p):
                if l.strip():
                    k, st = l.split('\t')[:2]; last[k] = st
            keys |= {k for k, st in last.items() if st in FINAL}
    return len(keys)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--mode', choices=['pc', 'vps'], required=True)
    ap.add_argument('--name', required=True, help='the run name: <out-root>/<name>, the logs')
    ap.add_argument('--dest', required=True, help='the run folder on the box, e.g. <user>@<host>:te-vps/options/<name>/')
    ap.add_argument('--scripts', default=os.path.dirname(os.path.abspath(__file__)))
    ap.add_argument('--out-root', required=True)
    ap.add_argument('--logs', required=True)
    ap.add_argument('--thetadata-dir', required=True)
    ap.add_argument('--python', default=sys.executable)
    ap.add_argument('--timezone-fs', required=True)
    ap.add_argument('--reference', required=True); ap.add_argument('--calendar-file', required=True)
    ap.add_argument('--max-dte', type=int)
    ap.add_argument('--workers', type=int, default=8)
    ap.add_argument('--key', help='VPS: the storage-box ssh key')
    ap.add_argument('--password-file', default='~/.config/hetzner-storage-box/credentials', help='PC: the box login')
    ap.add_argument('--block-start', default='07:00')
    ap.add_argument('--order', choices=['file', 'newest'], default='newest', help='the chunk order (eod_pull.py --order)')
    ap.add_argument('--start-now', action='store_true',
                    help="PC: don't wait for the block to open (only when the VPS is not pulling, e.g. its --not-before)")
    ap.add_argument('--not-before', help='ET "YYYY-MM-DD HH:MM": idle until then')
    a = ap.parse_args()
    other = 'vps' if a.mode == 'pc' else 'pc'
    out = os.path.join(a.out_root, a.name)
    os.makedirs(out, exist_ok=True); os.makedirs(a.logs, exist_ok=True)
    jobs = os.path.join(out, 'jobs.tsv')
    done_mine, done_other = os.path.join(out, 'done.tsv'), os.path.join(out, f'done_{other}.tsv')
    cal = MarketCalendar(a.timezone_fs, block_start=a.block_start)
    term = Terminal(a.thetadata_dir, os.path.join(a.logs, 'thetaterminal.log'))
    box = Box(a.dest, key=a.key if a.mode == 'vps' else None, password_file=a.password_file)
    remove = a.mode == 'vps'
    stale = 0
    if os.path.exists(os.path.join(out, 'DONE')):
        log(f"{out}/DONE exists: nothing to do"); return 0
    log(f"runner up ({a.mode}): {a.name} -> {a.dest}; block = trading days {a.block_start}–16:30 ET (13:30 early closes)")
    if a.not_before:
        nb = dt.datetime.fromisoformat(a.not_before).replace(tzinfo=ET)
        while dt.datetime.now(ET) < nb:
            log(f"idle until {nb:%Y-%m-%d %H:%M %Z} (--not-before)")
            time.sleep(min(3600, max(30, (nb - dt.datetime.now(ET)).total_seconds() + 5)))
    while True:
        now = dt.datetime.now(ET)
        if a.mode == 'vps':
            until = cal.blocked_until(now)
            if until:
                log(f"blocked (trading hours) until {until:%Y-%m-%d %H:%M %Z}; sleeping")
                time.sleep(max(30, (until - now).total_seconds() + 30)); continue
            stop_at = cal.next_block_start(now)
        else:
            b = cal.block(now.date())
            if b is None or now >= b[1] - MARGIN - dt.timedelta(minutes=10):
                log('not a PC window (the PC pulls only during trading hours): exiting'); return 0
            if now < b[0] + START_AFTER and not a.start_now:
                log(f"waiting for the PC window at {b[0] + START_AFTER:%H:%M %Z}")
                time.sleep((b[0] + START_AFTER - now).total_seconds()); continue
            stop_at = b[1]
        deadline = stop_at - MARGIN
        if deadline - now < dt.timedelta(minutes=10):
            time.sleep(max(30, (stop_at - now).total_seconds() + 30)); continue

        # the shared state: the job list and the other machine's progress
        if not os.path.exists(jobs) and box.pull_file('jobs.tsv', jobs):
            log(f"jobs.tsv from the box: {sum(1 for _ in open(jobs)):,} roots")
        if os.path.exists(done_other):
            os.remove(done_other)
        box.pull_file(f'done_{other}.tsv', done_other)
        before = final_count(done_mine, done_other)
        term.start()
        try:
            if not os.path.exists(jobs):
                log('no jobs.tsv here or on the box: building it')
                with open(os.path.join(a.logs, f'{a.name}.build_jobs.log'), 'a') as bl:
                    r = subprocess.run([a.python, os.path.join(a.scripts, 'build_market_jobs.py'), '--out', jobs + '.tmp'],
                                       stdout=bl, stderr=subprocess.STDOUT)
                if r.returncode:
                    raise RuntimeError(f"building the jobs failed ({r.returncode})")
                os.replace(jobs + '.tmp', jobs)
                box.push_file(jobs, 'jobs.tsv')
                log(f"jobs built and uploaded: {sum(1 for _ in open(jobs)):,} roots")
            stop_up = threading.Event()

            def uploader():
                while not stop_up.wait(UPLOAD_EVERY):
                    if box.push_data(out, remove) and os.path.exists(done_mine):
                        box.push_file(done_mine, f'done_{a.mode}.tsv')
            th = threading.Thread(target=uploader, daemon=True); th.start()
            cmd = [a.python, os.path.join(a.scripts, 'eod_pull.py'), '--jobs-file', jobs, '--out', out,
                   '--workers', str(a.workers), '--reference', a.reference, '--calendar-file', a.calendar_file,
                   '--deadline', deadline.astimezone(dt.timezone.utc).isoformat(), '--also-done', done_other,
                   '--order', a.order]
            if a.max_dte:
                cmd += ['--max-dte', str(a.max_dte)]
            log(f"window open until {stop_at:%Y-%m-%d %H:%M %Z} (no new requests after {deadline:%H:%M}); pulling")
            with open(os.path.join(a.logs, f'{a.name}.{a.mode}.pull.log'), 'a') as pl:
                p = subprocess.Popen(cmd, stdout=pl, stderr=subprocess.STDOUT, start_new_session=True)
                while p.poll() is None:
                    if dt.datetime.now(ET) >= stop_at - KILL_BEFORE:          # the watchdog: never into the other's window
                        log('the window is about to close and the pull is still running: SIGTERM')
                        os.killpg(p.pid, signal.SIGTERM)
                        try:
                            p.wait(60)
                        except subprocess.TimeoutExpired:
                            os.killpg(p.pid, signal.SIGKILL); p.wait()
                        break
                    time.sleep(15)
            rc = p.returncode
            stop_up.set(); th.join()
        finally:
            term.stop()
        box.push_data(out, remove)
        if os.path.exists(done_mine):
            box.push_file(done_mine, f'done_{a.mode}.tsv')
        after = final_count(done_mine, done_other)
        log(f"pull exit {rc}; final chunks (both machines) {before:,} -> {after:,}")
        if rc == 0:
            open(os.path.join(out, 'DONE'), 'w').write(f"{dt.datetime.now(ET).isoformat()}\n")
            box.push_file(os.path.join(out, 'DONE'), 'DONE')
            log('ALL JOBS FINAL: done'); return 0
        if rc == 1:
            stale = stale + 1 if after == before else 0
            if stale >= 3:
                open(os.path.join(out, 'ALERT'), 'w').write('3 passes with no new final chunk: see the pull log\n')
                log('ALERT: 3 passes with no progress; stopping'); return 2
            time.sleep(RETRY_WAIT)
        elif rc is not None and rc >= 0 and rc != 3:
            log(f"unexpected exit {rc}: waiting {RETRY_WAIT} s"); time.sleep(RETRY_WAIT)
        if a.mode == 'pc' and rc == 3:
            log('the PC window is over: exiting'); return 0


if __name__ == '__main__':
    sys.exit(main())
