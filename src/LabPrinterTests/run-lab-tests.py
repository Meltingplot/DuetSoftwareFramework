#!/usr/bin/env python3
"""Run the lab printer tests through a plink tunnel, show each result as it comes and evaluate the run at the end, see README.md

Usage: run-lab-tests.py [--side-load [--fault-injection [--plain-link]]] [dotnet test arguments...]
  --side-load        publish DCS from this tree and bind-mount its DLLs over the image's copies for the run
  --fault-injection  publish the test-only build that corrupts single SPI exchanges on request (SpiFaultInjection.cs)
  --plain-link       make that build keep the plain link: it does not ask RRF for the link capabilities (SPI.PlainLinkFile)
  Example:           run-lab-tests.py --side-load --filter "TestCategory!=Restart"

The evaluation follows sections 9 and 10 of design/lab-test-plan.md. The raw test output, the TRX file and the journal of the run
are kept in src/LabPrinterTests/TestResults/<time> (or LAB_RESULTS_DIR). Colors are off when NO_COLOR is set or the output is no terminal
"""

import base64
import datetime
import json
import os
import re
import shlex
import signal
import statistics
import subprocess
import sys
import tempfile
import time
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

PLINK = os.environ.get('PLINK', '/mnt/c/Program Files/PuTTY/plink.exe')
JUMP_HOST = os.environ.get('JUMP_HOST', 'meltingplot@192.168.172.143')
SBC = os.environ.get('SBC', '10.42.0.2')
SBC_USER = os.environ.get('SBC_USER', 'mpadmin')
TUNNEL_PORT = int(os.environ.get('TUNNEL_PORT', '18080'))
SIDE_LOAD_DIR = '/var/tmp/lab-tests'
SIDE_LOAD_DLLS = ['DuetAPI.dll', 'DuetControlServer.dll', 'DuetSharedLibrary.dll']
# Read by the fault injection build when DCS starts, see SPI.PlainLinkFile
PLAIN_LINK_FILE = f'{SIDE_LOAD_DIR}/spi-plain-link'

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent

# Colors
USE_COLOR = sys.stdout.isatty() and 'NO_COLOR' not in os.environ


def color(code: str, text: str) -> str:
    return f'\x1b[{code}m{text}\x1b[0m' if USE_COLOR else text


def green(text): return color('32', text)
def red(text): return color('31', text)
def yellow(text): return color('33', text)
def dim(text): return color('2', text)
def bold(text): return color('1', text)


ANSI = re.compile(r'\x1b\[[0-9;]*m')


def say(text: str = ''):
    print(text, flush=True)


# --- SBC access -----------------------------------------------------------------------------------------------------------------

SBC_COMMAND = [PLINK, '-ssh', '-agent', '-A', '-batch', JUMP_HOST, f'ssh -A -o BatchMode=yes {SBC_USER}@{SBC} bash -s']


def sbc(script: str, timeout: float = 600) -> subprocess.CompletedProcess:
    """Run a script read from stdin on the SBC"""
    return subprocess.run(SBC_COMMAND, input=script, capture_output=True, text=True, timeout=timeout)


def sbc_ok(script: str, timeout: float = 600) -> bool:
    result = sbc(script, timeout)
    if result.stdout.strip():
        say(dim(result.stdout.rstrip()))
    if result.returncode != 0 and result.stderr.strip():
        say(red(result.stderr.rstrip()))
    return result.returncode == 0


# After a DCS restart, send M999 if RRF is halted and wait until the printer is idle
RESET_FIRMWARE = r'''
status() { curl -s -m 2 localhost/machine/status | python3 -c 'import json, sys; print(json.load(sys.stdin)["state"]["status"])' 2>/dev/null || echo down; }
for i in $(seq 120); do
  s=$(status)
  [ "$s" = halted ] || [ "$s" = idle ] && break
  sleep 1
done
if [ "$s" = halted ]; then
  pid=$(systemctl show -p MainPID --value duetcontrolserver)
  key=$(curl -s -m 5 'localhost/machine/connect?password=reprap' | python3 -c 'import json, sys; print(json.load(sys.stdin)["sessionKey"])')
  curl -s -m 10 -X POST -H "X-Session-Key: $key" --data-binary M999 localhost/machine/code > /dev/null || true
  # DCS exits after sending the reset, wait for the new instance
  for i in $(seq 120); do
    p=$(systemctl show -p MainPID --value duetcontrolserver)
    [ "$p" != 0 ] && [ "$p" != "$pid" ] && break
    sleep 1
  done
fi
for i in $(seq 180); do
  [ "$(status)" = idle ] && exit 0
  sleep 1
done
echo "The printer did not become idle" >&2
exit 1
'''

