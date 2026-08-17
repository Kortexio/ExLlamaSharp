#include "pagetable.h"

#include <algorithm>

namespace exl {

PageTable::PageTable(int32_t page_size, int32_t max_pages)
    : page_size_(page_size > 0 ? page_size : kDefaultPageSize)
    , max_pages_(max_pages > 0 ? max_pages : 1024)
{
    pages_.resize(static_cast<size_t>(max_pages_));
    free_list_.reserve(static_cast<size_t>(max_pages_));
    for (int32_t i = max_pages_ - 1; i >= 0; --i) {
        pages_[static_cast<size_t>(i)].index = i;
        free_list_.push_back(i);
    }
}

int32_t PageTable::num_used() const {
    std::lock_guard<std::mutex> lock(mu_);
    return max_pages_ - static_cast<int32_t>(free_list_.size());
}

int32_t PageTable::num_free() const {
    std::lock_guard<std::mutex> lock(mu_);
    return static_cast<int32_t>(free_list_.size());
}

std::optional<std::vector<int32_t>> PageTable::allocate(int32_t count) {
    if (count <= 0)
        return std::vector<int32_t>{};

    std::lock_guard<std::mutex> lock(mu_);
    if (static_cast<int32_t>(free_list_.size()) < count)
        return std::nullopt;

    std::vector<int32_t> out;
    out.reserve(static_cast<size_t>(count));
    for (int32_t i = 0; i < count; ++i) {
        const int32_t idx = free_list_.back();
        free_list_.pop_back();
        Page& p = pages_[static_cast<size_t>(idx)];
        p.allocated = true;
        p.ref_count = 1;
        p.prefix_hash = 0;
        p.on_gpu = true;
        out.push_back(idx);
    }
    return out;
}

void PageTable::free(int32_t page_id) {
    std::lock_guard<std::mutex> lock(mu_);
    if (page_id < 0 || page_id >= max_pages_)
        return;

    Page& p = pages_[static_cast<size_t>(page_id)];
    if (!p.allocated)
        return;

    if (--p.ref_count > 0)
        return;

    if (p.prefix_hash != 0) {
        auto it = prefix_index_.find(p.prefix_hash);
        if (it != prefix_index_.end() && it->second == page_id)
            prefix_index_.erase(it);
    }

    p.allocated = false;
    p.ref_count = 0;
    p.prefix_hash = 0;
    p.on_gpu = true;
    free_list_.push_back(page_id);
}

void PageTable::free(const std::vector<int32_t>& page_ids) {
    std::lock_guard<std::mutex> lock(mu_);
    for (int32_t page_id : page_ids) {
        if (page_id < 0 || page_id >= max_pages_)
            continue;

        Page& p = pages_[static_cast<size_t>(page_id)];
        if (!p.allocated)
            continue;

        if (--p.ref_count > 0)
            continue;

        if (p.prefix_hash != 0) {
            auto it = prefix_index_.find(p.prefix_hash);
            if (it != prefix_index_.end() && it->second == page_id)
                prefix_index_.erase(it);
        }

        p.allocated = false;
        p.ref_count = 0;
        p.prefix_hash = 0;
        p.on_gpu = true;
        free_list_.push_back(page_id);
    }
}

void PageTable::add_ref(int32_t page_id) {
    std::lock_guard<std::mutex> lock(mu_);
    if (page_id < 0 || page_id >= max_pages_)
        return;
    Page& p = pages_[static_cast<size_t>(page_id)];
    if (p.allocated)
        ++p.ref_count;
}

void PageTable::add_ref(const std::vector<int32_t>& page_ids) {
    std::lock_guard<std::mutex> lock(mu_);
    for (int32_t page_id : page_ids) {
        if (page_id < 0 || page_id >= max_pages_)
            continue;
        Page& p = pages_[static_cast<size_t>(page_id)];
        if (p.allocated)
            ++p.ref_count;
    }
}

void PageTable::set_prefix_hash(int32_t page_id, uint64_t hash) {
    std::lock_guard<std::mutex> lock(mu_);
    if (page_id < 0 || page_id >= max_pages_)
        return;
    Page& p = pages_[static_cast<size_t>(page_id)];
    if (!p.allocated)
        return;

    if (p.prefix_hash != 0) {
        auto it = prefix_index_.find(p.prefix_hash);
        if (it != prefix_index_.end() && it->second == page_id)
            prefix_index_.erase(it);
    }

    p.prefix_hash = hash;
    if (hash != 0)
        prefix_index_[hash] = page_id;
}

std::optional<int32_t> PageTable::lookup_prefix(uint64_t hash) {
    if (hash == 0)
        return std::nullopt;

    std::lock_guard<std::mutex> lock(mu_);
    auto it = prefix_index_.find(hash);
    if (it == prefix_index_.end())
        return std::nullopt;

    const int32_t idx = it->second;
    Page& p = pages_[static_cast<size_t>(idx)];
    if (!p.allocated || p.prefix_hash != hash) {
        prefix_index_.erase(it);
        return std::nullopt;
    }
    ++p.ref_count;
    return idx;
}

uint64_t PageTable::hash_tokens(const int32_t* tokens, int32_t count, uint64_t seed) {
    uint64_t h = seed ? seed : 14695981039346656037ull; // FNV offset basis
    for (int32_t i = 0; i < count; ++i) {
        const uint32_t v = static_cast<uint32_t>(tokens[i]);
        h ^= v;
        h *= 1099511628211ull; // FNV prime
    }
    return h;
}

void PageTable::defrag() {
    std::lock_guard<std::mutex> lock(mu_);
    // Compact free_list_ (unique + sorted ascending) so allocation prefers low indices.
    std::sort(free_list_.begin(), free_list_.end());
    free_list_.erase(std::unique(free_list_.begin(), free_list_.end()), free_list_.end());
    // Reverse so allocate() pops low indices first (LIFO -> prefer compacted front).
    std::reverse(free_list_.begin(), free_list_.end());
}

const PageTable::Page* PageTable::page(int32_t index) const {
    if (index < 0 || index >= max_pages_)
        return nullptr;
    return &pages_[static_cast<size_t>(index)];
}

PageTable::Page* PageTable::page(int32_t index) {
    if (index < 0 || index >= max_pages_)
        return nullptr;
    return &pages_[static_cast<size_t>(index)];
}

} // namespace exl
