#pragma once

#include <cstdint>
#include <mutex>
#include <optional>
#include <unordered_map>
#include <vector>

namespace exl {

/**
 * Paged KV-cache table (vLLM-style).
 * Each page holds `page_size` tokens (default 256).
 * Prefix hashing enables automatic prefix-cache reuse across jobs.
 */
class PageTable {
public:
    static constexpr int32_t kDefaultPageSize = 256;

    struct Page {
        int32_t  index      = -1;
        int32_t  ref_count  = 0;
        uint64_t prefix_hash = 0;   // hash of token sequence covering this page
        bool     allocated  = false;
        bool     on_gpu     = true; // false when swapped to host
    };

    explicit PageTable(int32_t page_size = kDefaultPageSize, int32_t max_pages = 1024);

    int32_t page_size() const { return page_size_; }
    int32_t max_pages() const { return max_pages_; }
    int32_t num_used()  const;
    int32_t num_free()  const;

    /**
     * Allocate `count` free pages. Returns empty optional on OOM.
     * Newly allocated pages start with ref_count=1.
     */
    std::optional<std::vector<int32_t>> allocate(int32_t count);

    /** Decrement ref_count; free when it reaches 0. */
    void free(const std::vector<int32_t>& page_ids);
    void free(int32_t page_id);

    void add_ref(int32_t page_id);
    void add_ref(const std::vector<int32_t>& page_ids);

    /** Bind a prefix hash to a page (for later lookup). */
    void set_prefix_hash(int32_t page_id, uint64_t hash);

    /**
     * Look up an existing page by prefix hash (cache hit).
     * On hit, increments ref_count and returns the page index.
     */
    std::optional<int32_t> lookup_prefix(uint64_t hash);

    /**
     * Compute rolling prefix hash for a contiguous token span.
     * FNV-1a over int32 tokens, seeded by previous hash (0 for first page).
     */
    static uint64_t hash_tokens(const int32_t* tokens, int32_t count, uint64_t seed = 0);

    /**
     * Compact free slots toward the end so free_list_ is dense.
     * Does not move live GPU data in STUB; real build would rematerialize KV.
     */
    void defrag();

    const Page* page(int32_t index) const;
    Page*       page(int32_t index);

private:
    int32_t page_size_;
    int32_t max_pages_;
    std::vector<Page> pages_;
    std::vector<int32_t> free_list_;
    std::unordered_map<uint64_t, int32_t> prefix_index_;
    mutable std::mutex mu_;
};

} // namespace exl
