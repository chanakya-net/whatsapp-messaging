#!/usr/bin/env python3
"""Validate merged ReportGenerator or raw Coverlet Cobertura output."""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path


COMBINED_LINE_THRESHOLD = 85.0
COMBINED_BRANCH_THRESHOLD = 80.0
PROJECT_LINE_THRESHOLD = 80.0
BRANCH_COUNTS = re.compile(r"\((\d+)\s*/\s*(\d+)\)")


@dataclass
class Counts:
    covered_lines: int = 0
    valid_lines: int = 0
    covered_branches: int = 0
    valid_branches: int = 0

    def add(self, other: "Counts") -> None:
        self.covered_lines += other.covered_lines
        self.valid_lines += other.valid_lines
        self.covered_branches += other.covered_branches
        self.valid_branches += other.valid_branches


@dataclass
class Coverage:
    combined: Counts = field(default_factory=Counts)
    projects: dict[str, Counts] = field(default_factory=dict)


def percentage(covered: int, valid: int) -> float:
    return covered * 100.0 / valid if valid else 0.0


def parse_reportgenerator_summary(path: Path) -> Coverage:
    root = ET.parse(path).getroot()
    summary = root.find("Summary")
    if summary is None:
        raise ValueError(f"ReportGenerator summary has no Summary element: {path}")

    combined = Counts(
        int(summary.findtext("Coveredlines", "0")),
        int(summary.findtext("Coverablelines", "0")),
        int(summary.findtext("Coveredbranches", "0")),
        int(summary.findtext("Totalbranches", "0")),
    )
    projects: dict[str, Counts] = {}
    for assembly in root.findall("./Coverage/Assembly"):
        projects[assembly.attrib["name"]] = Counts(
            int(assembly.attrib.get("coveredlines", "0")),
            int(assembly.attrib.get("coverablelines", "0")),
            int(assembly.attrib.get("coveredbranches", "0")),
            int(assembly.attrib.get("totalbranches", "0")),
        )
    return Coverage(combined, projects)


def parse_cobertura(path: Path) -> Coverage:
    root = ET.parse(path).getroot()
    coverage = Coverage()
    seen_lines: set[tuple[str, str, str]] = set()
    seen_branches: set[tuple[str, str, str]] = set()

    for package in root.findall("./packages/package"):
        project = package.attrib.get("name", "unknown")
        project_counts = coverage.projects.setdefault(project, Counts())
        for class_element in package.findall("./classes/class"):
            filename = class_element.attrib.get("filename", "")
            for line in class_element.findall("./lines/line"):
                number = line.attrib.get("number", "")
                line_key = (project, filename, number)
                if line_key not in seen_lines:
                    seen_lines.add(line_key)
                    count = Counts(valid_lines=1, covered_lines=int(int(line.attrib.get("hits", "0")) > 0))
                    project_counts.add(count)
                    coverage.combined.add(count)

                if line.attrib.get("branch", "False").lower() == "true":
                    branch_match = BRANCH_COUNTS.search(line.attrib.get("condition-coverage", ""))
                    if branch_match and line_key not in seen_branches:
                        seen_branches.add(line_key)
                        count = Counts(
                            covered_branches=int(branch_match.group(1)),
                            valid_branches=int(branch_match.group(2)),
                        )
                        project_counts.add(count)
                        coverage.combined.add(count)

    if coverage.combined.valid_lines == 0:
        coverage.combined = Counts(
            int(root.attrib.get("lines-covered", "0")),
            int(root.attrib.get("lines-valid", "0")),
            int(root.attrib.get("branches-covered", "0")),
            int(root.attrib.get("branches-valid", "0")),
        )
    return coverage


def load_coverage(input_path: Path) -> Coverage:
    if input_path.is_file():
        root = ET.parse(input_path).getroot()
        if root.tag == "CoverageReport":
            return parse_reportgenerator_summary(input_path)
        return parse_cobertura(input_path)

    summary = input_path / "Summary.xml"
    if summary.is_file():
        return parse_reportgenerator_summary(summary)

    files = sorted(input_path.glob("**/coverage.cobertura.xml"))
    if not files:
        raise ValueError(f"No Cobertura files found in {input_path}")
    combined = Coverage()
    for path in files:
        parsed = parse_cobertura(path)
        combined.combined.add(parsed.combined)
        for project, counts in parsed.projects.items():
            combined.projects.setdefault(project, Counts()).add(counts)
    return combined


def validate(coverage: Coverage) -> tuple[bool, list[str]]:
    messages = [
        "Combined line: "
        f"{percentage(coverage.combined.covered_lines, coverage.combined.valid_lines):.1f}% "
        f"({coverage.combined.covered_lines}/{coverage.combined.valid_lines}), "
        f"threshold {COMBINED_LINE_THRESHOLD:.1f}%",
        "Combined branch: "
        f"{percentage(coverage.combined.covered_branches, coverage.combined.valid_branches):.1f}% "
        f"({coverage.combined.covered_branches}/{coverage.combined.valid_branches}), "
        f"threshold {COMBINED_BRANCH_THRESHOLD:.1f}%",
    ]
    failures: list[str] = []
    combined_line = percentage(coverage.combined.covered_lines, coverage.combined.valid_lines)
    combined_branch = percentage(coverage.combined.covered_branches, coverage.combined.valid_branches)
    if combined_line < COMBINED_LINE_THRESHOLD:
        failures.append(f"Combined line {combined_line:.1f}% below {COMBINED_LINE_THRESHOLD:.1f}%")
    if combined_branch < COMBINED_BRANCH_THRESHOLD:
        failures.append(f"Combined branch {combined_branch:.1f}% below {COMBINED_BRANCH_THRESHOLD:.1f}%")

    for project in sorted(coverage.projects):
        counts = coverage.projects[project]
        if counts.valid_lines == 0:
            messages.append(f"Project {project} line: n/a (0/0), no coverable lines")
            continue
        project_line = percentage(counts.covered_lines, counts.valid_lines)
        messages.append(
            f"Project {project} line: {project_line:.1f}% "
            f"({counts.covered_lines}/{counts.valid_lines}), threshold {PROJECT_LINE_THRESHOLD:.1f}%"
        )
        if project_line < PROJECT_LINE_THRESHOLD:
            failures.append(f"Project {project} line {project_line:.1f}% below {PROJECT_LINE_THRESHOLD:.1f}%")
    return not failures, messages + [f"FAIL: {failure}" for failure in failures]


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(f"Usage: {Path(argv[0]).name} <cobertura-directory-or-summary>", file=sys.stderr)
        return 2
    try:
        coverage = load_coverage(Path(argv[1]))
        passed, messages = validate(coverage)
    except (OSError, ET.ParseError, ValueError) as error:
        print(f"Coverage validation error: {error}", file=sys.stderr)
        return 2
    print("\n".join(messages))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
