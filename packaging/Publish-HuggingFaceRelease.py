#!/usr/bin/env python3
"""Publish immutable release artifacts; activate the signed pointer only after GitHub succeeds."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
from pathlib import Path
from urllib.parse import quote


def digest(path: Path, algorithm: str = "sha256") -> str:
    hasher = hashlib.new(algorithm)
    with path.open("rb") as source:
        for block in iter(lambda: source.read(4 * 1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def verify_remote(api, repository: str, remote_path: str, local: Path, token: str):
    info = api.model_info(repository, files_metadata=True, token=token)
    remote = next((s for s in info.siblings if s.rfilename == remote_path), None)
    if remote is None:
        return False
    if remote.size != local.stat().st_size:
        raise RuntimeError(f"Immutable release file size differs: {remote_path}")
    lfs = getattr(remote, "lfs", None)
    remote_sha = lfs.get("sha256") if isinstance(lfs, dict) else getattr(lfs, "sha256", None)
    if remote_sha:
        matches = remote_sha.lower() == digest(local)
    else:
        hasher = hashlib.sha1(f"blob {local.stat().st_size}\0".encode())
        with local.open("rb") as source:
            for block in iter(lambda: source.read(4 * 1024 * 1024), b""):
                hasher.update(block)
        matches = hasher.hexdigest() == getattr(remote, "blob_id", "")
    if not matches:
        raise RuntimeError(f"Immutable release file hash differs: {remote_path}")
    return True


def verify_manifest(data: bytes, public_key: str):
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import padding, rsa
    envelope = json.loads(data)
    payload = base64.b64decode(envelope["payload"], validate=True)
    key = serialization.load_pem_public_key(public_key.encode())
    if not isinstance(key, rsa.RSAPublicKey) or key.key_size < 2048:
        raise RuntimeError("An RSA public key of at least 2048 bits is required.")
    key.verify(base64.b64decode(envelope["signature"], validate=True), payload, padding.PKCS1v15(), hashes.SHA256())
    return json.loads(payload)


def publish(api, repository: str, version: str, installer: Path, update_root: Path, token: str, activate: bool = False, public_key: str = "", current: bytes | None = None):
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,3}", version) or not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise RuntimeError("Invalid release version or repository.")
    signed_path = update_root / "stable.json"
    manifest = verify_manifest(signed_path.read_bytes(), public_key)
    if manifest["version"] != version:
        raise RuntimeError("A signed manifest for this exact version is required.")
    if activate and current is not None:
        previous = verify_manifest(current, public_key)
        normalized = lambda v: tuple(map(int, (v.split('.') + ['0'] * 4)[:4]))
        if normalized(version) < normalized(previous["version"]):
            raise RuntimeError("Refusing to downgrade the active update pointer.")
    package = update_root / "RVC-Studio-Update.zip"
    for name, local in (("installer", installer), ("package", package)):
        artifact = manifest[name]
        if artifact["size"] != local.stat().st_size or artifact["sha256"].lower() != digest(local):
            raise RuntimeError(f"Signed {name} metadata does not match the local artifact.")
        expected_url = f"https://huggingface.co/{repository}/resolve/main/releases/v{version}/{local.name}?download=true"
        if artifact["url"] != expected_url:
            raise RuntimeError(f"Unexpected {name} URL in signed manifest.")
    prefix = f"releases/v{version}"
    for local in (installer, package, signed_path):
        remote_path = f"{prefix}/{local.name}"
        present = verify_remote(api, repository, remote_path, local, token)
        if activate and not present:
            raise RuntimeError(f"Cannot activate a missing release file: {remote_path}")
        if not present:
            api.upload_file(path_or_fileobj=local, path_in_repo=remote_path, repo_id=repository,
                            repo_type="model", token=token, commit_message=f"Publish RVC Studio v{version}: {local.name}")
            if not verify_remote(api, repository, remote_path, local, token):
                raise RuntimeError(f"Upload verification failed: {remote_path}")
    if activate:
        api.upload_file(path_or_fileobj=signed_path, path_in_repo="updates/stable.json", repo_id=repository,
                        repo_type="model", token=token, commit_message=f"Activate RVC Studio v{version}")
        if not verify_remote(api, repository, "updates/stable.json", signed_path, token):
            raise RuntimeError("Stable pointer verification failed.")
    return f"https://huggingface.co/{repository}/resolve/main/{quote(prefix, safe='/')}/{installer.name}?download=true"


def main():
    from huggingface_hub import HfApi, get_token, hf_hub_download
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--installer", required=True, type=Path)
    parser.add_argument("--update-directory", type=Path, default=Path("packaging/output/update"))
    parser.add_argument("--activate", action="store_true")
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args()
    token = os.environ.get("HF_TOKEN", "").strip() or get_token()
    if not token:
        raise RuntimeError("HF_TOKEN is required.")
    api = HfApi(token=token)
    api.create_repo(repo_id=args.repository, repo_type="model", private=False, exist_ok=True, token=token)
    current = None
    if args.activate and any(s.rfilename == "updates/stable.json" for s in api.model_info(args.repository, token=token).siblings):
        current = Path(hf_hub_download(args.repository, "updates/stable.json", token=token, force_download=True)).read_bytes()
    url = publish(api, args.repository, args.version, args.installer, args.update_directory, token, args.activate,
                  os.environ.get("RVC_UPDATE_PUBLIC_KEY_PEM", ""), current)
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8") as output:
            output.write(f"download_url={url}\nremote_size={args.installer.stat().st_size}\n")
    print(f"{'Activated' if args.activate else 'Published and verified'} RVC Studio v{args.version}")


if __name__ == "__main__":
    main()
