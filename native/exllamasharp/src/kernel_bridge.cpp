#include "kernel_bridge.h"

#if defined(EXL_HAS_EXLLAMAV3_KERNELS) && defined(EXL_HAS_TORCH) && !defined(EXL_STUB)
#include <ATen/ATen.h>
#include <c10/cuda/CUDAGuard.h>
#include <cuda_runtime.h>
#include "quant/exl3_gemm.cuh"
#endif

namespace exl {

bool KernelBridge::has_cuda_kernels() {
#if defined(EXL_HAS_EXLLAMAV3_KERNELS) && defined(EXL_HAS_TORCH) && !defined(EXL_STUB)
    return true;
#else
    return false;
#endif
}

const char* KernelBridge::backend_name() {
#if defined(EXL_STUB)
    return "stub";
#elif defined(EXL_HAS_EXLLAMAV3_KERNELS)
    return "exl3-cuda";
#elif defined(EXL_HAS_TORCH)
    return "torch";
#else
    return "stub";
#endif
}

bool KernelBridge::initialize(int device_id) {
    device_id_ = device_id;
#if defined(EXL_HAS_EXLLAMAV3_KERNELS) && defined(EXL_HAS_TORCH) && !defined(EXL_STUB)
    int count = 0;
    if (cudaGetDeviceCount(&count) != cudaSuccess || count <= 0) {
        ready_ = false;
        return false;
    }
    if (device_id_ < 0 || device_id_ >= count)
        device_id_ = 0;
    if (cudaSetDevice(device_id_) != cudaSuccess) {
        ready_ = false;
        return false;
    }
    ready_ = true;
    return true;
#else
    ready_ = false;
    return false;
#endif
}

bool KernelBridge::try_exl3_gemm_probe() {
#if defined(EXL_HAS_EXLLAMAV3_KERNELS) && defined(EXL_HAS_TORCH) && !defined(EXL_STUB)
    if (!ready_)
        return false;
    // Soft probe: ensure symbols resolve. Full GEMM needs loaded EXL3 weights.
    // Returning true means the kernel entry points are linked.
    (void)&exl3_gemm;
    return true;
#else
    return false;
#endif
}

} // namespace exl
