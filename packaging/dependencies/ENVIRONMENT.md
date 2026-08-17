# Build environment

- Host OS: Windows 10/11 x64; GitHub Actions uses `windows-2025`
- Client SDK: .NET SDK 10.0.303 (latest patch roll-forward)
- Desktop target: `net10.0`, `win-x64`, self-contained
- Python runtime: CPython 3.12.10 x64
- Installer compiler: Inno Setup 6.5 or newer
- Archive tool: 7-Zip
- GPU runtime: CUDA 12.8-capable PyTorch package with `sm_75`, `sm_86`, and
  `sm_120` architectures

The dependency archive is the authoritative binary environment. The
`requirments_cu128_py312.txt` file documents how to rebuild a compatible Python
environment from package indexes; it is not substituted for the checksummed
runtime during automated releases.
