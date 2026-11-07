#!/usr/bin/env python3
"""Line coverage of DSF by the unit tests, per component, checked against the coverage goals.

Runs the unit tests under dotnet-coverage (a global tool: dotnet tool install -g dotnet-coverage) and summarizes the
Cobertura report. DSF here means the libraries and services that run on the SBC or that clients build on; the
command-line tools, the plugins and generated code are left out.

  python3 src/UnitTests/coverage.py                  run the tests, print the summary and check the goals
  python3 src/UnitTests/coverage.py --report FILE    summarize an existing Cobertura report instead of running the tests
  python3 src/UnitTests/coverage.py --files NAME     also list the files of the components whose name contains NAME
  python3 src/UnitTests/coverage.py --uncovered PATH list the uncovered lines of the files whose path contains PATH

The exit code is 1 if a goal is missed, so the script can gate a branch.
"""
import argparse
import collections
import os
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET  # the report is written locally by dotnet-coverage, not untrusted input

SRC = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Assemblies that make up DSF for the overall goal
ASSEMBLIES = ["DuetAPI", "DuetAPIClient", "DuetControlServer", "DuetHttpClient", "DuetSharedLibrary", "DuetWebServer"]

OVERALL_GOAL = 50.0
CRITICAL_GOAL = 80.0

# Critical components: (name, path prefixes relative to src/, excluded path prefixes).
# They handle input from the firmware, files, clients and the network, or keep the machine and the job in a safe state
CRITICAL = [
    ("SBC link", ["DuetControlServer/Link/"], ["DuetControlServer/Link/Adapter/USB.cs"]),
    ("G-code parser", ["DuetAPI/Commands/Code/"], []),
    ("Code pipeline", ["DuetControlServer/Codes/"], ["DuetControlServer/Codes/Meta/"]),
    ("Meta G-code", ["DuetControlServer/Codes/Meta/"], []),
    ("Jobs and macros", ["DuetControlServer/Files/CodeBlock.cs", "DuetControlServer/Files/CodeFile.cs", "DuetControlServer/Files/CodeFilePath.cs",
                         "DuetControlServer/Files/FilePathResolver.cs", "DuetControlServer/Files/JobProcessor.cs", "DuetControlServer/Files/MacroFile.cs"], []),
    ("IPC server", ["DuetControlServer/IPC/"], []),
    ("HTTP sessions and API", ["DuetWebServer/Authorization/", "DuetWebServer/Endpoints/", "DuetWebServer/Services/SessionExpiry.cs",
                               "DuetWebServer/Singletons/SessionStorage.cs"], []),
]


def relative_path(filename):
    """Path of a source file relative to src/, or None if it is not part of DSF"""
    match = re.search(r".*/src/(.*)$", filename.replace("\\", "/"))
    if match is None:
        return None
    path = match.group(1)
    if "/obj/" in path or path.split("/")[0] not in ASSEMBLIES:
        return None
    return path


def read_report(report):
    """Read a Cobertura report into {relative path: {line: hits}}"""
    files = collections.defaultdict(dict)
    for cls in ET.parse(report).getroot().iter("class"):
        path = relative_path(cls.get("filename"))
        if path is None:
            continue
        lines = files[path]
        for line in cls.iter("line"):
            number, hits = int(line.get("number")), int(line.get("hits"))
            lines[number] = max(lines.get(number, 0), hits)
    return files


def matches(path, prefixes, excluded):
    return any(path.startswith(p) for p in prefixes) and not any(path.startswith(p) for p in excluded)


def count(files, paths):
    covered = sum(1 for p in paths for hits in files[p].values() if hits > 0)
    total = sum(len(files[p]) for p in paths)
    return covered, total


def percent(covered, total):
    return 100.0 * covered / total if total else 100.0


