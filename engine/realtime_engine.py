"""RVC realtime audio engine without a GUI dependency.

The legacy ``realtime_gui.py`` remains the fallback implementation.  This module
owns all audio and CUDA objects for the Avalonia frontend, so no audio frames
ever need to cross the Python/.NET process boundary.
"""

from __future__ import annotations

import json
import logging
import os
import sys
import threading
import time
from dataclasses import asdict, dataclass, fields
from pathlib import Path
from typing import Any

os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
os.environ.setdefault("OMP_NUM_THREADS", "4")

import librosa
import numpy as np
import sounddevice as sd
import torch
import torch.nn.functional as F
import torchaudio.transforms as tat

from configs.config import Config
from infer import rtrvc as rvc_for_realtime
from tools.cuda_graph import cuda_graph_enabled, run_cuda_graph
from tools.torchgate import TorchGate


@dataclass
class RealtimeConfig:
    pth_path: str = ""
    index_path: str = ""
    pitch: int = 0
    formant: float = 0.0
    sr_type: str = "sr_model"
    block_time: float = 0.25
    threshold: int = -60
    crossfade_length: float = 0.05
    extra_time: float = 2.5
    input_noise_reduce: bool = False
    output_noise_reduce: bool = False
    rms_mix_rate: float = 0.0
    index_rate: float = 0.0
    f0method: str = "fcpe"
    hostapi: str = ""
    wasapi_exclusive: bool = False
    input_device_id: str = ""
    output_device_id: str = ""
    input_device_name: str = ""
    output_device_name: str = ""

    @classmethod
    def from_mapping(cls, value: dict[str, Any] | None) -> "RealtimeConfig":
        value = value or {}
        aliases = {
            "threhold": "threshold",
            "crossfade_time": "crossfade_length",
            "I_noise_reduce": "input_noise_reduce",
            "O_noise_reduce": "output_noise_reduce",
            "sg_hostapi": "hostapi",
            "sg_wasapi_exclusive": "wasapi_exclusive",
            "sg_input_device": "input_device_name",
            "sg_output_device": "output_device_name",
        }
        allowed = {item.name for item in fields(cls)}
        normalized: dict[str, Any] = {}
        for key, item in value.items():
            key = aliases.get(key, key)
            if key in allowed:
                normalized[key] = item
        config = cls(**normalized)
        if config.f0method not in {"pm", "rmvpe", "fcpe"}:
            config.f0method = "fcpe"
        if config.sr_type not in {"sr_model", "sr_device"}:
            config.sr_type = "sr_model"
        return config

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


