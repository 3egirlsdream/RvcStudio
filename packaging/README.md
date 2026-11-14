# RVC Studio NVIDIA release packaging

This directory builds a Windows 10/11 x64, NVIDIA RTX 20–50 realtime-only
release. The source RVC runtime is never modified; a filtered copy is assembled
under `output/stage`.

Requires PowerShell 7 (`pwsh`), the SDK specified by `global.json`, and Inno Setup 6.

## Over-install cleanup

Complete installers reconcile `release-manifest.json` with the incoming package.
Before copying, an embedded copy of the new single-file app validates both file
lists and snapshots retired files. It also merges the existing small-update
baseline, so code introduced by a small update is eligible for later retirement.
The incoming full inventory is deliberately excluded from normal file copying.

After new files have copied, and before Python verification/launch, the helper
checks the new managed files, removes obsolete files that still match their old
size/SHA-256, prunes empty parent directories without recursive deletion, and
atomically commits the new inventory. Cleanup covers runtime dependencies,
engine/code resources, notices and base feature models. It never sweeps whole
directories. Imported voice models/indices/sidecars, personal configuration,
unlisted files and locally modified obsolete files are preserved. Preserved
modifications are recorded in `{app}\installer-cleanup.log` and may need review
if a customized runtime conflicts with the new one.

An absent previous full inventory permits only cleanup known from the small
baseline; completely untracked legacy files are retained. Invalid inventories,
unsafe paths and directory junctions stop the operation. Locked files or an
interruption leave the old inventory in place; close other instances and rerun
the full installer to retry. Already removed files are harmless on retry.
This is retryable inventory reconciliation, not a rollback of a whole full
installation. Cancelling before file copying does not remove installed files.
Full installers require a manifest: `-SkipManifest` is permitted only together
with `-SkipInstaller`.

The build runs `Test-InstallerCleanup.ps1 -Executable <published-single-file-exe>`
against an isolated temporary installation before assembling the release. Unit
tests cover protected/modified files, small-update inventory merging, file locks,
interruption/retry, read-only files, invalid paths and NTFS junctions.

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

The version is embedded in the app and installer. Local builds only create
files by default; they never upload or advance a server version. The legacy
version-only operation is available explicitly through `-PublishVersion`;
normal production publishing must use the ordered CI workflow below.

Use `-PreflightOnly` to check required files, .NET, Inno Setup and the bundled
VB-CABLE signature without changing build output. `-SkipInstaller` creates and
tests only the staged portable release and does not publish a server version.

The build publishes the self-contained Avalonia app, copies the realtime engine
and bundled voice models, removes invalid/unused CUDA 11 and ONNX remnants,
verifies CUDA architectures and the default model, writes package/license and
SHA-256 manifests, then compiles the Inno Setup installer.

Final files are written to `output/installer`. Inno Setup emits one complete
offline `RVC-Studio-NVIDIA-Setup.exe`; no external BIN payloads are required.
Because the full installer is larger than GitHub's 2 GiB per-asset limit,
GitHub Actions uploads the EXE to the public Hugging Face repository configured
by the `HF_RELEASE_REPOSITORY` repository variable. The GitHub Release contains
the generated README, SHA-256 list, and a direct link to that EXE. Configure a
write-capable Hugging Face token as the `HF_TOKEN` GitHub Actions secret.

## Signed small updates (1.1.0+)

Clients before 1.1.0 need one manual full installation to acquire the updater
and `update-baseline.json`. Later compatible releases can skip versions and
download only `output/update/RVC-Studio-Update.zip`. The ZIP contains the
self-contained .NET/Avalonia EXE, engine/config Python code and notices, but no
Python/CUDA runtime, models, indices or user settings. Sizes are measured by the
build and displayed to the user; no fixed size is promised.

The SHA-256 component ID covers non-program inputs including the runtime and
bundled models. It excludes timestamps, bytecode caches, generated manifests
and the mutable `configs/config.json`. An absent, damaged or different baseline
routes the client to the full installer. The five architecture JSON files in
`configs/v1` and `configs/v2` are explicitly managed program resources and travel
with the code. Other user JSON, imported models and model parameters are never
small-update targets. Full installs preserve existing voice models, indices and
user configuration; managed architecture JSON and base feature models may change.

Configure these GitHub settings before the first production build:

| Setting | Kind | Value |
| --- | --- | --- |
| `HF_RELEASE_REPOSITORY` | Variable | Public Hugging Face `owner/repository` |
| `HF_TOKEN` | Secret | Hugging Face write token |
| `RVC_UPDATE_PUBLIC_KEY_PEM` | Variable | RSA SubjectPublicKeyInfo PEM, at least 2048 bits |
| `RVC_UPDATE_PRIVATE_KEY_PEM` | Secret | Matching RSA PKCS#8 PEM private key |

Keep the private key outside the repository and retain the same key across
releases. Key rotation currently requires a new full installer. Generate a key
pair using your normal secret-management tooling; never paste private keys into
build arguments or logs. Local signing may use the same environment variables.
The public key and the stable URL are embedded at publish time using the
`UpdateTrustFile` MSBuild property. A normal development build has no trusted
update source and cannot install unsigned updates. Tests override only the URL
through `RVC_STUDIO_UPDATE_MANIFEST_URL`; this does not bypass the embedded key.
The existing API URL overrides continue to apply to account/legacy services.

`Build-Update.ps1 -Phase Prepare` hashes staged components and creates the
baseline/ZIP before Inno compilation. `-Phase Finalize` binds both artifact
hashes, actual sizes and URLs into an RSA-SHA256 signed envelope. Without a
private key it removes any stale signed pointer and leaves unsigned local
artifacts only. CI requires both keys and verifies that they match.

CI tests first, builds, uploads and verifies immutable
`releases/vVERSION/{RVC-Studio-NVIDIA-Setup.exe,RVC-Studio-Update.zip,stable.json}`,
publishes the GitHub Release, then activates `updates/stable.json` as a single
file and finally advances the old `RvcStudio` API. Failed uploads or GitHub
publication never activate the pointer. Published versions cannot be overwritten
with different bytes; retries must reuse the original artifacts or increment
the version. Activation rejects downgrades. Legacy clients still receive version
notices through the old API.

Updates are user-initiated, downloaded under
`%LocalAppData%\RvcStudio\Updates`, and rechecked in an administrator-only staging
directory after UAC. A temporary copy of the current single-file executable
coordinates shutdown and relaunch without keeping the installed EXE open or
launching the new app as administrator. File replacement uses atomic renames;
the durable `.rvc-update` journal and verified backups permit rollback after
write/self-test failures or interruption. Startup shows a recovery window before
initializing the engine when a journal remains. Logs are in
`%LocalAppData%\RvcStudio\Updates\update.log`; audio streams are never opened
by the post-install self-test. UAC must run as the same Windows account as the
original session. Close any other session using the installation before updating.

Run verification without publishing:

```powershell
dotnet test studio/RvcStudio.App.Tests/RvcStudio.App.Tests.csproj
pwsh -File packaging/Test-UpdatePackaging.ps1
python -m unittest discover -s packaging -p test_update_publishing.py -v
```

Publishing tests require `cryptography`; production publishing also requires
`huggingface_hub`. Before shipping, exercise two signed versions with unchanged
components on Windows: download/cancel/retry, UAC accept/cancel, active voice
conversion shutdown, two instances/file locks, normal-user relaunch, full-package
fallback, forced self-test failure and process interruption with recovery.

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
