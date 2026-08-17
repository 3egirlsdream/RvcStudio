# RVC Studio NVIDIA release packaging

This directory builds a Windows 10/11 x64, NVIDIA RTX 20–50 realtime-only
release. The source RVC runtime is never modified; a filtered copy is assembled
under `output/stage`.

## Build

For a normal rebuild after changing code, double-click:

```text
..\Build-Package.cmd
```

This root-level file is the single user-facing entry point. It calls the
PowerShell build logic, keeps the window open on success or failure, and writes
the finished package to `packaging\output\installer`. When `-Version` is not
specified, the build automatically reads the `<Version>` value from
`studio\RvcStudio.App\RvcStudio.App.csproj`.

For a versioned build from Command Prompt:

```bat
Build-Package.cmd -Version 1.2.0 -ReleaseNotes "本次更新说明"
```

PowerShell and CI can call the same build logic directly:

```powershell
.\Build-Release.ps1 -Version 1.2.0
```

GitHub Actions restores the exact Python/CUDA runtime and model inputs from the
`build-dependencies-v1` dependency release before calling this script. Hosted
runners have no NVIDIA device, so CI passes `-AllowNoCudaDevice`; the packaged
PyTorch architecture list and model loading are still verified. Installed
clients continue to run the CUDA hardware check.

The version is embedded in the app and installer. After the installer has been
built and hashed successfully, the script checks the `RvcStudio` channel on the
existing update service. It creates the channel on the first release, or updates
it only when the package version is newer than the server version. Use
`-SkipVersionPublish` for a local/test package that must not publish a version.

Use `-PreflightOnly` to check required files, .NET, Inno Setup and the bundled
VB-CABLE signature without changing build output. `-SkipInstaller` creates and
tests only the staged portable release and does not publish a server version.

The build publishes the self-contained Avalonia app, copies the realtime engine
and bundled voice models, removes invalid/unused CUDA 11 and ONNX remnants,
verifies CUDA architectures and the default model, writes package/license and
SHA-256 manifests, then compiles the Inno Setup installer.

Final files are written to `output/installer`. GitHub limits each Release asset
to 2 GiB, so Inno Setup emits `RVC-Studio-NVIDIA-Setup.exe` plus one or more
`RVC-Studio-NVIDIA-Setup-*.bin` payload parts. Users must download every setup
part into one directory. The generated README and SHA-256 list are optional
companion documents.

## VB-CABLE

`vendor/vb-cable/VBCABLE_Driver_Pack45.zip` is the unmodified standard
VB-CABLE package downloaded from VB-Audio. Its SHA-256 at the time of assembly
is `B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB`.

The installer offers VB-CABLE as a visible, default-selected Donationware task,
runs the signed x64 driver setup with `-i -h`, and schedules RVC Studio to launch
after the required reboot. The Windows driver consent dialog cannot be hidden.

Before commercial or organizational distribution, review the current terms at
https://vb-audio.com/Services/licensing.htm and obtain volume licensing or
written confirmation when applicable.

The Simplified Chinese installer messages file is vendored from the official
Inno Setup `is-6_7_3` source tag under `vendor/inno`, with the Inno Setup
license retained beside it.
