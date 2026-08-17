"""Cold-start verification for an assembled RVC Studio NVIDIA release."""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--require-cuda", action="store_true")
    return parser.parse_args()


def require_file(path: Path) -> None:
    if not path.is_file():
        raise FileNotFoundError(f"Required release file is missing: {path}")


def main() -> int:
    args = parse_args()
    root = args.root.resolve()
    os.chdir(root)
    sys.path.insert(0, str(root))

    required = [
        root / "RVC Studio.exe",
        root / "runtime" / "pythonw.exe",
        root / "realtime_service.py",
        root / "assets" / "hubert_base" / "pytorch_model.bin",
        root / "assets" / "rmvpe" / "rmvpe.pt",
        root / "assets" / "weights" / "kikiV1.pth",
        root / "assets" / "indices" / "kikiV1.index",
    ]
    for path in required:
        require_file(path)

    import torch

    architectures = torch.cuda.get_arch_list()
    required_architectures = {"sm_75", "sm_86", "sm_120"}
    if not required_architectures.issubset(architectures):
        missing = sorted(required_architectures.difference(architectures))
        raise RuntimeError(f"PyTorch CUDA package is missing RTX architectures: {missing}")
    if args.require_cuda and not torch.cuda.is_available():
        raise RuntimeError(
            "A compatible NVIDIA CUDA GPU/driver was not detected. "
            "RVC Studio NVIDIA requires an RTX GPU and current NVIDIA driver."
        )

    from engine.realtime_engine import RealtimeEngine

    engine = RealtimeEngine()
    try:
        capabilities = engine.capabilities()
        devices = engine.device_payload()
    finally:
        engine.close()

    # Loading the checkpoint and FAISS index catches missing native DLLs and
    # damaged model files without opening a live microphone stream.
    from configs.config import Config
    from infer.rtrvc import RVC

    original_argv = sys.argv[:]
    try:
        sys.argv = [sys.argv[0]]
        config = Config()
    finally:
        sys.argv = original_argv
    model_path = root / "assets" / "weights" / "kikiV1.pth"
    index_path = root / "assets" / "indices" / "kikiV1.index"
    model = RVC(12, 0.0, str(model_path), str(index_path), 0.0, config, None)

    result = {
        "ok": True,
        "root": str(root),
        "torch": torch.__version__,
        "cuda_runtime": torch.version.cuda,
        "cuda_available": bool(torch.cuda.is_available()),
        "gpu": capabilities["gpu_name"],
        "cuda_graph": capabilities["cuda_graph_enabled"],
        "architectures": architectures,
        "input_devices": len(devices["inputs"]),
        "output_devices": len(devices["outputs"]),
        "model_sample_rate": model.tgt_sr,
    }
    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
