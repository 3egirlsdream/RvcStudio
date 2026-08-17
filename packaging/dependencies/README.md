# Exact build dependencies

The source repository intentionally does not put the 6+ GiB Python/CUDA
runtime and model files into Git history. Their exact, checksummed archive is
stored as split assets in the GitHub Release tagged `build-dependencies-v1`.

`Restore-BuildDependencies.ps1` downloads every archive part, validates it
against `SHA256SUMS.txt`, and extracts these build inputs at the repository root:

- `runtime/` — Python 3.12.10 and the tested CUDA/PyTorch package set
- `assets/hubert_base/`
- `assets/weights/`
- `assets/indices/`
- `assets/rmvpe/rmvpe.pt`

To recreate the dependency release from a verified local package stage, run:

```powershell
.\packaging\Create-BuildDependencies.ps1
```

Upload every generated `.7z.00N` file to the same dependency Release, commit
the regenerated `SHA256SUMS.txt`, and increment the release tag/prefix in the
scripts and workflow whenever the dependency set changes.
