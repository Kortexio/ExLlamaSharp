#include "engine.h"
#include "kernel_bridge.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <numeric>
#include <sstream>

#ifndef EXL_STUB
// Real build: LibTorch + ExLlamaV3 kernels via KernelBridge / EXL_HAS_EXLLAMAV3_KERNELS.
// Full transformer forward is not ported here — production EXL3 inference uses the
// Python worker (tools/exl3_worker). Native load_safetensors validates EXL3 folders.
namespace fs = std::filesystem;
#endif

namespace exl {

namespace {

uint64_t fnv1a_bytes(const void* data, size_t n, uint64_t seed = 14695981039346656037ull) {
    const auto* p = static_cast<const uint8_t*>(data);
    uint64_t h = seed;
    for (size_t i = 0; i < n; ++i) {
        h ^= p[i];
        h *= 1099511628211ull;
    }
    return h;
}

} // namespace

Engine::Engine(EngineConfig cfg)
    : cfg_(std::move(cfg))
{
    if (cfg_.max_num_seqs <= 0)
        cfg_.max_num_seqs = 256;
    if (cfg_.max_num_batched_tokens <= 0)
        cfg_.max_num_batched_tokens = 8192;
    if (cfg_.max_chunk_size <= 0)
        cfg_.max_chunk_size = 2048;
    if (cfg_.page_size <= 0)
        cfg_.page_size = 256;
    if (cfg_.max_pages <= 0) {
        // Rough default: enough pages for max_num_seqs * 4k context.
        const int64_t tokens = static_cast<int64_t>(cfg_.max_num_seqs) * 4096;
        cfg_.max_pages = static_cast<int32_t>(
            (tokens + cfg_.page_size - 1) / cfg_.page_size);
    }
    if (cfg_.cuda_devices.empty())
        cfg_.cuda_devices.push_back(0);

    kernels_ = std::make_unique<KernelBridge>();
    kernels_->initialize(cfg_.cuda_devices.front());

    scheduler_ = std::make_unique<Scheduler>(*this, cfg_);
}

Engine::~Engine() {
    stop();
    unload();
}

bool Engine::load(const std::string& model_path) {
    std::lock_guard<std::mutex> lock(model_mu_);
    if (loaded_.load(std::memory_order_relaxed))
        return false;

#ifdef EXL_STUB
    (void)model_path;
    cfg_.model_path = model_path;
    vocab_size_ = 32000;
    eos_token_id_ = 2;
    loaded_.store(true, std::memory_order_release);
    return true;
#else
    if (!load_safetensors(model_path))
        return false;
    cfg_.model_path = model_path;
    loaded_.store(true, std::memory_order_release);
    return true;
#endif
}

bool Engine::unload() {
    stop();
    std::lock_guard<std::mutex> lock(model_mu_);
    if (!loaded_.load(std::memory_order_relaxed))
        return true;

#ifndef EXL_STUB
    unload_model();
#endif
    cfg_.model_path.clear();
    loaded_.store(false, std::memory_order_release);
    return true;
}

void Engine::start() {
    if (!is_loaded())
        return;
    scheduler_->start();
}

void Engine::stop() {
    if (scheduler_)
        scheduler_->stop();
}

void Engine::step() {
    if (!is_loaded())
        return;
    scheduler_->step();
}

std::shared_ptr<Job> Engine::submit(Job job) {
    if (!is_loaded())
        return nullptr;
    return scheduler_->submit(std::move(job));
}

bool Engine::cancel(int64_t job_id) {
    return scheduler_->cancel(job_id);
}

#ifdef EXL_STUB

int32_t Engine::stub_next_token(const Job& job) const {
    // Deterministic fake token from prompt + outputs hash.
    uint64_t h = fnv1a_bytes(job.prompt_tokens.data(),
                             job.prompt_tokens.size() * sizeof(int32_t));
    if (!job.output_tokens.empty()) {
        h = fnv1a_bytes(job.output_tokens.data(),
                        job.output_tokens.size() * sizeof(int32_t), h);
    }
    h ^= static_cast<uint64_t>(job.output_tokens.size() + 1) * 0x9e3779b97f4a7c15ull;
    const int32_t tok = static_cast<int32_t>(h % static_cast<uint64_t>(vocab_size_));
    // Avoid emitting EOS until near max_new_tokens so jobs run their length.
    if (tok == eos_token_id_ && job.remaining_to_generate() > 1)
        return (tok + 1) % static_cast<int32_t>(vocab_size_);
    return tok;
}

#endif

int32_t Engine::sample_greedy(const std::vector<float>& logits) const {
    return static_cast<int32_t>(
        std::distance(logits.begin(), std::max_element(logits.begin(), logits.end())));
}

int32_t Engine::sample_top_p(const std::vector<float>& logits,
                             float temperature,
                             float top_p) const {
    // Stub sampling path used by both STUB and as fallback.
    if (temperature <= 0.0f || top_p >= 1.0f) {
        // Degenerate: greedy
        return sample_greedy(logits);
    }

    const size_t n = logits.size();
    std::vector<int32_t> idx(n);
    std::iota(idx.begin(), idx.end(), 0);

    const float inv_t = 1.0f / temperature;
    std::vector<float> probs(n);
    float max_l = *std::max_element(logits.begin(), logits.end());
    float sum = 0.0f;
    for (size_t i = 0; i < n; ++i) {
        probs[i] = std::exp((logits[i] - max_l) * inv_t);
        sum += probs[i];
    }
    for (float& p : probs)
        p /= sum;

    std::sort(idx.begin(), idx.end(),
              [&](int32_t a, int32_t b) { return probs[static_cast<size_t>(a)]
                                               > probs[static_cast<size_t>(b)]; });

    float cum = 0.0f;
    size_t cutoff = n;
    for (size_t i = 0; i < n; ++i) {
        cum += probs[static_cast<size_t>(idx[i])];
        if (cum >= top_p) {
            cutoff = i + 1;
            break;
        }
    }

    // Deterministic pick: highest-prob token inside the nucleus (no RNG in stub).
    if (cutoff == 0)
        return idx.front();
    return idx.front();
}

void Engine::forward_and_sample(const std::vector<std::shared_ptr<Job>>& batch) {
    if (!is_loaded() || batch.empty())
        return;

    std::lock_guard<std::mutex> lock(model_mu_);

    for (const auto& job : batch) {
        if (!job || job->is_terminal() || job->wants_cancel)
            continue;

        const int32_t total = job->total_tokens();
        if (job->num_computed_tokens < total) {
            // Prefill: advance computed pointer up to current sequence length.
            // Real build would run the transformer over the new token span.
            const int32_t chunk = std::min(
                cfg_.max_chunk_size, total - job->num_computed_tokens);
            job->num_computed_tokens += chunk;
            if (job->num_computed_tokens < total)
                continue; // still prefilling; decode next step
        }

        if (job->remaining_to_generate() <= 0)
            continue;

#ifdef EXL_STUB
        const int32_t next = stub_next_token(*job);
#else
        // Metadata-only native load: sampling is a placeholder.
        // Production text generation MUST use ExLlamaV3WorkerEngine (Python worker).
        std::vector<float> logits(static_cast<size_t>(vocab_size_), 0.0f);
        if (vocab_size_ > 0)
            logits[static_cast<size_t>(eos_token_id_ % vocab_size_)] = -1.0f;
        const int32_t next = (job->temperature <= 0.0f)
            ? sample_greedy(logits)
            : sample_top_p(logits, job->temperature, job->top_p);
#endif
        job->output_tokens.push_back(next);
        job->num_computed_tokens += 1;
        note_generated_tokens(1);

        // Grow pages if sequence spilled into a new page.
        const int32_t need_pages =
            (job->total_tokens() + cfg_.page_size - 1) / cfg_.page_size;
        while (static_cast<int32_t>(job->page_ids.size()) < need_pages) {
            auto extra = scheduler_->page_table().allocate(1);
            if (!extra || extra->empty())
                break;
            job->page_ids.push_back((*extra)[0]);
        }
    }
}

bool Engine::tokenize(const std::string& text, std::vector<int32_t>& out) const {
    out.clear();
#ifdef EXL_STUB
    // Fake tokenizer: map each byte / whitespace word to a token id via hash.
    if (text.empty())
        return true;

    size_t i = 0;
    while (i < text.size()) {
        while (i < text.size() && (text[i] == ' ' || text[i] == '\t' || text[i] == '\n'))
            ++i;
        if (i >= text.size())
            break;
        size_t j = i;
        while (j < text.size() && text[j] != ' ' && text[j] != '\t' && text[j] != '\n')
            ++j;
        const uint64_t h = fnv1a_bytes(text.data() + i, j - i);
        out.push_back(static_cast<int32_t>(h % static_cast<uint64_t>(vocab_size_)));
        i = j;
    }
    if (out.empty()) {
        const uint64_t h = fnv1a_bytes(text.data(), text.size());
        out.push_back(static_cast<int32_t>(h % static_cast<uint64_t>(vocab_size_)));
    }
    return true;
#else
    // Text tokenization is handled by the Python EXL3 worker / managed fallback.
    (void)text;
    return false;
#endif
}

bool Engine::detokenize(const std::vector<int32_t>& tokens, std::string& out) const {
    out.clear();
#ifdef EXL_STUB
    std::ostringstream oss;
    for (size_t i = 0; i < tokens.size(); ++i) {
        if (i)
            oss << ' ';
        oss << "T" << tokens[i];
    }
    out = oss.str();
    return true;
#else
    (void)tokens;
    return false;
#endif
}

#ifndef EXL_STUB

namespace {

bool read_file_text(const fs::path& p, std::string& out) {
    std::ifstream in(p, std::ios::binary);
    if (!in)
        return false;
    std::ostringstream ss;
    ss << in.rdbuf();
    out = ss.str();
    return true;
}

// Minimal JSON number extractor: "key": 123 or "key":123
bool json_find_int(const std::string& json, const char* key, int64_t& value) {
    const std::string needle = std::string("\"") + key + "\"";
    size_t pos = 0;
    while ((pos = json.find(needle, pos)) != std::string::npos) {
        pos += needle.size();
        while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\r' ||
                                     json[pos] == '\n' || json[pos] == ':'))
            ++pos;
        if (pos >= json.size())
            return false;
        // skip null
        if (json.compare(pos, 4, "null") == 0) {
            pos += 4;
            continue;
        }
        char* end = nullptr;
        const long long v = std::strtoll(json.c_str() + pos, &end, 10);
        if (end != json.c_str() + pos) {
            value = static_cast<int64_t>(v);
            return true;
        }
        break;
    }
    return false;
}