class RealtimeEngine:
    """Single-owner audio and CUDA runtime for a single realtime stream."""

    _RESTART_FIELDS = {
        "pth_path",
        "index_path",
        "sr_type",
        "block_time",
        "crossfade_length",
        "extra_time",
        "hostapi",
        "wasapi_exclusive",
        "input_device_id",
        "output_device_id",
        "input_device_name",
        "output_device_name",
    }

    def __init__(self, logger: logging.Logger | None = None):
        self.log = logger or logging.getLogger("rvcstudio.engine")
        # The upstream Config class parses process-wide argv.  RVC Studio uses
        # its own --bootstrap argument, which must not leak into that parser.
        original_argv = sys.argv[:]
        try:
            sys.argv = [sys.argv[0]]
            self.config = Config()
        finally:
            sys.argv = original_argv
        self.gui_config = RealtimeConfig()
        self._lock = threading.RLock()
        self.stream: sd.Stream | None = None
        self.rvc: Any | None = None
        self.running = False
        self.restart_required = False
        self.last_error = ""
        self.last_audio_status = ""
        self.input_level = 0.0
        self.output_level = 0.0
        self.infer_ms = 0.0
        self.delay_ms = 0
        self.samplerate = 0
        self.channels = 0
        self._devices: list[dict[str, Any]] = []
        self._hostapis: list[dict[str, Any]] = []
        self.refresh_devices()

    def capabilities(self) -> dict[str, Any]:
        gpu_name = ""
        try:
            if torch.cuda.is_available():
                gpu_name = torch.cuda.get_device_name(0)
        except Exception as exc:  # pragma: no cover - driver-specific
            self.log.warning("Unable to inspect CUDA device: %s", exc)
        return {
            "cuda_available": bool(torch.cuda.is_available()),
            "cuda_version": torch.version.cuda or "",
            "torch_version": torch.__version__,
            "gpu_name": gpu_name,
            "fcpe_available": True,
            "cuda_graph_enabled": cuda_graph_enabled(self.config.device),
        }

    def refresh_devices(self) -> dict[str, Any]:
        with self._lock:
            if self.running:
                raise RuntimeError("请先停止实时变声，再刷新音频设备。")
            sd._terminate()
            sd._initialize()
            raw_devices = sd.query_devices()
            raw_hostapis = sd.query_hostapis()
            self._hostapis = [
                {"id": str(index), "name": item["name"]}
                for index, item in enumerate(raw_hostapis)
            ]
            hostapi_names = {index: item["name"] for index, item in enumerate(raw_hostapis)}
            self._devices = []
            for index, item in enumerate(raw_devices):
                hostapi_index = int(item["hostapi"])
                self._devices.append(
                    {
                        "id": str(index),
                        "name": item["name"],
                        "hostapi": hostapi_names[hostapi_index],
                        "hostapi_id": str(hostapi_index),
                        "max_input_channels": int(item["max_input_channels"]),
                        "max_output_channels": int(item["max_output_channels"]),
                        "default_samplerate": int(round(item["default_samplerate"])),
                    }
                )
            return self.device_payload()

    def device_payload(self) -> dict[str, Any]:
        default_input_name = self._default_device_name("input")
        default_output_name = self._default_device_name("output")
        return {
            "hostapis": self._hostapis,
            # PortAudio exposes the same Windows endpoint through MME,
            # DirectSound, WASAPI and sometimes WDM-KS.  Showing every one in
            # the desktop picker is confusing and makes it look as if devices
            # are duplicated.  Keep one endpoint per displayed device name,
            # preferring the lower-latency WASAPI variant for realtime use.
            "inputs": self._preferred_devices("max_input_channels", default_input_name),
            "outputs": self._preferred_devices("max_output_channels", default_output_name),
        }

    @staticmethod
    def _default_device_name(kind: str) -> str:
        try:
            return str(sd.query_devices(kind=kind)["name"])
        except Exception:
            return ""

    def _preferred_devices(
        self, channel_name: str, default_device_name: str = ""
    ) -> list[dict[str, Any]]:
        preference = {
            "Windows WASAPI": 0,
            "WDM-KS": 1,
            "ASIO": 2,
            "Windows DirectSound": 3,
            "MME": 4,
        }
        candidates = [item for item in self._devices if item[channel_name] > 0]
        # For a game microphone on current Windows, WASAPI is both the
        # lowest-latency general endpoint and the only host API that carries a
        # consistent endpoint name.  WDM-KS translates some device names, so
        # mixing it in would reintroduce apparent duplicates.  Keep ASIO too
        # when it is installed because it is intentionally a distinct route.
        realtime_candidates = [
            item for item in candidates if item["hostapi"] in {"Windows WASAPI", "ASIO"}
        ]
        if realtime_candidates:
            candidates = realtime_candidates
        selected: dict[str, dict[str, Any]] = {}
        for device in candidates:
            key = device["name"].casefold()
            previous = selected.get(key)
            if previous is None or preference.get(device["hostapi"], 99) < preference.get(previous["hostapi"], 99):
                selected[key] = device
        result = sorted(
            selected.values(),
            key=lambda item: (item["name"].casefold(), item["hostapi"].casefold()),
        )
        for item in result:
            item["is_default"] = bool(default_device_name) and (
                item["name"].casefold() == default_device_name.casefold()
            )
        return result

    def load_config_file(self, path: str | Path) -> RealtimeConfig:
        try:
            with open(path, "r", encoding="utf-8") as source:
                config = RealtimeConfig.from_mapping(json.load(source))
        except FileNotFoundError:
            config = RealtimeConfig()
        except Exception as exc:
            self.log.warning("Unable to load configuration: %s", exc)
            config = RealtimeConfig()
        self.update_config(config.to_dict())
        return self.gui_config

    def save_config_file(self, path: str | Path) -> None:
        target = Path(path)
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_suffix(target.suffix + ".tmp")
        with open(temporary, "w", encoding="utf-8") as output:
            json.dump(self.gui_config.to_dict(), output, ensure_ascii=False, indent=2)
        os.replace(temporary, target)

    def update_config(self, update: dict[str, Any]) -> dict[str, Any]:
        with self._lock:
            merged = self.gui_config.to_dict()
            merged.update(update or {})
            next_config = RealtimeConfig.from_mapping(merged)
            changed = {
                name
                for name, value in next_config.to_dict().items()
                if getattr(self.gui_config, name) != value
            }
            if self.running and changed & self._RESTART_FIELDS:
                self.restart_required = True
            self.gui_config = next_config
            if self.rvc is not None:
                if "pitch" in changed:
                    self.rvc.change_key(self.gui_config.pitch)
                if "formant" in changed:
                    self.rvc.change_formant(self.gui_config.formant)
                if "index_rate" in changed:
                    self.rvc.change_index_rate(self.gui_config.index_rate)
            return {
                "config": self.gui_config.to_dict(),
                "restart_required": self.restart_required,
            }

    def validate_model(self) -> None:
        if not self.gui_config.pth_path:
            raise ValueError("请先选择 .pth 音色模型。")
        if not Path(self.gui_config.pth_path).is_file():
            raise FileNotFoundError(f"找不到音色模型：{self.gui_config.pth_path}")
        if self.gui_config.index_path and not Path(self.gui_config.index_path).is_file():
            raise FileNotFoundError(f"找不到索引文件：{self.gui_config.index_path}")

    def _device_index(self, kind: str) -> int:
        config_id = (
            self.gui_config.input_device_id
            if kind == "input"
            else self.gui_config.output_device_id
        )
        config_name = (
            self.gui_config.input_device_name
            if kind == "input"
            else self.gui_config.output_device_name
        )
        channel_name = "max_input_channels" if kind == "input" else "max_output_channels"
        candidates = [item for item in self._devices if item[channel_name] > 0]
        if config_id:
            for item in candidates:
                if item["id"] == str(config_id) and (
                    not config_name or item["name"] == config_name
                ):
                    return int(item["id"])
        if config_name:
            for item in candidates:
                if item["name"] == config_name and (
                    not self.gui_config.hostapi or item["hostapi"] == self.gui_config.hostapi
                ):
                    return int(item["id"])
        if not candidates:
            raise RuntimeError(f"没有可用的{('输入' if kind == 'input' else '输出')}音频设备。")
        return int(candidates[0]["id"])

    def _set_devices(self) -> tuple[int, int]:
        input_index = self._device_index("input")
        output_index = self._device_index("output")
        self.gui_config.input_device_id = str(input_index)
        self.gui_config.output_device_id = str(output_index)
        self.gui_config.input_device_name = sd.query_devices(input_index)["name"]
        self.gui_config.output_device_name = sd.query_devices(output_index)["name"]
        return input_index, output_index

    def start(self) -> dict[str, Any]:
        with self._lock:
            if self.running:
                return self.status()
            self.validate_model()
            self.last_error = ""
            self.restart_required = False
            try:
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
                input_index, output_index = self._set_devices()
                self.rvc = rvc_for_realtime.RVC(
                    self.gui_config.pitch,
                    self.gui_config.formant,
                    self.gui_config.pth_path,
                    self.gui_config.index_path,
                    self.gui_config.index_rate,
                    self.config,
                    self.rvc,
                )
                self.samplerate = (
                    self.rvc.tgt_sr
                    if self.gui_config.sr_type == "sr_model"
                    else int(sd.query_devices(input_index)["default_samplerate"])
                )
                input_channels = int(sd.query_devices(input_index)["max_input_channels"])
                output_channels = int(sd.query_devices(output_index)["max_output_channels"])
                self.channels = min(input_channels, output_channels, 2)
                if self.channels < 1:
                    raise RuntimeError("所选设备没有共同可用的音频声道。")
                self._prepare_buffers()
                self._start_stream(input_index, output_index)
                self.running = True
                self.log.info("Realtime stream started: %s -> %s", self.gui_config.input_device_name, self.gui_config.output_device_name)
                return self.status()
            except Exception as exc:
                self.last_error = str(exc)
                self.log.exception("Unable to start realtime stream")
                self._stop_stream_unlocked()
                raise

    def stop(self) -> dict[str, Any]:
        with self._lock:
            self._stop_stream_unlocked()
            return self.status()

    def close(self) -> None:
        with self._lock:
            self._stop_stream_unlocked()
            self.rvc = None
            if torch.cuda.is_available():
                torch.cuda.empty_cache()

    def _stop_stream_unlocked(self) -> None:
        if self.stream is not None:
            try:
                self.stream.abort()
                self.stream.close()
            finally:
                self.stream = None
        self.running = False
        self.restart_required = False

    def _prepare_buffers(self) -> None:
        self.zc = self.samplerate // 100
        self.block_frame = int(round(self.gui_config.block_time * self.samplerate / self.zc)) * self.zc
        self.block_frame_16k = 160 * self.block_frame // self.zc
        self.crossfade_frame = int(round(self.gui_config.crossfade_length * self.samplerate / self.zc)) * self.zc
        self.sola_buffer_frame = min(self.crossfade_frame, 4 * self.zc)
        self.sola_search_frame = self.zc
        self.extra_frame = int(round(self.gui_config.extra_time * self.samplerate / self.zc)) * self.zc
        device = self.config.device
        self.input_wav = torch.zeros(
            self.extra_frame + self.crossfade_frame + self.sola_search_frame + self.block_frame,
            device=device,
            dtype=torch.float32,
        )
        self.input_wav_denoise = self.input_wav.clone()
        self.input_wav_res = torch.zeros(160 * self.input_wav.shape[0] // self.zc, device=device, dtype=torch.float32)
        self.rms_buffer = np.zeros(4 * self.zc, dtype="float32")
        self.sola_buffer = torch.zeros(self.sola_buffer_frame, device=device, dtype=torch.float32)
        self.sola_den_kernel = torch.ones(1, 1, self.sola_buffer_frame, device=device, dtype=torch.float32)
        self.nr_buffer = self.sola_buffer.clone()
        self.output_buffer = self.input_wav.clone()
        self.skip_head = self.extra_frame // self.zc
        self.return_length = (self.block_frame + self.sola_buffer_frame + self.sola_search_frame) // self.zc
        self.fade_in_window = torch.sin(
            0.5 * np.pi * torch.linspace(0.0, 1.0, steps=self.sola_buffer_frame, device=device, dtype=torch.float32)
        ) ** 2
        self.fade_out_window = 1 - self.fade_in_window
        self.resampler = tat.Resample(orig_freq=self.samplerate, new_freq=16000, dtype=torch.float32).to(device)
        self.resampler2 = (
            tat.Resample(orig_freq=self.rvc.tgt_sr, new_freq=self.samplerate, dtype=torch.float32).to(device)
            if self.rvc.tgt_sr != self.samplerate
            else None
        )
        self.tg = TorchGate(sr=self.samplerate, n_fft=4 * self.zc, prop_decrease=0.9).to(device)
        self._prewarm_cuda_graph()

    def _prewarm_cuda_graph(self) -> None:
        if not cuda_graph_enabled(self.config.device):
            return
        try:
            samples = self.input_wav_res.shape[0]
            phase = torch.arange(samples, device=self.config.device, dtype=torch.float32)
            probe = 0.05 * torch.sin(2 * np.pi * 220.0 * phase / 16000.0)
            self.input_wav_res.copy_(probe)
            if self.gui_config.input_noise_reduce:
                short = self.input_wav[-self.sola_buffer_frame - self.block_frame :].unsqueeze(0)
                self.tg(short, self.input_wav.unsqueeze(0))
            resample_input = self.input_wav[-self.block_frame - 2 * self.zc :]
            run_cuda_graph(self.resampler, "realtime-input-resample", lambda audio: self.resampler(audio), resample_input)
            inferred = self.rvc.infer(self.input_wav_res, self.block_frame_16k, self.skip_head, self.return_length, self.gui_config.f0method)
            if self.resampler2 is not None:
                inferred = run_cuda_graph(self.resampler2, "realtime-output-resample", lambda audio: self.resampler2(audio), inferred)
            if self.gui_config.output_noise_reduce:
                self.tg(inferred.unsqueeze(0), self.output_buffer.unsqueeze(0))
            torch.cuda.synchronize(self.config.device)
        except Exception:
            self.log.exception("CUDA Graph prewarm failed; continuing with eager execution")
        finally:
            self.input_wav.zero_()
            self.input_wav_denoise.zero_()
            self.input_wav_res.zero_()
            self.output_buffer.zero_()
            self.sola_buffer.zero_()
            self.nr_buffer.zero_()
            if hasattr(self.rvc, "cache_pitch"):
                self.rvc.cache_pitch.zero_()
            if hasattr(self.rvc, "cache_pitchf"):
                self.rvc.cache_pitchf.zero_()

    def _start_stream(self, input_index: int, output_index: int) -> None:
        extra_settings = (
            sd.WasapiSettings(exclusive=True)
            if "WASAPI" in self.gui_config.hostapi and self.gui_config.wasapi_exclusive
            else None
        )
        self.stream = sd.Stream(
            device=(input_index, output_index),
            callback=self._audio_callback,
            blocksize=self.block_frame,
            samplerate=self.samplerate,
            channels=self.channels,
            dtype="float32",
            extra_settings=extra_settings,
        )
        self.stream.start()
        latency = self.stream.latency
        output_latency = latency[-1] if isinstance(latency, (tuple, list)) else latency
        self.delay_ms = int(round((output_latency + self.gui_config.block_time + self.gui_config.crossfade_length + 0.01) * 1000))
        if self.gui_config.input_noise_reduce:
            self.delay_ms += int(round(min(self.gui_config.crossfade_length, 0.04) * 1000))

    def _audio_callback(self, indata: np.ndarray, outdata: np.ndarray, frames: int, _time_info: Any, status: Any) -> None:
        started = time.perf_counter()
        try:
            if status:
                self.last_audio_status = str(status)
            mono_input = librosa.to_mono(indata.T)
            self.input_level = float(np.sqrt(np.mean(np.square(mono_input)))) if mono_input.size else 0.0
            if self.gui_config.threshold > -60:
                gated = np.append(self.rms_buffer, mono_input)
                rms = librosa.feature.rms(y=gated, frame_length=4 * self.zc, hop_length=self.zc)[:, 2:]
                self.rms_buffer[:] = gated[-4 * self.zc :]
                mono_input = gated[2 * self.zc - self.zc // 2 :]
                muted = librosa.amplitude_to_db(rms, ref=1.0)[0] < self.gui_config.threshold
                for index, should_mute in enumerate(muted):
                    if should_mute:
                        mono_input[index * self.zc : (index + 1) * self.zc] = 0
                mono_input = mono_input[self.zc // 2 :]
            self.input_wav[: -self.block_frame] = self.input_wav[self.block_frame :].clone()
            self.input_wav[-mono_input.shape[0] :] = torch.from_numpy(mono_input).to(self.config.device)
            self.input_wav_res[: -self.block_frame_16k] = self.input_wav_res[self.block_frame_16k :].clone()
            if self.gui_config.input_noise_reduce:
                self._apply_input_noise_reduction()
            else:
                resample_input = self.input_wav[-mono_input.shape[0] - 2 * self.zc :]
                self.input_wav_res[-160 * (mono_input.shape[0] // self.zc + 1) :] = run_cuda_graph(
                    self.resampler, "realtime-input-resample", lambda audio: self.resampler(audio), resample_input
                )[160:]
            infer_wav = self.rvc.infer(
                self.input_wav_res,
                self.block_frame_16k,
                self.skip_head,
                self.return_length,
                self.gui_config.f0method,
            )
            if self.resampler2 is not None:
                infer_wav = run_cuda_graph(
                    self.resampler2, "realtime-output-resample", lambda audio: self.resampler2(audio), infer_wav
                )
            if self.gui_config.output_noise_reduce:
                self.output_buffer[: -self.block_frame] = self.output_buffer[self.block_frame :].clone()
                self.output_buffer[-self.block_frame :] = infer_wav[-self.block_frame :]
                infer_wav = self.tg(infer_wav.unsqueeze(0), self.output_buffer.unsqueeze(0)).squeeze(0)
            self._apply_rms_mix(infer_wav)
            output = self._apply_sola(infer_wav)
            outdata[:] = output.repeat(self.channels, 1).t().cpu().numpy()
            self.output_level = float(np.sqrt(np.mean(np.square(outdata)))) if outdata.size else 0.0
            self.infer_ms = round((time.perf_counter() - started) * 1000, 1)
        except Exception as exc:  # sounddevice callback must always return audio
            outdata.fill(0)
            self.last_error = str(exc)
            self.log.exception("Realtime audio callback failed")

    def _apply_input_noise_reduction(self) -> None:
        self.input_wav_denoise[: -self.block_frame] = self.input_wav_denoise[self.block_frame :].clone()
        input_wav = self.input_wav[-self.sola_buffer_frame - self.block_frame :]
        input_wav = self.tg(input_wav.unsqueeze(0), self.input_wav.unsqueeze(0)).squeeze(0)
        input_wav[: self.sola_buffer_frame] *= self.fade_in_window
        input_wav[: self.sola_buffer_frame] += self.nr_buffer * self.fade_out_window
        self.input_wav_denoise[-self.block_frame :] = input_wav[: self.block_frame]
        self.nr_buffer[:] = input_wav[self.block_frame :]
        resample_input = self.input_wav_denoise[-self.block_frame - 2 * self.zc :]
        self.input_wav_res[-self.block_frame_16k - 160 :] = run_cuda_graph(
            self.resampler, "realtime-input-resample", lambda audio: self.resampler(audio), resample_input
        )[160:]

    def _apply_rms_mix(self, infer_wav: torch.Tensor) -> None:
        if self.gui_config.rms_mix_rate >= 1:
            return
        input_wav = (
            self.input_wav_denoise[self.extra_frame :]
            if self.gui_config.input_noise_reduce
            else self.input_wav[self.extra_frame :]
        )
        rms1 = librosa.feature.rms(y=input_wav[: infer_wav.shape[0]].cpu().numpy(), frame_length=4 * self.zc, hop_length=self.zc)
        rms1 = torch.from_numpy(rms1).to(self.config.device)
        rms1 = F.interpolate(rms1.unsqueeze(0), size=infer_wav.shape[0] + 1, mode="linear", align_corners=True)[0, 0, :-1]
        rms2 = librosa.feature.rms(y=infer_wav.cpu().numpy(), frame_length=4 * self.zc, hop_length=self.zc)
        rms2 = torch.from_numpy(rms2).to(self.config.device)
        rms2 = F.interpolate(rms2.unsqueeze(0), size=infer_wav.shape[0] + 1, mode="linear", align_corners=True)[0, 0, :-1]
        rms2 = torch.maximum(rms2, torch.zeros_like(rms2) + 1e-3)
        infer_wav *= torch.pow(rms1 / rms2, 1.0 - self.gui_config.rms_mix_rate)

    def _apply_sola(self, infer_wav: torch.Tensor) -> torch.Tensor:
        conv_input = infer_wav[None, None, : self.sola_buffer_frame + self.sola_search_frame]
        cor_nom = F.conv1d(conv_input, self.sola_buffer[None, None, :])
        cor_den = torch.sqrt(F.conv1d(conv_input**2, self.sola_den_kernel) + 1e-8)
        offset = int(torch.argmax(cor_nom[0, 0] / cor_den[0, 0]).item())
        infer_wav = infer_wav[offset:]
        infer_wav[: self.sola_buffer_frame] *= self.fade_in_window
        infer_wav[: self.sola_buffer_frame] += self.sola_buffer * self.fade_out_window
        self.sola_buffer[:] = infer_wav[self.block_frame : self.block_frame + self.sola_buffer_frame]
        return infer_wav[: self.block_frame]

    def status(self) -> dict[str, Any]:
        return {
            "running": self.running,
            "restart_required": self.restart_required,
            "model_loaded": bool(self.rvc is not None),
            "model_path": self.gui_config.pth_path,
            "input_level": round(self.input_level, 5),
            "output_level": round(self.output_level, 5),
            "infer_ms": self.infer_ms,
            "delay_ms": self.delay_ms,
            "samplerate": self.samplerate,
            "channels": self.channels,
            "last_error": self.last_error,
            "audio_status": self.last_audio_status,
            "config": self.gui_config.to_dict(),
        }
