"""Background entry point launched by RVC Studio.exe via pythonw.exe."""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
os.chdir(ROOT)
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from engine.control_server import ControlServer
from engine.realtime_engine import RealtimeEngine


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="RVC Studio background engine")
    parser.add_argument("--bootstrap", required=True, help="Path to a one-time local startup configuration JSON file")
    return parser.parse_args()


def configure_logging(log_dir: Path) -> logging.Logger:
    log_dir.mkdir(parents=True, exist_ok=True)
    log_file = log_dir / "rvc-studio-engine.log"
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
        handlers=[logging.FileHandler(log_file, encoding="utf-8")],
    )
    return logging.getLogger("rvcstudio")


def migrate_bundled_path(value: str, relative_directory: str) -> str:
    """Keep bundled model selections valid after moving or installing the app."""
    if not value:
        return value
    configured = Path(value)
    if configured.is_file():
        return value
    bundled = ROOT / relative_directory / configured.name
    if bundled.is_file():
        return bundled.relative_to(ROOT).as_posix()
    return value


def main() -> int:
    args = parse_args()
    bootstrap_path = Path(args.bootstrap).resolve()
    with open(bootstrap_path, "r", encoding="utf-8") as source:
        bootstrap = json.load(source)
    log = configure_logging(Path(bootstrap["log_dir"]))
    log.info("Starting RVC Studio engine")
    engine = RealtimeEngine(log.getChild("engine"))
    legacy_config = ROOT / "configs" / "config.json"
    app_config = Path(bootstrap["app_config"])
    first_run = not app_config.exists()
    engine.load_config_file(app_config if app_config.exists() else legacy_config)
    migrated_paths = {
        "pth_path": migrate_bundled_path(engine.gui_config.pth_path, "assets/weights"),
        "index_path": migrate_bundled_path(engine.gui_config.index_path, "assets/indices"),
    }
    paths_changed = any(
        migrated_paths[name] != getattr(engine.gui_config, name)
        for name in migrated_paths
    )
    if paths_changed:
        engine.update_config(migrated_paths)
    # RVC Studio is built for the requested realtime FCPE workflow.  Preserve
    # the legacy GUI configuration untouched, but use FCPE for this new app's
    # first-run profile; later user choices live in app_config.
    if first_run:
        engine.update_config({"f0method": "fcpe"})
    if first_run or paths_changed:
        engine.save_config_file(app_config)
    server = ControlServer(engine, bootstrap["token"], app_config, log.getChild("control"))
    try:
        asyncio.run(server.run("127.0.0.1", int(bootstrap["port"])))
        return 0
    except Exception:
        log.exception("RVC Studio engine stopped unexpectedly")
        return 1
    finally:
        engine.close()
        try:
            bootstrap_path.unlink(missing_ok=True)
        except OSError:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
