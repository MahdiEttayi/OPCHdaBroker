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
    public class MemoryCache
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _store
            = new ConcurrentDictionary<string, CacheEntry>();

        private class CacheEntry
        {
            public object   Value     { get; set; }
            public DateTime ExpiresAt { get; set; }
        }

        public T GetOrAdd<T>(string key, Func<T> factory, TimeSpan ttl)
        {
            var lazy = new Lazy<T>(factory);
            var entry = _store.AddOrUpdate(key,
                _ => new CacheEntry
                {
                    Value     = lazy.Value,
                    ExpiresAt = DateTime.UtcNow.Add(ttl)
                },
                (_, existing) => DateTime.UtcNow < existing.ExpiresAt
                    ? existing
                    : new CacheEntry
                    {
                        Value     = lazy.Value,
                        ExpiresAt = DateTime.UtcNow.Add(ttl)
                    });
            return (T)entry.Value;
        }

        public void Invalidate(string key)
        {
            _store.TryRemove(key, out _);
        }

        public void Clear()
        {
            _store.Clear();
        }
    }
}