# State of the printer as JSON: journal cursor, versions, status, side-load, test leftovers and M122
SNAPSHOT = r'''
python3 - <<'PY'
import json, os, subprocess, urllib.request
def run(*command):
    return subprocess.run(list(command), capture_output=True, text=True).stdout
result = {}
cursor = run("sudo", "-n", "journalctl", "-u", "duetcontrolserver", "-n", "1", "--show-cursor", "-o", "cat", "--no-pager")
result["cursor"] = next((line[len("-- cursor: "):] for line in cursor.splitlines() if line.startswith("-- cursor: ")), None)
result["time"] = run("date", "-u", "+%FT%TZ").strip()
try:
    status = json.load(urllib.request.urlopen("http://localhost/machine/status", timeout=5))
    boards = status.get("boards") or []
    result["status"] = status["state"]["status"]
    result["dsf"] = status["sbc"]["dsf"]["version"]
    result["rrf"] = boards[0]["firmwareVersion"] if boards else None
    result["boards"] = [board.get("state") for board in boards[1:]]
    result["job"] = ((status.get("job") or {}).get("file") or {}).get("fileName")
except Exception as e:
    result["error"] = str(e)
result["sideLoad"] = run("findmnt", "-n", "-o", "SOURCE", "/opt/dsf/bin/DuetControlServer.dll").strip() or None
result["sideLoadDir"] = os.path.exists("/var/tmp/lab-tests")
result["ota"] = run("systemctl", "is-active", "mp-ota-gate.timer").strip()
result["trigger30"] = subprocess.run(["sudo", "-n", "test", "-e", "/opt/dsf/sd/sys/trigger30.g"]).returncode == 0
result["testMacros"] = subprocess.run(["sudo", "-n", "test", "-e", "/opt/dsf/sd/macros/lab-tests"]).returncode == 0
try:
    key = json.load(urllib.request.urlopen("http://localhost/machine/connect?password=reprap", timeout=5))["sessionKey"]
    request = urllib.request.Request("http://localhost/machine/code", data=b"M122", headers={"X-Session-Key": key}, method="POST")
    result["m122"] = urllib.request.urlopen(request, timeout=30).read().decode("utf-8", "replace")
except Exception as e:
    result["m122"] = f"M122 failed: {e}"
print(json.dumps(result))
PY
'''


def snapshot() -> dict:
    result = sbc(SNAPSHOT, 120)
    try:
        return json.loads(result.stdout.strip().splitlines()[-1])
    except (IndexError, json.JSONDecodeError):
        return {'error': (result.stderr or result.stdout).strip()}


def journal_since(cursor: str) -> str:
    result = sbc(f"sudo -n journalctl -u duetcontrolserver --after-cursor={shlex.quote(cursor)} -o short-iso-precise --no-pager", 600)
    return ANSI.sub('', result.stdout)


# --- Running the tests ----------------------------------------------------------------------------------------------------------

RESULT_LINE = re.compile(r'^\s{2}(Passed|Failed|Skipped) (.+?) \[(.+)\]$')


