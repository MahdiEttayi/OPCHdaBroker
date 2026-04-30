// ═══════════════════════════════════════════════════════════════════════════
// IN-MEMORY CACHE
// ───────────────────────────────────────────────────────────────────────────
// Simple TTL-based cache for tag lists and other infrequently-changing data.
// No external dependencies — uses ConcurrentDictionary + expiry timestamps.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;

namespace OpcHdaBroker.Cache
{
    /// <summary>
    /// Thread-safe in-memory cache with per-entry TTL expiration.
    /// Used to avoid re-browsing the tag namespace on every request.
    /// </summary>
    public class MemoryCache
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _store
            = new ConcurrentDictionary<string, CacheEntry>();

        private class CacheEntry
        {
            public object   Value     { get; set; }
            public DateTime ExpiresAt { get; set; }
        }

        /// <summary>
        /// Get a cached value, or compute and cache it if missing/expired.
        /// </summary>
        public T GetOrAdd<T>(string key, Func<T> factory, TimeSpan ttl)
        {
            if (_store.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
            {
                return (T)entry.Value;
            }

            T value = factory();
            _store[key] = new CacheEntry
            {
                Value     = value,
                ExpiresAt = DateTime.UtcNow.Add(ttl)
            };
            return value;
        }

        /// <summary>
        /// Invalidate a specific cache entry.
        /// </summary>
        public void Invalidate(string key)
        {
            _store.TryRemove(key, out _);
        }

        /// <summary>
        /// Clear all cached entries.
        /// </summary>
        public void Clear()
        {
            _store.Clear();
        }
    }
}
