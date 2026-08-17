"""Authenticated, loopback-only JSON Lines control server for RVC Studio."""

from __future__ import annotations

import asyncio
import json
import logging
from pathlib import Path
from typing import Any

from .realtime_engine import RealtimeEngine


class ControlServer:
    def __init__(self, engine: RealtimeEngine, token: str, config_path: Path, logger: logging.Logger):
        self.engine = engine
        self.token = token
        self.config_path = config_path
        self.log = logger
        self.shutdown_requested = asyncio.Event()

    async def handle_client(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        peer = writer.get_extra_info("peername")
        try:
            while not reader.at_eof():
                raw = await reader.readline()
                if not raw:
                    break
                try:
                    request = json.loads(raw.decode("utf-8"))
                    response = await self.dispatch(request)
                except Exception as exc:
                    self.log.exception("Control request failed")
                    response = {"ok": False, "error": str(exc)}
                writer.write((json.dumps(response, ensure_ascii=False) + "\n").encode("utf-8"))
                await writer.drain()
                if self.shutdown_requested.is_set():
                    break
        except ConnectionError:
            self.log.info("Control connection closed: %s", peer)
        finally:
            writer.close()
            await writer.wait_closed()

    async def dispatch(self, request: dict[str, Any]) -> dict[str, Any]:
        if request.get("token") != self.token:
            raise PermissionError("无效的本机控制令牌。")
        command = request.get("command")
        payload = request.get("payload") or {}
        if command == "hello":
            result = {"service": "rvc-studio-engine", "capabilities": self.engine.capabilities()}
        elif command == "get_capabilities":
            result = self.engine.capabilities()
        elif command == "get_devices":
            result = self.engine.device_payload()
        elif command == "refresh_devices":
            result = self.engine.refresh_devices()
        elif command == "get_status":
            result = self.engine.status()
        elif command == "update_config":
            result = self.engine.update_config(payload)
            self.engine.save_config_file(self.config_path)
        elif command == "load_model":
            result = self.engine.update_config(payload)
            self.engine.validate_model()
            self.engine.save_config_file(self.config_path)
        elif command == "start":
            result = self.engine.start()
            self.engine.save_config_file(self.config_path)
        elif command == "stop":
            result = self.engine.stop()
        elif command == "shutdown":
            self.engine.close()
            self.shutdown_requested.set()
            result = {"shutting_down": True}
        else:
            raise ValueError(f"未知命令：{command}")
        return {"ok": True, "result": result}

    async def run(self, host: str, port: int) -> None:
        server = await asyncio.start_server(self.handle_client, host=host, port=port)
        sockets = server.sockets or []
        if not sockets:
            raise RuntimeError("无法绑定本机控制端口。")
        self.log.info("Control server listening on %s", sockets[0].getsockname())
        async with server:
            await self.shutdown_requested.wait()
