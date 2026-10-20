#!/usr/bin/env python3
"""Documentation gate: links, cross-language feature parity and original SVG validity."""
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PAGES = ("README.md", "README.zh-TW.md", "README.ja.md")
ASSETS = ("docs/assets/architecture-rings.svg", "docs/assets/request-boundaries.svg")
ROUTES = (
    "POST /api/competitions",
    "POST /api/competitions/{id}/participants",
    "POST /api/competitions/{id}/results",
    "POST /api/competitions/{id}/results/{resultId}/corrections",
    "GET /api/competitions/{id}/leaderboard",
    "GET /api/competitions/{id}/me/history",
    "GET /health",
)
TERMS = ("input boundary", "output boundary", "presenter", "gateway",
         "postgresql", "opentelemetry", "opaqueevidence", "idempotency-key")

LINKS = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")


def inspect_page(name: str, body: str) -> list[str]:
    problems: list[str] = []
    lowered = body.casefold()
    for term in TERMS:
        if term not in lowered:
            problems.append(f"{name}: missing architecture or trust term")
    for route in ROUTES:
        if route not in body:
            problems.append(f"{name}: missing documented HTTP route")
    for img in ASSETS:
        if img not in body:
            problems.append(f"{name}: missing one of the two reference diagrams")

    for match in LINKS.finditer(body):
        destination = match.group(1).split("#", 1)[0].split("?", 1)[0]
        if not destination or "://" in destination or destination.startswith(("mailto:", "#")):
            continue
        if destination.startswith("/") or ".." in Path(destination).parts:
            problems.append(f"{name}: invalid relative link")
            continue
        if not (ROOT / destination).is_file():
            problems.append(f"{name}: missing local link target")
    return problems


def main() -> int:
    if "--self-test" in sys.argv:
        # Negative control: stripping the content must cause the gate to fail.
        assert inspect_page("README.md", "")
        actual = (ROOT / "README.md").read_text(encoding="utf-8")
        assert not inspect_page("README.md", actual)
        # Also prove missing local links are not silently accepted.
        assert any("missing local link" in failure for failure in
                   inspect_page("README.md", actual + "\\n[broken](missing-local-doc.md)"))
        print("Documentation negative control passed.")
        return 0

    failures: list[str] = []
    for name in PAGES:
        page = ROOT / name
        if not page.exists():
            failures.append(f"{name}: missing README")
        else:
            failures.extend(inspect_page(name, page.read_text(encoding="utf-8")))
    for name in ASSETS:
        try:
            svg = ET.parse(ROOT / name).getroot()
            if svg.tag != "{http://www.w3.org/2000/svg}svg":
                failures.append(f"{name}: root is not SVG")
            if any(item.tag.endswith(("script", "foreignObject")) for item in svg.iter()):
                failures.append(f"{name}: disallowed active content")
        except (OSError, ET.ParseError) as error:
            failures.append(f"{name}: invalid XML ({type(error).__name__})")
    if failures:
        print("\n".join(failures))
        return 1
    print("Documentation gate PASS: three localized READMEs, local links and both SVGs.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
