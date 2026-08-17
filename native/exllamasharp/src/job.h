#pragma once

#include <chrono>
#include <cstdint>
#include <vector>

namespace exl {

enum class JobStatus : int32_t {
    Waiting   = 0,
    Running   = 1,
    Swapped   = 2,
    Finished  = 3,
    Cancelled = 4,
    Failed    = 5,
};

struct Job {
    int64_t  id = 0;
    int32_t  priority = 0;
    JobStatus status = JobStatus::Waiting;

    std::vector<int32_t> prompt_tokens;
    std::vector<int32_t> output_tokens;

    int32_t max_new_tokens = 128;
    float   temperature    = 1.0f;
    float   top_p          = 1.0f;
    int32_t top_k          = 0;
    int32_t stop_token_id  = -1;

    void* user_data = nullptr;

    using Clock = std::chrono::steady_clock;
    Clock::time_point start_time{};
    Clock::time_point finish_time{};

    /** Pages currently pinned for this job (PageTable indices). */
    std::vector<int32_t> page_ids;

    /** Tokens already processed through the model (prompt + generated). */
    int32_t num_computed_tokens = 0;

    bool wants_cancel = false;

    bool is_terminal() const {
        return status == JobStatus::Finished
            || status == JobStatus::Cancelled
            || status == JobStatus::Failed;
    }

    int32_t total_tokens() const {
        return static_cast<int32_t>(prompt_tokens.size() + output_tokens.size());
    }

    int32_t remaining_to_generate() const {
        return max_new_tokens - static_cast<int32_t>(output_tokens.size());
    }
};

/** Comparator for priority queues: higher priority first, then FIFO by id. */
struct JobPriorityLess {
    bool operator()(const Job* a, const Job* b) const {
        if (a->priority != b->priority)
            return a->priority < b->priority; // max-heap via priority_queue
        return a->id > b->id;                 // older (smaller id) first
    }
};

} // namespace exl
