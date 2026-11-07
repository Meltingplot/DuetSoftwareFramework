#!/bin/bash
# Run the lab printer tests through a plink tunnel, see README.md and run-lab-tests.py
#
# Usage: run-lab-tests.sh [--side-load [--fault-injection]] [dotnet test arguments...]
exec python3 "$(dirname "$0")/run-lab-tests.py" "$@"