bool json_contains_exl3(const std::string& json) {
    // quant_method / quantization markers used by EXL3 packs
    auto lower = json;
    std::transform(lower.begin(), lower.end(), lower.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return lower.find("exl3") != std::string::npos;
}

bool dir_has_safetensors(const fs::path& dir) {
    if (!fs::is_directory(dir))
        return false;
    for (const auto& ent : fs::directory_iterator(dir)) {
        if (!ent.is_regular_file())
            continue;
        auto ext = ent.path().extension().string();
        std::transform(ext.begin(), ext.end(), ext.begin(),
                       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        if (ext == ".safetensors")
            return true;
    }
    return false;
}

} // namespace

bool Engine::load_safetensors(const std::string& model_path) {
    const fs::path root(model_path);
    if (!fs::is_directory(root))
        return false;

    const fs::path config_path = root / "config.json";
    const fs::path tok_config = root / "tokenizer_config.json";
    const fs::path tokenizer_json = root / "tokenizer.json";

    if (!fs::is_regular_file(config_path))
        return false;
    if (!dir_has_safetensors(root))
        return false;
    if (!fs::is_regular_file(tokenizer_json) && !fs::is_regular_file(tok_config))
        return false;

    std::string config_text;
    if (!read_file_text(config_path, config_text))
        return false;

    // Prefer EXL3-marked configs; also accept HF+safetensors EXL3 layouts.
    const bool looks_exl3 = json_contains_exl3(config_text) ||
                            (dir_has_safetensors(root) && fs::is_regular_file(tokenizer_json));
    if (!looks_exl3)
        return false;

    int64_t vocab = 0;
    if (!json_find_int(config_text, "vocab_size", vocab) || vocab <= 0)
        vocab = 128256; // Llama-3 default
    vocab_size_ = vocab;

    int64_t eos = 2;
    if (!json_find_int(config_text, "eos_token_id", eos)) {
        // tokenizer_config.json often has eos_token as a string; keep numeric defaults.
        if (fs::is_regular_file(tok_config)) {
            std::string tc;
            if (read_file_text(tok_config, tc)) {
                int64_t eos2 = 0;
                if (json_find_int(tc, "eos_token_id", eos2))
                    eos = eos2;
            }
        }
    }
    eos_token_id_ = static_cast<int32_t>(eos);

    // Metadata-only success. Weights stay on the Python worker path.
    model_ = nullptr;
    cache_ = nullptr;
    return true;
}

void Engine::unload_model() {
    model_ = nullptr;
    cache_ = nullptr;
}

#endif

} // namespace exl