class LiveOutput:
    """Shows the dotnet test output with one colored line per test, the messages of failed tests and the progress of the tests"""

    def __init__(self, log):
        self.log = log
        self.testing = False
        self.block = None   # None, 'failed' (after a failed test), 'message' or 'stack'

    def line(self, raw: str):
        self.log.write(raw)
        text = raw.rstrip('\n')
        if not self.testing:
            if text.startswith('Test run for') or 'NUnit3TestExecutor discovered' in text or text.startswith('Starting test execution'):
                self.testing = True
            if ' error ' in text or text.startswith('error'):
                say(red(text))
            elif self.testing or 'discovered' in text:
                say(dim(text.strip()))
            return

        match = RESULT_LINE.match(text)
        if match:
            outcome, name, duration = match.groups()
            if outcome == 'Passed':
                say(f'  {green("PASS")}  {name} {dim(duration)}')
                self.block = None
            elif outcome == 'Failed':
                say(f'  {red("FAIL")}  {bold(name)} {dim(duration)}')
                self.block = 'failed'
            else:
                say(f'  {yellow("SKIP")}  {name} {dim(duration)}')
                self.block = None
            return

        stripped = text.strip()
        if self.block in ('failed', 'message', 'stack'):
            if stripped == 'Error Message:':
                self.block = 'message'
                return
            if stripped == 'Stack Trace:' or stripped.startswith('Standard Output Messages:'):
                self.block = 'stack'
                return
            if self.block == 'message':
                if stripped:
                    say(f'        {red(stripped)}')
                return
            if self.block == 'stack' and (not stripped or text.startswith('   ') or stripped.startswith('at ') or re.match(r'^\d+\)', stripped)):
                return
            self.block = None

        if re.match(r'^(Total tests|Test Run (Successful|Failed)|\s*(Passed|Failed|Skipped|Total time):)', stripped):
            return
        if stripped:
            say(dim(f'        {stripped}'))


def run_tests(arguments: list, results: Path, env: dict) -> int:
    trx = results / 'results.trx'
    command = ['dotnet', 'test', str(HERE / 'LabPrinterTests.csproj'), '--logger', 'console;verbosity=normal',
               '--logger', f'trx;LogFileName={trx}', '--results-directory', str(results), *arguments]
    with open(results / 'test-output.log', 'w') as log:
        output = LiveOutput(log)
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=env, bufsize=1)
        assert process.stdout is not None
        for raw in process.stdout:
            output.line(raw)
        return process.wait()


def read_trx(trx: Path) -> list:
    # The TRX file is written by dotnet test of this run, so the standard XML parser is fine here
    if not trx.exists():
        return []
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    results = []
    for result in ET.parse(trx).getroot().iterfind('.//t:UnitTestResult', ns):
        message = result.find('.//t:ErrorInfo/t:Message', ns)
        results.append({'name': result.get('testName'), 'outcome': result.get('outcome'), 'duration': result.get('duration'),
                        'message': (message.text or '').strip() if message is not None else ''})
    return results


# --- Evaluation (design/lab-test-plan.md, sections 9 and 10) ----------------------------------------------------------------------

# Findings, unless the test case expects them
FINDINGS = [
    'Lost connection to Duet', 'Maximum number of SPI transfer retries exceeded', 'timeout while waiting for TfrRdy pin',
    'No transfer with RepRapFirmware completed', 'Failed to start macro file', 'Out-of-order reply', 'Failed to process code in stage',
    'DivideByZeroException', 'transferDump',
]
# Expected once per RRF reboot
PER_REBOOT = ['Connection to controller has been reset', 'RepRapFirmware has left the extended link', 'Restarting full transfer because']
# Expected in some test cases
IN_SOME_TESTS = ['Failed to find corresponding state for code M291 P"never"', 'Machine is halted', 'SPI fault injected', 'Bad header CRC',
                 'Bad data CRC', 'Sending transfer #', 'keeping the plain link', 'RepRapFirmware is exchanging the data']
# Known, cause or assessment open
KNOWN_OPEN = ["State 'stop-sigterm' timed out", 'Failed to find query for object model response', 'Aborting orphaned macro file daemon.g',
              "Daemon: Out-of-order reply"]
# Outside the plan, only noted
NOTED = ['Client with outdated protocol version', 'has no value', 'predicted maximum temperature']
# Expected in some test cases as well: lines that RefusedMacroPaths causes on purpose, with the refused paths it uses. They are left out of
# the findings
REFUSED_PATH_TESTS = ['Failed to start macro file 99:/macros/lab-tests/refused/', 'Failed to start macro file 0:/../lab-tests-refused-']


def count_lines(journal: str, pattern: str) -> int:
    return sum(1 for line in journal.splitlines() if pattern in line)


