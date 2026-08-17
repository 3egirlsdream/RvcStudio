#!/usr/bin/env python3
"""Publish and verify one complete offline installer on Hugging Face Hub."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
from urllib.parse import quote

from huggingface_hub import HfApi, get_token


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True, help="Hugging Face repo id, for example user/RvcStudio")
    parser.add_argument("--version", required=True)
    parser.add_argument("--installer", required=True, type=Path)
    parser.add_argument("--github-output", type=Path)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    token = os.environ.get("HF_TOKEN", "").strip() or get_token()
    if not token:
        raise RuntimeError(
            "A Hugging Face token is required. Set HF_TOKEN or run `hf auth login`."
        )
    if not args.installer.is_file():
        raise FileNotFoundError(args.installer)
    if args.installer.name != "RVC-Studio-NVIDIA-Setup.exe":
        raise RuntimeError(f"Unexpected installer name: {args.installer.name}")

    remote_path = f"releases/v{args.version}/{args.installer.name}"
    local_size = args.installer.stat().st_size
    api = HfApi(token=token)
    api.create_repo(
        repo_id=args.repository,
        repo_type="model",
        private=False,
        exist_ok=True,
        token=token,
    )
    api.upload_file(
        path_or_fileobj=args.installer,
        path_in_repo=remote_path,
        repo_id=args.repository,
        repo_type="model",
        token=token,
        commit_message=f"Publish RVC Studio v{args.version} offline installer",
    )

    info = api.model_info(args.repository, files_metadata=True, token=token)
    remote_file = next(
        (sibling for sibling in info.siblings if sibling.rfilename == remote_path),
        None,
    )
    if remote_file is None:
        raise RuntimeError(f"Uploaded file was not found in Hugging Face metadata: {remote_path}")
    if remote_file.size != local_size:
        raise RuntimeError(
            f"Hugging Face size mismatch for {remote_path}: local={local_size}, remote={remote_file.size}"
        )

    encoded_path = quote(remote_path, safe="/")
    download_url = (
        f"https://huggingface.co/{args.repository}/resolve/main/{encoded_path}?download=true"
    )
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8") as output:
            output.write(f"download_url={download_url}\n")
            output.write(f"remote_size={remote_file.size}\n")
    print(f"Published and verified {download_url} ({remote_file.size} bytes)")


if __name__ == "__main__":
    main()
