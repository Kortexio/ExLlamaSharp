#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace exl {

enum class ParallelismMode : int32_t {
    None   = 0,
    Tensor = 1,
    Pipe   = 2,
    Model  = 3,
};

struct EngineConfig {
    ParallelismMode parallelism = ParallelismMode::None;
    std::vector<int32_t> cuda_devices{0};

    int32_t max_num_seqs            = 256;
    int32_t max_num_batched_tokens  = 8192;
    int32_t max_chunk_size          = 2048;
    int32_t page_size               = 256;
    int32_t max_pages               = 0;   // 0 = derive from GPU mem hint
    float   gpu_memory_utilization  = 0.90f;
    int32_t seed                    = -1;

    std::string model_path;  // set on load
};

} // namespace exl