def m122_values(text: str) -> dict:
    values = {}
    for name, pattern in [
            ('full transfer restarts', r'Full transfer restarts: (\d+)'), ('header retries', r'header retries: (\d+)'),
            ('data retries', r'data retries: (\d+)'), ('sent again', r'Transfers sent again: (\d+)'),
            ('requested again', r'transfers requested again: (\d+)'), ('extended left', r'extended link left by RRF: (\d+)'),
            ('lost connections', r'lost connections: (\d+)'), ('connection resets', r'connection resets: (\d+)'),
            ('RRF failed transfers', r'failed transfers: (\d+)'), ('RRF checksum errors', r'checksum errors: (\d+)'),
            ('RRF underruns', r'SPI underruns (\d+)'), ('RRF overruns', r'overruns (\d+)'),
            ('RRF disconnects', r'disconnects: (\d+)'), ('RRF timeouts', r'timeouts: (\d+) total')]:
        match = re.search(pattern, text)
        if match:
            values[name] = int(match.group(1))
    reset = re.search(r'Last reset (.+?), cause: (\w+)', text)
    if reset:
        values['last reset'] = f'{reset.group(1)}, cause {reset.group(2)}'
    return values


def counts_line(journal: str, patterns: list, bad: bool) -> list:
    lines = []
    for pattern in patterns:
        n = count_lines(journal, pattern)
        if n == 0 and not bad:
            continue
        number = f'{n:6d}'
        lines.append(f'  {(red(number) if n else green(number)) if bad else number}  {pattern}')
    return lines


def evaluate(start: dict, end: dict, journal: str, results: list, output_log: Path, results_dir: Path, rc: int, side_load: bool, fault: bool,
             plain: bool):
    say()
    say(bold('=== Evaluation (design/lab-test-plan.md, sections 9 and 10) ==='))
    say(f'Run: {start.get("time", "?")} to {end.get("time", "?")} UTC, DSF {start.get("dsf", "?")}, RRF {start.get("rrf", "?")}' +
        (', side-load' + (' with fault injection' + (' on the plain link' if plain else '') if fault else '') if side_load else ', image DCS'))

    # Tests
    passed = [r for r in results if r['outcome'] == 'Passed']
    failed = [r for r in results if r['outcome'] == 'Failed']
    explicit = [r for r in results if r['outcome'] == 'NotExecuted' and 'OneTimeSetUp' in r['message']]
    skipped = [r for r in results if r['outcome'] not in ('Passed', 'Failed') and r not in explicit]
    say()
    say(bold('Tests: ') + f'{green(str(len(passed)) + " passed")}, ' + (red(f'{len(failed)} failed') if failed else '0 failed') +
        f', {yellow(str(len(skipped)) + " skipped") if skipped else "0 skipped"}, {len(explicit)} explicit not run')
    for result in failed:
        say(f'  {red("FAIL")}  {result["name"]}: {result["message"].splitlines()[0] if result["message"] else ""}')
    for result in skipped:
        say(f'  {yellow("SKIP")}  {result["name"]}: {result["message"].splitlines()[0] if result["message"] else ""}')

    # Watchdog recovery times of FirmwareRestart
    output = output_log.read_text(errors='replace') if output_log.exists() else ''
    recoveries = [float(m) for m in re.findall(r'^Run \d+: idle again after ([\d.]+) s', output, re.MULTILINE)]
    if recoveries:
        say(f'Watchdog recovery ({len(recoveries)} reboots): min {min(recoveries):.1f} s, median {statistics.median(recoveries):.1f} s, '
            f'max {max(recoveries):.1f} s')

    # Journal
    say()
    say(bold('Journal of DCS') + f' ({len(journal.splitlines())} lines, {count_lines(journal, "Duet Control Server v")} DCS starts)')
    suppressed = sum(int(n) for n in re.findall(r'Suppressed (\d+) messages from duetcontrolserver', journal))
    if suppressed:
        say(f'  {red("journald suppressed " + str(suppressed) + " messages")}')
    say('Findings, unless the test case expects them:')
    findings_journal = '\n'.join(line for line in journal.splitlines() if not any(pattern in line for pattern in REFUSED_PATH_TESTS))
    for line in counts_line(findings_journal, FINDINGS, True):
        say(line)
    say('Expected once per RRF reboot:')
    for line in counts_line(journal, PER_REBOOT, False) or ['  none']:
        say(line)
    say('Expected in some test cases:')
    for line in counts_line(journal, IN_SOME_TESTS + REFUSED_PATH_TESTS, False) or ['  none']:
        say(line)
    say('Known, cause or assessment open:')
    for line in counts_line(journal, KNOWN_OPEN, False) or ['  none']:
        say(line)
    noted = counts_line(journal, NOTED, False)
    if noted:
        say('Outside the plan, only noted:')
        for line in noted:
            say(line)
    selftests = count_lines(journal, 'maintenance/self-test on channel HTTP')
    say(f'Self-test runs: {selftests} started, {count_lines(journal, "Self-test: quick check passed")} passed, '
        f'{count_lines(journal, "Self-test: quick check failed")} failed')

    # M122
    say()
    before, after = m122_values(start.get('m122', '')), m122_values(end.get('m122', ''))
    say(bold('M122') + dim(' (DSF counters count since the last DCS start, which the side-load removal restarts)' if side_load else ''))
    for name in after:
        if name == 'last reset':
            continue
        value = after[name]
        text = f'{before.get(name, "?")} -> {value}'
        say(f'  {name:22s} {red(text) if value and name.startswith(("lost", "RRF")) else text}')
    if 'last reset' in after:
        say(f'  {"RRF last reset":22s} {after["last reset"]}')

    # Restore check
    say()
    say(bold('Restore check (section 10)'))
    checks = [
        ('no side-load mounted', end.get('sideLoad') is None),
        (f'{SIDE_LOAD_DIR} removed', not end.get('sideLoadDir', True)),
        ('mp-ota-gate.timer active', end.get('ota') == 'active'),
        ('sys/trigger30.g removed', not end.get('trigger30', True)),
        ('0:/macros/lab-tests removed', not end.get('testMacros', True)),
        ('printer idle, no job', end.get('status') == 'idle' and not end.get('job')),
        ('expansion boards running', bool(end.get('boards')) and all(state == 'running' for state in end.get('boards', []))),
        (f'versions as before (DSF {end.get("dsf")}, RRF {end.get("rrf")})', end.get('dsf') == start.get('dsf') and end.get('rrf') == start.get('rrf')),
    ]
    for text, ok in checks:
        say(f'  {green("ok  ") if ok else red("FAIL")}  {text}')
    if 'error' in end:
        say(red(f'  The final state could not be read: {end["error"]}'))

    say()
    say(f'Files: {results_dir}')
    say(bold('Result: ') + (green('passed') if rc == 0 else red(f'failed (dotnet test exit code {rc})')) +
        dim(' (the journal findings above are not part of this result, see section 9)'))


