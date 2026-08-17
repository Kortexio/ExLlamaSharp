#pragma once

#include "config.h"
#include "job.h"
#include "pagetable.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <queue>
#include <thread>
#include <unordered_map>
#include <vector>

namespace exl {

class Engine; // forward

/**
 * Continuous-batching scheduler inspired by vLLM.
 * Queues: waiting / running / swapped. Background thread runs Step() in a loop.
 */
class Scheduler {
public:
    explicit Scheduler(Engine& engine, const EngineConfig& cfg);
    ~Scheduler();

    Scheduler(const Scheduler&) = delete;
    Scheduler& operator=(const Scheduler&) = delete;

    void start();
    void stop();
    bool is_running() const { return running_.load(std::memory_order_acquire); }

    /** One scheduling iteration: admit, schedule, execute chunk, finish. */
    void step();

    std::shared_ptr<Job> submit(Job job);
    bool cancel(int64_t job_id);

    int64_t num_waiting()  const;
    int64_t num_running()  const;
    int64_t num_swapped()  const;
    int64_t num_finished() const;
    double  last_step_ms() const { return last_step_ms_.load(std::memory_order_relaxed); }
    int64_t step_count()   const { return step_count_.load(std::memory_order_relaxed); }

    PageTable& page_table() { return page_table_; }
    const PageTable& page_table() const { return page_table_; }

private:
    using JobPtr = std::shared_ptr<Job>;

    struct WaitingCmp {
        bool operator()(const JobPtr& a, const JobPtr& b) const {
            return JobPriorityLess{}(a.get(), b.get());
        }
    };

    void thread_main();
    void admit_from_waiting();
    void preempt_if_needed();
    void schedule_running_chunk(std::vector<JobPtr>& batch, int32_t& token_budget);
    void execute_batch(const std::vector<JobPtr>& batch);
    void finish_or_continue(JobPtr& job);
    int32_t pages_needed_for(const Job& job) const;

    Engine& engine_;
    EngineConfig cfg_;
    PageTable page_table_;

    mutable std::mutex mu_;
    std::condition_variable cv_;

    std::priority_queue<JobPtr, std::vector<JobPtr>, WaitingCmp> waiting_;
    std::vector<JobPtr> running_jobs_;
    std::vector<JobPtr> swapped_;
    std::unordered_map<int64_t, JobPtr> jobs_by_id_;
    int64_t next_job_id_ = 1;
    int64_t finished_count_ = 0;

    std::atomic<bool> running_{false};
    std::atomic<bool> stop_requested_{false};
    std::thread worker_;

    std::atomic<double> last_step_ms_{0.0};
    std::atomic<int64_t> step_count_{0};
};

} // namespace exl
