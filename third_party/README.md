# Third-party dependencies

## ExLlamaV3 (local tree — DO NOT PUSH UPSTREAM)

ExLlamaSharp uses a **local copy** of [exllamav3](https://github.com/turboderp-org/exllamav3) under:

```
third_party/exllamav3/
```

**Rules:**
- Use the files already on disk. Do **not** re-clone unless the tree is missing.
- **Never** `git push` to `turboderp-org/exllamav3` (or any upstream ExLlama remote).
- Native CMake (`native/exllamasharp`) points at this tree and compiles `exllamav3_ext` **without** `bindings.cpp` / pybind11 when `EXL_STUB=OFF`.

### Expected layout

```
third_party/
  README.md
  exllamav3/
    exllamav3/
      exllamav3_ext/     ← CUDA kernels (.cu/.cpp)
      ...
    setup.py
```

### Build note

```powershell
# Stub (no CUDA) — always safe for CI
cmake -S native/exllamasharp -B native/exllamasharp/build -DEXL_STUB=ON
cmake --build native/exllamasharp/build --config Release

# Full CUDA (LibTorch lives in third_party/libtorch after download)
$libtorch = "$PWD/third_party/libtorch"
cmake -S native/exllamasharp -B native/exllamasharp/build `
  -DEXL_STUB=OFF -DEXL_LINK_EXLLAMAV3=ON `
  -DCMAKE_PREFIX_PATH="$libtorch"
cmake --build native/exllamasharp/build --config Release
```

See also `packaging/install-cuda-libtorch.md`.
