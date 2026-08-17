#include "exllamasharp.h"

#include "config.h"
#include "engine.h"
#include "job.h"

#include <algorithm>
#include <cstring>
#include <memory>
#include <new>
#include <string>
#include <vector>

namespace {

thread_local std::string g_last_error;

void set_error(const char* msg) {
    g_last_error = msg ? msg : "";
}

void clear_error() { g_last_error.clear(); }

exl::Engine* as_engine(exl_engine_t* e) {
    return reinterpret_cast<exl::Engine*>(e);
}

/** Keep shared_ptrs alive for jobs returned through the C ABI. */
struct JobHandle {
    std::shared_ptr<exl::Job> job;
};

exl_job_t* wrap_job(const std::shared_ptr<exl::Job>& job) {
    auto* h = new (std::nothrow) JobHandle{job};
    return reinterpret_cast<exl_job_t*>(h);
}

JobHandle* as_handle(exl_job_t* j) {
    return reinterpret_cast<JobHandle*>(j);
}

const JobHandle* as_handle(const exl_job_t* j) {
    return reinterpret_cast<const JobHandle*>(j);
}

} // namespace

extern "C" {

EXL_API void exl_engine_config_init(exl_engine_config_t* cfg) {
    if (!cfg)
        return;
    std::memset(cfg, 0, sizeof(*cfg));
    cfg->parallelism = EXL_PARALLEL_NONE;
    cfg->num_devices = 0;
    cfg->device_ids = nullptr;
    cfg->max_num_seqs = 256;
    cfg->max_num_batched_tokens = 8192;
    cfg->max_chunk_size = 2048;
    cfg->page_size = 256;
    cfg->max_pages = 0;
    cfg->gpu_memory_utilization = 0.90f;
    cfg->seed = -1;
}

EXL_API exl_engine_t* exl_engine_create(const exl_engine_config_t* cfg) {
    clear_error();
    try {
        exl::EngineConfig c;
        if (cfg) {
            c.parallelism = static_cast<exl::ParallelismMode>(cfg->parallelism);
            c.max_num_seqs = cfg->max_num_seqs;
            c.max_num_batched_tokens = cfg->max_num_batched_tokens;
            c.max_chunk_size = cfg->max_chunk_size;
            c.page_size = cfg->page_size;
            c.max_pages = cfg->max_pages;
            c.gpu_memory_utilization = cfg->gpu_memory_utilization;
            c.seed = cfg->seed;
            if (cfg->num_devices > 0 && cfg->device_ids) {
                c.cuda_devices.assign(cfg->device_ids,
                                      cfg->device_ids + cfg->num_devices);
            }
        }
        auto* engine = new (std::nothrow) exl::Engine(std::move(c));
        if (!engine) {
            set_error("out of memory creating engine");
            return nullptr;
        }
        return reinterpret_cast<exl_engine_t*>(engine);
    } catch (const std::exception& ex) {
        set_error(ex.what());
        return nullptr;
    } catch (...) {
        set_error("unknown error creating engine");
        return nullptr;
    }
}

EXL_API void exl_engine_destroy(exl_engine_t* engine) {
    clear_error();
    delete as_engine(engine);
}

EXL_API exl_status_t exl_engine_load(exl_engine_t* engine, const char* model_path) {
    clear_error();
    if (!engine || !model_path) {
        set_error("invalid argument to exl_engine_load");
        return EXL_ERR_INVALID_ARG;
    }
    auto* e = as_engine(engine);
    if (e->is_loaded()) {
        set_error("model already loaded");
        return EXL_ERR_ALREADY_LOADED;
    }
    if (!e->load(model_path)) {
        set_error("failed to load model");
        return EXL_ERR_INTERNAL;
    }
    return EXL_OK;
}

EXL_API exl_status_t exl_engine_unload(exl_engine_t* engine) {
    clear_error();
    if (!engine) {
        set_error("invalid argument to exl_engine_unload");
        return EXL_ERR_INVALID_ARG;
    }
    as_engine(engine)->unload();
    return EXL_OK;
}

EXL_API exl_status_t exl_engine_start(exl_engine_t* engine) {
    clear_error();
    if (!engine) {
        set_error("invalid argument to exl_engine_start");
        return EXL_ERR_INVALID_ARG;
    }
    auto* e = as_engine(engine);
    if (!e->is_loaded()) {
        set_error("model not loaded");
        return EXL_ERR_NOT_LOADED;
    }
    e->start();
    return EXL_OK;
}

EXL_API exl_status_t exl_engine_stop(exl_engine_t* engine) {
    clear_error();
    if (!engine) {
        set_error("invalid argument to exl_engine_stop");
        return EXL_ERR_INVALID_ARG;
    }
    as_engine(engine)->stop();
    return EXL_OK;
}

EXL_API exl_status_t exl_engine_step(exl_engine_t* engine) {
    clear_error();
    if (!engine) {
        set_error("invalid argument to exl_engine_step");
        return EXL_ERR_INVALID_ARG;
    }
    auto* e = as_engine(engine);
    if (!e->is_loaded()) {
        set_error("model not loaded");
        return EXL_ERR_NOT_LOADED;
    }
    e->step();
    return EXL_OK;
}

EXL_API exl_status_t exl_engine_metrics(exl_engine_t* engine, exl_metrics_t* out) {
    clear_error();
    if (!engine || !out) {
        set_error("invalid argument to exl_engine_metrics");
        return EXL_ERR_INVALID_ARG;
    }
    auto* e = as_engine(engine);
    auto& sched = e->scheduler();
    std::memset(out, 0, sizeof(*out));
    out->total_prompt_tokens = e->total_prompt_tokens();
    out->total_generated_tokens = e->total_generated_tokens();
    out->num_jobs_waiting = sched.num_waiting();
    out->num_jobs_running = sched.num_running();
    out->num_jobs_swapped = sched.num_swapped();
    out->num_jobs_finished = sched.num_finished();
    out->num_pages_used = sched.page_table().num_used();
    out->num_pages_free = sched.page_table().num_free();
    out->last_step_ms = sched.last_step_ms();
    out->step_count = sched.step_count();

    const double ms = out->last_step_ms;
    if (ms > 0.0 && out->total_generated_tokens > 0 && out->step_count > 0) {
        // Rough instantaneous estimate from last step duration.
        out->tokens_per_second = (1000.0 / ms) * static_cast<double>(
            std::max<int64_t>(1, out->num_jobs_running));
    }
    return EXL_OK;
}

EXL_API exl_status_t exl_job_submit(exl_engine_t* engine,
                                    const exl_job_params_t* params,
                                    exl_job_t** out_job) {
    clear_error();
    if (!engine || !params || !out_job) {
        set_error("invalid argument to exl_job_submit");
        return EXL_ERR_INVALID_ARG;
    }
    *out_job = nullptr;
    if (!params->prompt_tokens || params->prompt_length <= 0) {
        set_error("prompt_tokens required");
        return EXL_ERR_INVALID_ARG;
    }

    auto* e = as_engine(engine);
    if (!e->is_loaded()) {
        set_error("model not loaded");
        return EXL_ERR_NOT_LOADED;
    }

    exl::Job job;
    job.prompt_tokens.assign(params->prompt_tokens,
                             params->prompt_tokens + params->prompt_length);
    job.max_new_tokens = params->max_new_tokens > 0 ? params->max_new_tokens : 128;
    job.temperature = params->temperature;
    job.top_p = params->top_p > 0.0f ? params->top_p : 1.0f;
    job.top_k = params->top_k;
    job.priority = params->priority;
    job.stop_token_id = params->stop_token_id;
    job.user_data = params->user_data;

    auto shared = e->submit(std::move(job));
    if (!shared) {
        set_error("failed to submit job");
        return EXL_ERR_INTERNAL;
    }
    *out_job = wrap_job(shared);
    if (!*out_job) {
        set_error("out of memory wrapping job");
        return EXL_ERR_OOM;
    }
    return EXL_OK;
}

EXL_API exl_status_t exl_job_cancel(exl_engine_t* engine, exl_job_t* job) {
    clear_error();
    if (!engine || !job) {
        set_error("invalid argument to exl_job_cancel");
        return EXL_ERR_INVALID_ARG;
    }
    auto* h = as_handle(job);
    if (!h || !h->job) {
        set_error("invalid job handle");
        return EXL_ERR_INVALID_ARG;
    }
    if (!as_engine(engine)->cancel(h->job->id)) {
        set_error("job not found or already terminal");
        return EXL_ERR_CANCELLED;
    }
    return EXL_OK;
}

EXL_API exl_job_state_t exl_job_state(const exl_job_t* job) {
    if (!job)
        return EXL_JOB_FAILED;
    const auto* h = as_handle(job);
    if (!h || !h->job)
        return EXL_JOB_FAILED;
    return static_cast<exl_job_state_t>(h->job->status);
}

EXL_API exl_status_t exl_job_tokens(const exl_job_t* job,
                                    int32_t* out_tokens,
                                    int32_t* inout_count) {
    clear_error();
    if (!job || !inout_count) {
        set_error("invalid argument to exl_job_tokens");
        return EXL_ERR_INVALID_ARG;
    }
    const auto* h = as_handle(job);
    if (!h || !h->job) {
        set_error("invalid job handle");
        return EXL_ERR_INVALID_ARG;
    }
    const auto& toks = h->job->output_tokens;
    const int32_t n = static_cast<int32_t>(toks.size());
    const int32_t cap = *inout_count;
    *inout_count = n;
    if (!out_tokens || cap <= 0)
        return EXL_OK;
    const int32_t copy_n = (std::min)(cap, n);
    if (copy_n > 0)
        std::memcpy(out_tokens, toks.data(), static_cast<size_t>(copy_n) * sizeof(int32_t));
    return EXL_OK;
}

EXL_API void exl_job_release(exl_job_t* job) {
    delete as_handle(job);
}

EXL_API exl_status_t exl_tokenize(exl_engine_t* engine,
                                  const char* text,
                                  int32_t* out_tokens,
                                  int32_t* inout_count) {
    clear_error();
    if (!engine || !text || !inout_count) {
        set_error("invalid argument to exl_tokenize");
        return EXL_ERR_INVALID_ARG;
    }
    auto* e = as_engine(engine);
    std::vector<int32_t> toks;
    if (!e->tokenize(text, toks)) {
        set_error("tokenize failed");
        return EXL_ERR_UNSUPPORTED;
    }
    const int32_t n = static_cast<int32_t>(toks.size());
    const int32_t cap = *inout_count;
    *inout_count = n;
    if (out_tokens && cap > 0) {
        const int32_t copy_n = (std::min)(cap, n);
        if (copy_n > 0)
            std::memcpy(out_tokens, toks.data(),
                        static_cast<size_t>(copy_n) * sizeof(int32_t));
    }
    return EXL_OK;
}

EXL_API exl_status_t exl_detokenize(exl_engine_t* engine,
                                     const int32_t* tokens,
                                     int32_t count,
                                     char* out_text,
                                     int32_t* inout_nbytes) {
    clear_error();
    if (!engine || !inout_nbytes || count < 0 || (count > 0 && !tokens)) {
        set_error("invalid argument to exl_detokenize");
        return EXL_ERR_INVALID_ARG;
    }
    auto* e = as_engine(engine);
    std::vector<int32_t> toks;
    if (count > 0)
        toks.assign(tokens, tokens + count);
    std::string text;
    if (!e->detokenize(toks, text)) {
        set_error("detokenize failed");
        return EXL_ERR_UNSUPPORTED;
    }
    const int32_t need = static_cast<int32_t>(text.size()) + 1;
    const int32_t cap = *inout_nbytes;
    *inout_nbytes = need;
    if (out_text && cap > 0) {
        const int32_t copy_n = (std::min)(cap - 1, static_cast<int32_t>(text.size()));
        if (copy_n > 0)
            std::memcpy(out_text, text.data(), static_cast<size_t>(copy_n));
        out_text[copy_n] = '\0';
    }
    return EXL_OK;
}

EXL_API const char* exl_last_error(void) {
    return g_last_error.c_str();
}

EXL_API const char* exl_version(void) {
#ifdef EXL_STUB
    return "0.1.0-stub";
#else
    return "0.1.0";
#endif
}

} // extern "C"
