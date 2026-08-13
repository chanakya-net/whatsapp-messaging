#!/usr/bin/env python3
"""Executable self-tests for the shipped coverage validator."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path
import xml.etree.ElementTree as ET


ROOT = Path(__file__).parent
VALIDATOR = ROOT / "validator.py"
FIXTURES = ROOT / "fixtures"


def run(name: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(VALIDATOR), str(FIXTURES / name)],
        check=False,
        capture_output=True,
        text=True,
    )


def assert_case(name: str, expected_code: int, expected_text: str) -> str:
    result = run(name)
    output = result.stdout + result.stderr
    assert result.returncode == expected_code, f"{name}: {result.returncode}\n{output}"
    assert expected_text in output, f"{name}: missing {expected_text!r}\n{output}"
    return output


def main() -> int:
    runsettings = ROOT.parent.parent / "tests" / "coverlet.runsettings"
    exclusion_value = ET.parse(runsettings).findtext(
        ".//DataCollector[@friendlyName='XPlat code coverage']/Configuration/Exclude"
    )
    assert exclusion_value is not None, "coverage collector must configure exclusions"
    assert "[JasperFx]*" in exclusion_value, "coverage must exclude the JasperFx dependency assembly"

    exclusion_report = FIXTURES / "exclusions" / "coverage.cobertura.xml"
    report_text = exclusion_report.read_text(encoding="utf-8")
    assert "MessageBridge.Application.OrdinaryClass" in report_text
    for excluded in ("Migrations.", "/obj/", "/bin/", "MessageBridge.Worker.Program"):
        assert excluded not in report_text, f"excluded output contains {excluded}"
    ET.parse(exclusion_report)

    passing = assert_case("passing", 0, "Combined line: 94.4% (17/18)")
    assert "Combined branch: 88.9% (8/9)" in passing
    assert "Project LowProject line: 100.0% (9/9)" in passing
    assert_case("low-combined-line", 1, "FAIL: Combined line 60.0% below 85.0%")
    assert_case("low-combined-branch", 1, "FAIL: Combined branch 25.0% below 80.0%")
    assert_case("low-project-line", 1, "FAIL: Project LowProject line 70.0% below 80.0%")
    assert run("passing").stdout == passing
    print("coverage self-tests passed: thresholds, deterministic output, approved exclusion fixture")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
