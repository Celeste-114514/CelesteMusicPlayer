"""Static candidates, not a claim that every match is a visible defect or unique interaction."""
from pathlib import Path
import csv
import re

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "outputs/webview2-shell"
TARGET = ROOT / "collab/AUDIT-20261027-webview-controls-motion.csv"
PATTERNS = {
    "event": r"\baddEventListener\s*\(|\.on(?:click|change|input|keydown|pointerdown|pointermove|pointerup|blur|focus)\s*=",
    "select": r"<select\b|(?:createElement|node|el|element)\(\s*['\"]select['\"]",
    "checkbox_radio": r"(?:\.type\s*=\s*|type\s*=\s*|type\s*:\s*)['\"](?:checkbox|radio)['\"]",
    "color": r"(?:\.type\s*=\s*|type\s*=\s*|type\s*:\s*)['\"]color['\"]",
    "range": r"(?:\.type\s*=\s*|type\s*=\s*|type\s*:\s*)['\"]range['\"]",
    "native_confirm": r"\bconfirm\s*\(",
    "dialog": r"<dialog\b|createElement\(\s*['\"]dialog['\"]|node\(\s*['\"]dialog['\"]",
    "motion": r"\btransition\s*:|\banimation\s*:|\.animate\s*\(|@keyframes\b",
    "focus_suppression": r"outline\s*:\s*(?:none|0)(?:\s|;|})",
    "hardcoded_color": r"#[0-9a-fA-F]{3,8}\b|\brgba?\([^)]*\)",
}

rows = []
sources = sorted(p for p in SOURCE.iterdir() if p.suffix in (".html", ".js", ".css"))
for path in sources:
    for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        for kind, pattern in PATTERNS.items():
            matches = list(re.finditer(pattern, line))
            if not matches:
                continue
            start = max(0, matches[0].start() - 60)
            rows.append({"file": path.relative_to(ROOT).as_posix(), "line": number,
                         "kind": kind, "occurrences": len(matches), "excerpt": line[start:start + 400].strip()})
with TARGET.open("w", encoding="utf-8-sig", newline="") as output:
    writer = csv.DictWriter(output, fieldnames=["file", "line", "kind", "occurrences", "excerpt"])
    writer.writeheader()
    writer.writerows(rows)
print(f"Scanned {len(sources)} source files; wrote {TARGET.relative_to(ROOT)}")
for kind in PATTERNS:
    print(kind, sum(row["occurrences"] for row in rows if row["kind"] == kind))
