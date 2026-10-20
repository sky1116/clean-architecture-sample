#!/usr/bin/env python3
"""Public-repository publication gate. Sensitive source identifiers are represented as hashes, never cleartext."""
import hashlib
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
# SHA-256 of lower-cased prohibited source identifiers (their original spellings are intentionally absent).
# An additional, privately managed comma-separated list may be passed via PUBLICATION_RESTRICTED_TERMS.
DENIED = set()
forbidden_lengths = set()
for raw in os.environ.get("PUBLICATION_RESTRICTED_TERMS", "").split(","):
    token = raw.strip().lower()
    if token:
        DENIED.add(hashlib.sha256(token.encode("utf-8")).hexdigest())
        forbidden_lengths.add(len(token))

def main() -> int:
    tracked = subprocess.check_output(["git", "ls-files"], cwd=ROOT, text=True).splitlines()
    errors = []
    for path in tracked:
        if path.startswith(".git/"):
            continue
        file = ROOT / path
        if not file.is_file():
            continue
        raw = file.read_bytes()
        if b"\0" in raw[:4096]:
            continue
        contents = raw.decode("utf-8", errors="replace")
        if re.search(r"file:///|(?:[A-Za-z]:\\\\)", contents):
            errors.append(f"{path}: local absolute path")
        lowered = (path + "\n" + contents).lower()
        for length in forbidden_lengths:
            for index in range(len(lowered) - length + 1):
                token_hash = hashlib.sha256(lowered[index:index + length].encode("utf-8")).hexdigest()
                if token_hash in DENIED:
                    errors.append(f"{path}: prohibited identifier")
                    break
    if errors:
        print("\n".join(errors))
        return 1
    print(f"Publication hygiene checked {len(tracked)} tracked paths. "
          + ("External restricted-term scan enabled." if DENIED else "External restricted-term list not supplied."))
    return 0

if __name__ == "__main__":
    sys.exit(main())
