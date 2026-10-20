#!/usr/bin/env python3
"""Public source-identifier leak check. Forbidden source names are NEVER stored in cleartext."""
import hashlib
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
# SHA-256 fingerprints of prohibited identifiers (lowercase UTF-8). Never publish the words themselves.
FINGERPRINTS = {
    7: ("76d9150b7103359c51f0a10201ae5d274d9d39da9b812e82a0cb6bf5ec47af6e",),
    3: ("28d73ad8c2a61db058b96b63d9b25a63c518da5f026e2c9785f09e17c443cecd",),
    5: ("f5cfcb570b7edac2ed16e1a025d50155d6148de7397f4068790cdfc142300070",),
    10: ("2914994602a4844c881a460cf045fec785d3a388084ae2ed88f685923b40e218",),
    9: ("41bbdda8697a11dc3f0c86dcd6812357b20d292dbfbeb190dce8727dc5afd9f0",
        "0f98d3cea49eb3507999d980fbf36ac4a22f9b913d5f68192901a0178fcaca39"),
    13: ("9d39df9a978b88c24d3f85d232548bac043b2ae21809a3f2a1baa27834815ad4",),
}

def matches_restricted_token(haystack: str) -> bool:
    lower = haystack.casefold()
    for length, digests in FINGERPRINTS.items():
        targets = set(digests)
        for offset in range(len(lower) - length + 1):
            segment = lower[offset:offset + length]
            if not segment.isascii():
                continue
            if hashlib.sha256(segment.encode("utf-8")).hexdigest() in targets:
                return True
    return False

def main() -> int:
    entries = subprocess.check_output(["git", "ls-files"], cwd=ROOT, text=True).splitlines()
    errors = []
    private_terms = [term.strip().casefold() for term in
                     os.getenv("PUBLICATION_RESTRICTED_TERMS", "").split(",") if term.strip()]
    for rel in entries:
        file = ROOT / rel
        if not file.is_file():
            continue
        raw = file.read_bytes()
        if b"\x00" in raw[:4096]:
            continue
        text = raw.decode("utf-8", errors="replace")
        if rel != "scripts/check_publication.py" and ("file:///" in text or
                re.search(r"[A-Za-z]:\\(?:Users|Projects|Work|Desktop)\\", text)):
            errors.append(f"{rel}: local filesystem address")
        content = rel + "\n" + text
        if matches_restricted_token(content) or any(word in content.casefold() for word in private_terms):
            errors.append(f"{rel}: prohibited origin identifier")
    if errors:
        print("\n".join(errors))
        return 1
    print(f"Publication check passed for {len(entries)} tracked files "
          "(embedded hashed denylist; external list " +
          ("enabled)." if private_terms else "not provided)."))
    return 0

if __name__ == "__main__":
    sys.exit(main())