# --- Main -----------------------------------------------------------------------------------------------------------------------

def main() -> int:
    arguments = sys.argv[1:]
    side_load = fault = plain = False
    if arguments[:1] == ['--side-load']:
        side_load = True
        arguments = arguments[1:]
        if arguments[:1] == ['--fault-injection']:
            fault = True
            arguments = arguments[1:]
            if arguments[:1] == ['--plain-link']:
                plain = True
                arguments = arguments[1:]

    results_dir = Path(os.environ.get('LAB_RESULTS_DIR', HERE / 'TestResults')) / datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S')
    results_dir.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, PATH=f'{Path.home() / ".dotnet"}:{os.environ.get("PATH", "")}')

    if sbc('true', 120).returncode != 0:
        say(red(f'Cannot reach {SBC_USER}@{SBC} through {JUMP_HOST}. Is the hardware token plugged in?'))
        return 1

    start = snapshot()
    if 'error' in start:
        say(red(f'Cannot read the state of the printer: {start["error"]}'))
    else:
        say(dim(f'Printer: DSF {start.get("dsf")}, RRF {start.get("rrf")}, status {start.get("status")}, journal cursor taken'))

    tunnel = None
    side_loaded = ota_stopped = False
    rc = 1

    def stop(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, stop)

    try:
        # An update must not start while the tests run
        ota_stopped = sbc_ok('sudo -n systemctl stop mp-ota-gate.timer')

        if side_load:
            with tempfile.TemporaryDirectory() as out:
                say(f'Publishing DCS from {ROOT}')
                subprocess.run(['dotnet', 'publish', str(ROOT / 'src/DuetControlServer/DuetControlServer.csproj'), '-c', 'Release', '-r', 'linux-arm64',
                                '--self-contained', 'false', '-o', out, '--nologo', '-v', 'quiet',
                                f'-p:SpiFaultInjection={"true" if fault else "false"}'], check=True, env=env)
                for dll in SIDE_LOAD_DLLS:
                    say(f'Uploading {dll}')
                    data = base64.b64encode((Path(out) / dll).read_bytes()).decode()
                    if not sbc_ok(f"sudo -n mkdir -p {SIDE_LOAD_DIR}\nbase64 -d <<'DLL' | sudo -n tee {SIDE_LOAD_DIR}/{dll} > /dev/null\n{data}\nDLL\n"):
                        raise RuntimeError(f'Uploading {dll} failed')
            # The flag must be there before DCS starts, and a run without it must not find one that an interrupted run left behind
            sbc_ok(''.join(f'sudo -n mount --bind {SIDE_LOAD_DIR}/{dll} /opt/dsf/bin/{dll}\n' for dll in SIDE_LOAD_DLLS) +
                   (f'sudo -n touch {PLAIN_LINK_FILE}\n' if plain else f'sudo -n rm -f {PLAIN_LINK_FILE}\n') +
                   'sudo -n systemctl restart duetcontrolserver || true\n')
            side_loaded = True
            if not sbc_ok(RESET_FIRMWARE):
                raise RuntimeError('The printer did not become idle after the side-load')
            revision = subprocess.run(['git', '-C', str(ROOT), 'rev-parse', '--short', 'HEAD'], capture_output=True, text=True).stdout.strip()
            dirty = subprocess.run(['git', '-C', str(ROOT), 'diff', '--quiet', 'HEAD', '--', 'src']).returncode != 0
            say(f'Side-loaded {" ".join(SIDE_LOAD_DLLS)} from {revision}{" with uncommitted changes" if dirty else ""}{", fault injection build" if fault else ""}'
                f'{" on the plain link" if plain else ""}')

        tunnel = subprocess.Popen([sys.executable, '-I', str(HERE / 'tunnel.py'), PLINK, JUMP_HOST, f'{SBC}:80', str(TUNNEL_PORT)])
        url = f'http://127.0.0.1:{TUNNEL_PORT}'
        for attempt in range(20):
            try:
                urllib.request.urlopen(f'{url}/machine/status', timeout=5).read()
                break
            except Exception:
                if attempt == 19:
                    raise RuntimeError("The tunnel to the printer's HTTP API does not work")
                time.sleep(1)

        env['LAB_PRINTER_URL'] = url
        env['LAB_PRINTER_SHELL'] = shlex.join(SBC_COMMAND)
        rc = run_tests(arguments, results_dir, env)
    except KeyboardInterrupt:
        say(red('Interrupted, cleaning up'))
        rc = 130
    except Exception as e:
        say(red(str(e)))
        rc = 1
    finally:
        if tunnel is not None:
            tunnel.terminate()
        if side_loaded:
            say('Removing the side-loaded DLLs')
            # A plain umount reports the DLLs as busy. The first new instance may fail to connect, e.g. while RRF still waits for the
            # old one, and systemd starts it again
            sbc_ok(''.join(f'findmnt -n /opt/dsf/bin/{dll} > /dev/null && sudo -n umount -l /opt/dsf/bin/{dll}\n' for dll in SIDE_LOAD_DLLS) +
                   f'sudo -n rm -rf {SIDE_LOAD_DIR}\nsudo -n systemctl restart duetcontrolserver || true\n')
            if not sbc_ok(RESET_FIRMWARE):
                rc = rc or 1
        if ota_stopped:
            sbc_ok('sudo -n systemctl start mp-ota-gate.timer')

    end = snapshot()
    journal = journal_since(start['cursor']) if start.get('cursor') else ''
    (results_dir / 'journal.txt').write_text(journal)
    evaluate(start, end, journal, read_trx(results_dir / 'results.trx'), results_dir / 'test-output.log', results_dir, rc, side_load, fault, plain)
    return rc


if __name__ == '__main__':
    sys.exit(main())
