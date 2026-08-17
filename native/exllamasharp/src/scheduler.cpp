#include "scheduler.h"
#include "engine.h"

#include <algorithm>
#include <chrono>

namespace exl {

Scheduler::Scheduler(Engine& engine, const EngineConfig& cfg)
    : engine_(engine)
    , cfg_(cfg)
    , page_table_(
          cfg.page_size > 0 ? cfg.page_size : PageTable::kDefaultPageSize,
          cfg.max_pages > 0 ? cfg.max_pages : 1024)
{
}

Scheduler::~Scheduler() {
    stop();
}

void Scheduler::start() {
    bool expected = false;
    if (!running_.compare_exchange_strong(expected, true))
        return;

    stop_requested_.store(false, std::memory_order_release);
    worker_ = std::thread([this] { thread_main(); });
}

void Scheduler::stop() {
    if (!running_.load(std::memory_order_acquire))
        return;

    stop_requested_.store(true, std::memory_order_release);
    cv_.notify_all();
    if (worker_.joinable())
        worker_.join();
    running_.store(false, std::memory_order_release);
}

void Scheduler::thread_main() {
    while (!stop_requested_.load(std::memory_order_acquire)) {
        step();
        // Brief yield when idle to avoid busy-spin.
        std::unique_lock<std::mutex> lock(mu_);
        if (waiting_.empty() && running_jobs_.empty() && swapped_.empty()) {
            cv_.wait_for(lock, std::chrono::milliseconds(1), [this] {
                return stop_requested_.load(std::memory_order_acquire)
                    || !waiting_.empty()
                    || !running_jobs_.empty()
                    || !swapped_.empty();
            });
        }
    }
}

std::shared_ptr<Job> Scheduler::submit(Job job) {
    std::lock_guard<std::mutex> lock(mu_);
    job.id = next_job_id_++;
    job.status = JobStatus::Waiting;
    job.start_time = Job::Clock::now();
    job.num_computed_tokens = 0;

    auto ptr = std::make_shared<Job>(std::move(job));
    jobs_by_id_[ptr->id] = ptr;
    waiting_.push(ptr);
    engine_.note_prompt_tokens(static_cast<int64_t>(ptr->prompt_tokens.size()));
    cv_.notify_one();
    return ptr;
}

bool Scheduler::cancel(int64_t job_id) {
    std::lock_guard<std::mutex> lock(mu_);
    auto it = jobs_by_id_.find(job_id);
    if (it == jobs_by_id_.end())
        return false;

    JobPtr job = it->second;
    if (job->is_terminal())
        return false;

    job->wants_cancel = true;
    if (job->status == JobStatus::Waiting) {
        job->status = JobStatus::Cancelled;
        job->finish_time = Job::Clock::now();
        page_table_.free(job->page_ids);
        job->page_ids.clear();
        ++finished_count_;
        jobs_by_id_.erase(it);
    }
    cv_.notify_one();
    return true;
}

int64_t Scheduler::num_waiting() const {
    std::lock_guard<std::mutex> lock(mu_);
    return static_cast<int64_t>(waiting_.size());
}

int64_t Scheduler::num_running() const {
    std::lock_guard<std::mutex> lock(mu_);
    return static_cast<int64_t>(running_jobs_.size());
}

int64_t Scheduler::num_swapped() const {
    std::lock_guard<std::mutex> lock(mu_);
    return static_cast<int64_t>(swapped_.size());
}

int64_t Scheduler::num_finished() const {
    std::lock_guard<std::mutex> lock(mu_);
    return finished_count_;
}

int32_t Scheduler::pages_needed_for(const Job& job) const {
    const int32_t total = job.total_tokens() + job.remaining_to_generate();
    const int32_t ps = page_table_.page_size();
    const int32_t need = (total + ps - 1) / ps;
    const int32_t have = static_cast<int32_t>(job.page_ids.size());
    return std::max(0, need - have);
}

void Scheduler::admit_from_waiting() {
    // Prefer re-admit swapped (higher priority / FIFO) before new waiting.
    while (!swapped_.empty()
           && static_cast<int32_t>(running_jobs_.size()) < cfg_.max_num_seqs) {
        JobPtr job = swapped_.front();
        const int32_t need = pages_needed_for(*job);
        auto pages = page_table_.allocate(need);
        if (!pages)
            break;
        job->page_ids.insert(job->page_ids.end(), pages->begin(), pages->end());
        job->status = JobStatus::Running;
        swapped_.erase(swapped_.begin());
        running_jobs_.push_back(job);
    }

    while (!waiting_.empty()
           && static_cast<int32_t>(running_jobs_.size()) < cfg_.max_num_seqs) {
        JobPtr job = waiting_.top();
        if (job->wants_cancel || job->status == JobStatus::Cancelled) {
            waiting_.pop();
            continue;
        }

        const int32_t need = pages_needed_for(*job);
        auto pages = page_table_.allocate(need);
        if (!pages)
            break;

        waiting_.pop();

        // Prefix-cache: hash full pages of the prompt and try reuse.
        const int32_t ps = page_table_.page_size();
        uint64_t rolling = 0;
        const int32_t prompt_pages =
            static_cast<int32_t>(job->prompt_tokens.size()) / ps;
        for (int32_t pi = 0; pi < prompt_pages && pi < static_cast<int32_t>(pages->size()); ++pi) {
            rolling = PageTable::hash_tokens(
                job->prompt_tokens.data() + static_cast<size_t>(pi) * ps, ps, rolling);
            if (auto hit = page_table_.lookup_prefix(rolling)) {
                // Reuse cached page; free the freshly allocated slot.
                page_table_.free((*pages)[static_cast<size_t>(pi)]);
                (*pages)[static_cast<size_t>(pi)] = *hit;
                job->num_computed_tokens = std::max(
                    job->num_computed_tokens, (pi + 1) * ps);
            } else {
                page_table_.set_prefix_hash((*pages)[static_cast<size_t>(pi)], rolling);
            }
        }

        job->page_ids = std::move(*pages);
        job->status = JobStatus::Running;
        running_jobs_.push_back(job);
    }
}

void Scheduler::preempt_if_needed() {
    // If waiting jobs exist and we are out of pages, swap lowest-priority running.
    if (waiting_.empty())
        return;
    if (page_table_.num_free() > 0)
        return;
    if (running_jobs_.empty())
        return;

    auto victim_it = std::min_element(
        running_jobs_.begin(), running_jobs_.end(),
        [](const JobPtr& a, const JobPtr& b) {
            if (a->priority != b->priority)
                return a->priority < b->priority;
            return a->id > b->id;
        });

    JobPtr victim = *victim_it;
    page_table_.free(victim->page_ids);
    victim->page_ids.clear();
    victim->status = JobStatus::Swapped;
    running_jobs_.erase(victim_it);
    swapped_.push_back(victim);
}

void Scheduler::schedule_running_chunk(std::vector<JobPtr>& batch, int32_t& token_budget) {
    batch.clear();
    const int32_t chunk = cfg_.max_chunk_size > 0 ? cfg_.max_chunk_size : 2048;

    // Stable order: higher priority first.
    std::stable_sort(running_jobs_.begin(), running_jobs_.end(),
                     [](const JobPtr& a, const JobPtr& b) {
                         if (a->priority != b->priority)
                             return a->priority > b->priority;
                         return a->id < b->id;
                     });

    for (auto& job : running_jobs_) {
        if (token_budget <= 0)
            break;
        if (job->wants_cancel)
            continue;

        const int32_t total = job->total_tokens();
        const int32_t remaining_prefill = std::max(0, total - job->num_computed_tokens);
        int32_t take = 0;
        if (remaining_prefill > 0) {
            take = std::min({remaining_prefill, chunk, token_budget});
        } else if (job->remaining_to_generate() > 0) {
            take = std::min(1, token_budget); // decode: one token per step per seq
        } else {
            continue;
        }

        if (take <= 0)
            continue;

        token_budget -= take;
        batch.push_back(job);
        // Stash intended chunk size in num_computed via temporary: engine advances.
        (void)take;
    }
}

void Scheduler::execute_batch(const std::vector<JobPtr>& batch) {
    if (batch.empty())
        return;
    engine_.forward_and_sample(batch);
}

void Scheduler::finish_or_continue(JobPtr& job) {
    if (job->wants_cancel) {
        job->status = JobStatus::Cancelled;
        job->finish_time = Job::Clock::now();
        page_table_.free(job->page_ids);
        job->page_ids.clear();
        ++finished_count_;
        jobs_by_id_.erase(job->id);
        return;
    }

    const bool hit_stop =
        job->stop_token_id >= 0
        && !job->output_tokens.empty()
        && job->output_tokens.back() == job->stop_token_id;

    if (job->remaining_to_generate() <= 0 || hit_stop) {
        job->status = JobStatus::Finished;
        job->finish_time = Job::Clock::now();
        page_table_.free(job->page_ids);
        job->page_ids.clear();
        ++finished_count_;
        jobs_by_id_.erase(job->id);
    }
}

void Scheduler::step() {
    using clock = std::chrono::steady_clock;
    const auto t0 = clock::now();

    std::vector<JobPtr> batch;
    {
        std::lock_guard<std::mutex> lock(mu_);

        // Handle cancels on running set.
        for (auto& job : running_jobs_) {
            if (job->wants_cancel)
                finish_or_continue(job);
        }
        running_jobs_.erase(
            std::remove_if(running_jobs_.begin(), running_jobs_.end(),
                           [](const JobPtr& j) { return j->is_terminal(); }),
            running_jobs_.end());

        preempt_if_needed();
        admit_from_waiting();

        int32_t budget = cfg_.max_num_batched_tokens > 0
                             ? cfg_.max_num_batched_tokens
                             : 8192;
        schedule_running_chunk(batch, budget);
    }

    // Forward outside the scheduler lock so submit/cancel stay responsive.
    execute_batch(batch);

    {
        std::lock_guard<std::mutex> lock(mu_);
        for (auto& job : batch) {
            if (!job->is_terminal())
                finish_or_continue(job);
        }
        running_jobs_.erase(
            std::remove_if(running_jobs_.begin(), running_jobs_.end(),
                           [](const JobPtr& j) { return j->is_terminal(); }),
            running_jobs_.end());

        // Occasional defrag when free list gets fragmented.
        if (step_count_.load(std::memory_order_relaxed) % 64 == 0)
            page_table_.defrag();
    }

    const auto t1 = clock::now();
    const double ms =
        std::chrono::duration<double, std::milli>(t1 - t0).count();
    last_step_ms_.store(ms, std::memory_order_relaxed);
    step_count_.fetch_add(1, std::memory_order_relaxed);
}

} // namespace exl
