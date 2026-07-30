#!/usr/bin/env python3
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

def extract_coverage_from_cobertura(cobertura_files):
    """Extract combined line and branch coverage from Cobertura XML files."""
    total_lines_valid = 0
    total_lines_covered = 0
    total_branches_valid = 0
    total_branches_covered = 0

    for cobertura_file in cobertura_files:
        tree = ET.parse(cobertura_file)
        root = tree.getroot()

        for package in root.findall('.//package'):
            lines_valid = int(package.get('line-rate', '0')) * 100 if '.' not in package.get('line-rate', '0') else float(package.get('line-rate', '0')) * 100
            branches_valid = int(package.get('branch-rate', '0')) * 100 if '.' not in package.get('branch-rate', '0') else float(package.get('branch-rate', '0')) * 100

            total_lines_valid += lines_valid
            total_branches_valid += branches_valid

    # Normalize to percentage: Cobertura stores as decimal (0.85 = 85%)
    if total_lines_valid > 0:
        combined_line_percent = (total_lines_covered / total_lines_valid * 100) if total_lines_valid else 0
    else:
        combined_line_percent = 0

    if total_branches_valid > 0:
        combined_branch_percent = (total_branches_covered / total_branches_valid * 100) if total_branches_valid else 0
    else:
        combined_branch_percent = 0

    return combined_line_percent, combined_branch_percent

def validate_thresholds(line_percent, branch_percent, line_threshold=85, branch_threshold=80):
    """Validate combined coverage against thresholds."""
    passed = True
    messages = []

    if line_percent < line_threshold:
        passed = False
        messages.append(f"Line coverage {line_percent:.1f}% below threshold {line_threshold}%")

    if branch_percent < branch_threshold:
        passed = False
        messages.append(f"Branch coverage {branch_percent:.1f}% below threshold {branch_threshold}%")

    return passed, messages

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: validator.py <cobertura_dir>")
        sys.exit(1)

    coverage_dir = Path(sys.argv[1])
    cobertura_files = list(coverage_dir.glob("**/coverage.cobertura.xml"))

    if not cobertura_files:
        print(f"No Cobertura files found in {coverage_dir}")
        sys.exit(1)

    line_percent, branch_percent = extract_coverage_from_cobertura(cobertura_files)
    passed, messages = validate_thresholds(line_percent, branch_percent)

    print(f"Combined Coverage: Line={line_percent:.1f}%, Branch={branch_percent:.1f}%")
    for msg in messages:
        print(f"  ✗ {msg}")

    sys.exit(0 if passed else 1)
