#pragma once

#include "config.h"
#include "job.h"
#include "scheduler.h"

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace exl {

class KernelBridge;

/**
 * LlamaForCausalLM inference engine.
 * Owns the Scheduler and (when not STUB) LibTorch / ExLlamaV3 model weights.
 */
class Engine {
public:
    explicit Engine(EngineConfig cfg);
    ~Engine();

    Engine(const Engine&) = delete;
    Engine& operator=(const Engine&) = delete;

    const EngineConfig& config() const { return cfg_; }

    bool load(const std::string& model_path);
    bool unload();
    bool is_loaded() const { return loaded_.load(std::memory_order_acquire); }

    void start();
    void stop();
    void step();

    std::shared_ptr<Job> submit(Job job);
    bool cancel(int64_t job_id);

    /**
     * Run a forward pass for a batch of jobs, appending one sampled token
     * (or a chunk of prefill) to each.
     * Called by Scheduler under its own locking discipline.
     */
    void forward_and_sample(const std::vector<std::shared_ptr<Job>>& batch);

    bool tokenize(const std::string& text, std::vector<int32_t>& out) const;
    bool detokenize(const std::vector<int32_t>& tokens, std::string& out) const;

    int64_t vocab_size() const { return vocab_size_; }
    Scheduler& scheduler() { return *scheduler_; }
    const Scheduler& scheduler() const { return *scheduler_; }
    KernelBridge* kernels() { return kernels_.get(); }

    int64_t total_prompt_tokens() const {
        return total_prompt_tokens_.load(std::memory_order_relaxed);
    }
    int64_t total_generated_tokens() const {
        return total_generated_tokens_.load(std::memory_order_relaxed);
    }

    void note_prompt_tokens(int64_t n) {
        total_prompt_tokens_.fetch_add(n, std::memory_order_relaxed);
    }
    void note_generated_tokens(int64_t n) {
        total_generated_tokens_.fetch_add(n, std::memory_order_relaxed);
    }

private:
    int32_t sample_greedy(const std::vector<float>& logits) const;
    int32_t sample_top_p(const std::vector<float>& logits, float temperature, float top_p) const;

#ifdef EXL_STUB
    /** Deterministic fake next-token from prompt/output hash. */
    int32_t stub_next_token(const Job& job) const;
#else
    bool load_safetensors(const std::string& model_path);
    void unload_model();
#endif

    EngineConfig cfg_;
    std::unique_ptr<Scheduler> scheduler_;
    std::unique_ptr<KernelBridge> kernels_;

    std::atomic<bool> loaded_{false};
    mutable std::mutex model_mu_;

    int64_t vocab_size_ = 32000;
    int32_t eos_token_id_ = 2;

    std::atomic<int64_t> total_prompt_tokens_{0};
    std::atomic<int64_t> total_generated_tokens_{0};

#ifndef EXL_STUB
    void* model_ = nullptr;
    void* cache_ = nullptr;
#endif
};

} // namespace exl