def run_tests(filter_expression):
    tool = shutil.which("dotnet-coverage") or os.path.expanduser("~/.dotnet/tools/dotnet-coverage")
    if not os.path.exists(tool):
        sys.exit("dotnet-coverage not found, install it with: dotnet tool install -g dotnet-coverage")
    report = os.path.join(tempfile.mkdtemp(prefix="dsf-coverage-"), "coverage.cobertura.xml")
    command = f"dotnet test \"{os.path.join(SRC, 'UnitTests', 'UnitTests.csproj')}\""
    if filter_expression:
        command += f" --filter \"{filter_expression}\""
    result = subprocess.run([tool, "collect", "-f", "cobertura", "-o", report, command])
    if result.returncode != 0 or not os.path.exists(report):
        sys.exit(f"The tests failed (exit code {result.returncode}), so the coverage does not count")
    return report


def ranges(numbers):
    """Compress sorted line numbers into ranges such as 10-14"""
    result, start, last = [], None, None
    for n in numbers:
        if start is None:
            start = last = n
        elif n == last + 1:
            last = n
        else:
            result.append(f"{start}-{last}" if start != last else f"{start}")
            start = last = n
    if start is not None:
        result.append(f"{start}-{last}" if start != last else f"{start}")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--report", help="existing Cobertura report to summarize")
    parser.add_argument("--filter", help="dotnet test filter, for a quick look at a part of the tests (the goals are not checked then)")
    parser.add_argument("--files", help="list the files of the components whose name contains this text")
    parser.add_argument("--uncovered", help="list the uncovered lines of the files whose path contains this text")
    args = parser.parse_args()

    report = args.report or run_tests(args.filter)
    files = read_report(report)
    print(f"Report: {report}\n")

    failed = []
    print(f"{'Critical component':<28} {'Lines':>13} {'Coverage':>9}  Goal {CRITICAL_GOAL:.0f}%")
    for name, prefixes, excluded in CRITICAL:
        paths = sorted(p for p in files if matches(p, prefixes, excluded))
        covered, total = count(files, paths)
        value = percent(covered, total)
        # dotnet-coverage sees only the assemblies the tests load, so a component without lines was not measured at all
        ok = total > 0 and value >= CRITICAL_GOAL
        if not ok:
            failed.append(name)
        status = "ok" if ok else "MISSED" if total else "MISSED (not loaded by the tests)"
        print(f"{name:<28} {covered:>6}/{total:<6} {value:>8.1f}%  {status}")
        if args.files and args.files.lower() in name.lower():
            for path in paths:
                c, t = count(files, [path])
                print(f"    {percent(c, t):5.1f}% {c:5}/{t:<5} {path}")

    print(f"\n{'Assembly':<28} {'Lines':>13} {'Coverage':>9}")
    all_paths = sorted(files)
    not_loaded = []
    for assembly in ASSEMBLIES:
        paths = [p for p in all_paths if p.startswith(assembly + "/")]
        covered, total = count(files, paths)
        if total == 0:
            not_loaded.append(assembly)
        print(f"{assembly:<28} {covered:>6}/{total:<6} {percent(covered, total):>8.1f}%{'  not loaded by the tests' if total == 0 else ''}")
    covered, total = count(files, all_paths)
    overall = percent(covered, total)
    # The lines of an assembly that no test loads are missing from the total, which would make the overall figure look better than it is
    ok = overall >= OVERALL_GOAL and not not_loaded
    if not ok:
        failed.append("DSF overall")
    print(f"{'DSF overall':<28} {covered:>6}/{total:<6} {overall:>8.1f}%  Goal {OVERALL_GOAL:.0f}%: {'ok' if ok else 'MISSED'}")

    if args.uncovered:
        print()
        for path in all_paths:
            if args.uncovered in path:
                missing = sorted(n for n, hits in files[path].items() if hits == 0)
                print(f"{path}: {len(missing)} uncovered lines: {', '.join(ranges(missing))}")

    if args.filter:
        print("\nOnly a part of the tests ran, so the goals are not checked")
        return 0
    if failed:
        print(f"\nGoals missed: {', '.join(failed)}")
        return 1
    print("\nAll coverage goals met")
    return 0


if __name__ == "__main__":
    sys.exit(main())
