#pragma once

#include <cstdint>
#include <string>

namespace exl {

/// Thin façade over ExLlamaV3 CUDA kernels (when EXL_HAS_EXLLAMAV3_KERNELS).
/// In EXL_STUB / without LibTorch, methods are no-ops or CPU fallbacks.
class KernelBridge {
public:
    KernelBridge() = default;

    /// Returns true when real EXL3 kernels are linked and CUDA is usable.
    static bool has_cuda_kernels();

    /// Human-readable backend: "exl3-cuda" | "torch" | "stub"
    static const char* backend_name();

    /// Probe CUDA device; returns false on stub or no device.
    bool initialize(int device_id = 0);

    /// Best-effort GEMM entry used by the engine forward path.
    /// When kernels are unavailable, returns false (caller uses stub sampling).
    bool try_exl3_gemm_probe();

    int device_id() const { return device_id_; }
    bool ready() const { return ready_; }

private:
    int device_id_ = 0;
    bool ready_ = false;
};

} // namespace exl
