#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>
#include <stddef.h>

/* ---------------------------------------------------------------------------
 * Export macro
 * --------------------------------------------------------------------------- */
#if defined(_WIN32) || defined(_WIN64)
#  ifdef EXL_BUILDING_DLL
#    define EXL_API __declspec(dllexport)
#  else
#    define EXL_API __declspec(dllimport)
#  endif
#else
#  define EXL_API __attribute__((visibility("default")))
#endif

/* ---------------------------------------------------------------------------
 * Opaque handles
 * --------------------------------------------------------------------------- */
typedef struct exl_engine_s exl_engine_t;
typedef struct exl_job_s    exl_job_t;

/* ---------------------------------------------------------------------------
 * Status / enums
 * --------------------------------------------------------------------------- */
typedef enum exl_status {
    EXL_OK                 =  0,
    EXL_ERR_INVALID_ARG    = -1,
    EXL_ERR_NOT_LOADED     = -2,
    EXL_ERR_ALREADY_LOADED = -3,
    EXL_ERR_BUSY           = -4,
    EXL_ERR_OOM            = -5,
    EXL_ERR_CANCELLED      = -6,
    EXL_ERR_INTERNAL       = -7,
    EXL_ERR_NOT_RUNNING    = -8,
    EXL_ERR_UNSUPPORTED    = -9,
} exl_status_t;

typedef enum exl_parallelism {
    EXL_PARALLEL_NONE   = 0,
    EXL_PARALLEL_TENSOR = 1,
    EXL_PARALLEL_PIPE   = 2,
    EXL_PARALLEL_MODEL  = 3,
} exl_parallelism_t;

typedef enum exl_job_state {
    EXL_JOB_WAITING  = 0,
    EXL_JOB_RUNNING  = 1,
    EXL_JOB_SWAPPED  = 2,
    EXL_JOB_FINISHED = 3,
    EXL_JOB_CANCELLED= 4,
    EXL_JOB_FAILED   = 5,
} exl_job_state_t;

/* ---------------------------------------------------------------------------
 * Engine configuration (passed to create)
 * --------------------------------------------------------------------------- */
typedef struct exl_engine_config {
    int32_t  parallelism;          /* exl_parallelism_t */
    int32_t  num_devices;          /* length of device_ids */
    const int32_t* device_ids;     /* CUDA device ordinals; NULL = {0} */
    int32_t  max_num_seqs;         /* default 256 */
    int32_t  max_num_batched_tokens; /* default 8192 */
    int32_t  max_chunk_size;       /* default 2048 */
    int32_t  page_size;            /* tokens per page; default 256 */
    int32_t  max_pages;            /* PageTable capacity; 0 = auto */
    float    gpu_memory_utilization; /* 0..1; hint for KV budget */
    int32_t  seed;                 /* RNG seed; -1 = nondeterministic */
} exl_engine_config_t;

/* ---------------------------------------------------------------------------
 * Job submission parameters
 * --------------------------------------------------------------------------- */
typedef struct exl_job_params {
    const int32_t* prompt_tokens;
    int32_t  prompt_length;
    int32_t  max_new_tokens;
    float    temperature;
    float    top_p;
    int32_t  top_k;
    int32_t  priority;             /* higher = sooner; default 0 */
    int32_t  stop_token_id;        /* -1 = none */
    void*    user_data;            /* opaque cookie returned in callbacks */
} exl_job_params_t;

/* ---------------------------------------------------------------------------
 * Engine metrics snapshot
 * --------------------------------------------------------------------------- */
typedef struct exl_metrics {
    int64_t  total_prompt_tokens;
    int64_t  total_generated_tokens;
    int64_t  num_jobs_waiting;
    int64_t  num_jobs_running;
    int64_t  num_jobs_swapped;
    int64_t  num_jobs_finished;
    int64_t  num_pages_used;
    int64_t  num_pages_free;
    double   tokens_per_second;
    double   last_step_ms;
    int64_t  step_count;
} exl_metrics_t;

/* ---------------------------------------------------------------------------
 * Engine lifecycle
 * --------------------------------------------------------------------------- */

/** Fill config with defaults. Safe to call before create. */
EXL_API void exl_engine_config_init(exl_engine_config_t* cfg);

/** Create engine. Returns NULL on failure; check exl_last_error(). */
EXL_API exl_engine_t* exl_engine_create(const exl_engine_config_t* cfg);

EXL_API void exl_engine_destroy(exl_engine_t* engine);

/**
 * Load model from a safetensors directory (or single file).
 * In STUB mode any path "loads" successfully.
 */
EXL_API exl_status_t exl_engine_load(exl_engine_t* engine, const char* model_path);

EXL_API exl_status_t exl_engine_unload(exl_engine_t* engine);

/** Start background scheduler thread (continuous Step()). */
EXL_API exl_status_t exl_engine_start(exl_engine_t* engine);

/** Stop background scheduler thread (joins). */
EXL_API exl_status_t exl_engine_stop(exl_engine_t* engine);

/**
 * Manual single scheduling step. Useful when the background thread is not
 * running (tests / embedders that drive the loop themselves).
 */
EXL_API exl_status_t exl_engine_step(exl_engine_t* engine);

EXL_API exl_status_t exl_engine_metrics(exl_engine_t* engine, exl_metrics_t* out);

/* ---------------------------------------------------------------------------
 * Jobs
 * --------------------------------------------------------------------------- */

/**
 * Submit a generation job. On success *out_job is a non-owning handle valid
 * until the job finishes and the caller no longer needs it (engine retains
 * ownership; call exl_job_cancel to abort).
 */
EXL_API exl_status_t exl_job_submit(exl_engine_t* engine,
                                    const exl_job_params_t* params,
                                    exl_job_t** out_job);

EXL_API exl_status_t exl_job_cancel(exl_engine_t* engine, exl_job_t* job);

EXL_API exl_job_state_t exl_job_state(const exl_job_t* job);

/**
 * Copy generated token ids into caller buffer.
 * *inout_count: on entry capacity, on exit number written.
 */
EXL_API exl_status_t exl_job_tokens(const exl_job_t* job,
                                    int32_t* out_tokens,
                                    int32_t* inout_count);

/** Release a job handle returned by exl_job_submit (does not cancel the job). */
EXL_API void exl_job_release(exl_job_t* job);

/* ---------------------------------------------------------------------------
 * Tokenization (STUB: whitespace / fake hash; real: HF tokenizer via model)
 * --------------------------------------------------------------------------- */

EXL_API exl_status_t exl_tokenize(exl_engine_t* engine,
                                  const char* text,
                                  int32_t* out_tokens,
                                  int32_t* inout_count);

EXL_API exl_status_t exl_detokenize(exl_engine_t* engine,
                                     const int32_t* tokens,
                                     int32_t count,
                                     char* out_text,
                                     int32_t* inout_nbytes);

/* ---------------------------------------------------------------------------
 * Diagnostics
 * --------------------------------------------------------------------------- */

/** Thread-local last error message (never NULL; empty string if none). */
EXL_API const char* exl_last_error(void);

/** Library version string, e.g. "0.1.0-stub" or "0.1.0". */
EXL_API const char* exl_version(void);

#ifdef __cplusplus
} /* extern "C" */
#endif
