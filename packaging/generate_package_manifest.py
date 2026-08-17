"""Generate package/version and SHA-256 manifests for a staged release."""

from __future__ import annotations

import argparse
import csv
import hashlib
import importlib.metadata
import json
from datetime import datetime, timezone
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--packages-only", action="store_true")
    args = parser.parse_args()
    root = args.root.resolve()
    license_dir = root / "licenses"
    license_dir.mkdir(parents=True, exist_ok=True)

    distributions = []
    for dist in importlib.metadata.distributions():
        metadata = dist.metadata
        distributions.append(
            {
                "name": metadata.get("Name", ""),
                "version": dist.version,
                "license": metadata.get("License", ""),
                "homepage": metadata.get("Home-page", ""),
            }
        )
    distributions.sort(key=lambda item: item["name"].casefold())
    with (license_dir / "python-packages.csv").open(
        "w", newline="", encoding="utf-8-sig"
    ) as output:
        writer = csv.DictWriter(
            output, fieldnames=("name", "version", "license", "homepage")
        )
        writer.writeheader()
        writer.writerows(distributions)

    if args.packages_only:
        print(
            json.dumps(
                {"packages": len(distributions), "manifest": "python-packages.csv"}
            )
        )
        return 0

    manifest_path = root / "release-manifest.json"
    files = []
    for path in sorted(root.rglob("*")):
        if not path.is_file() or path == manifest_path:
            continue
        files.append(
            {
                "path": path.relative_to(root).as_posix(),
                "size": path.stat().st_size,
                "sha256": sha256(path),
            }
        )
    payload = {
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "file_count": len(files),
        "total_size": sum(item["size"] for item in files),
        "files": files,
    }
    manifest_path.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(
        json.dumps(
            {
                "file_count": payload["file_count"],
                "total_size": payload["total_size"],
                "manifest": str(manifest_path),
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
