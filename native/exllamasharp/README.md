# Native engine (`exllamasharp.dll`)

## Modes

| Mode | Flag | Requires | Behavior |
|------|------|----------|----------|
| Stub | `-DEXL_STUB=ON` | MSVC + CMake | Queues + deterministic tokens (CI / no GPU) |
| CUDA | `-DEXL_STUB=OFF -DEXL_LINK_EXLLAMAV3=ON` | CUDA + LibTorch + local `third_party/exllamav3` | Compiles `exllamav3_ext` **without** pybind11 |

## Local ExLlamaV3

Uses `../../third_party/exllamav3` already on disk. **Do not push** to `turboderp-org/exllamav3`.

## Build stub

```powershell
powershell -File packaging/build-native-stub.ps1
```

Output: `src/ExLlamaSharp/runtimes/win-x64/native/exllamasharp.dll`

## Build CUDA (when toolkit + LibTorch installed)

```powershell
cmake -S native/exllamasharp -B native/exllamasharp/build `
  -DEXL_STUB=OFF -DEXL_LINK_EXLLAMAV3=ON `
  -DCMAKE_PREFIX_PATH="C:/path/to/libtorch"
cmake --build native/exllamasharp/build --config Release
```
