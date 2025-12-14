using System;
using System.Collections.Generic;

namespace DominoGovernanceTracker.Core
{
    /// <summary>
    /// Thread-safe LRU (Least Recently Used) cache with bounded size
    /// No volatile keywords - uses lock for thread safety
    /// </summary>
    /// <typeparam name="TKey">Key type</typeparam>
    /// <typeparam name="TValue">Value type</typeparam>
    public class LruCache<TKey, TValue>
    {
        private readonly int _maxSize;
        private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cache;
        private readonly LinkedList<CacheItem> _lruList;
        private readonly object _lock = new object();

        public LruCache(int maxSize)
        {
            if (maxSize <= 0)
                throw new ArgumentException("Max size must be positive", nameof(maxSize));

            _maxSize = maxSize;
            _cache = new Dictionary<TKey, LinkedListNode<CacheItem>>(maxSize);
            _lruList = new LinkedList<CacheItem>();
        }

        /// <summary>
        /// Adds or updates a value in the cache
        /// </summary>
        public void Set(TKey key, TValue value)
        {
            lock (_lock)
            {
                // If key exists, update it and move to front
                if (_cache.TryGetValue(key, out var node))
                {
                    // Update value and move to front
                    _lruList.Remove(node);
                    _cache.Remove(key);

                    // Create new node (can't reuse removed node)
                    var updatedItem = new CacheItem { Key = key, Value = value };
                    var newNode = _lruList.AddFirst(updatedItem);
                    _cache[key] = newNode;
                    return;
                }

                // If at capacity, remove least recently used
                if (_cache.Count >= _maxSize)
                {
                    var lruNode = _lruList.Last;
                    if (lruNode != null)
                    {
                        _cache.Remove(lruNode.Value.Key);
                        _lruList.RemoveLast();
                    }
                }

                // Add new item to front (most recently used)
                var newItem = new CacheItem { Key = key, Value = value };
                var addedNode = _lruList.AddFirst(newItem);
                _cache[key] = addedNode;
            }
        }

        /// <summary>
        /// Tries to get a value from the cache
        /// </summary>
        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    // Move to front (mark as recently used)
                    _lruList.Remove(node);
                    _cache.Remove(key);

                    // Create new node (can't reuse removed node)
                    var item = new CacheItem { Key = node.Value.Key, Value = node.Value.Value };
                    var newNode = _lruList.AddFirst(item);
                    _cache[key] = newNode;

                    value = node.Value.Value;
                    return true;
                }

                value = default;
                return false;
            }
        }

        /// <summary>
        /// Gets current cache size
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _cache.Count;
                }
            }
        }

        /// <summary>
        /// Clears the cache
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
                _lruList.Clear();
            }
        }

        /// <summary>
        /// Removes entries matching a predicate (for cleanup on workbook close)
        /// </summary>
        public void RemoveWhere(Func<TKey, bool> predicate)
        {
            lock (_lock)
            {
                var keysToRemove = new List<TKey>();

                foreach (var kvp in _cache)
                {
                    if (predicate(kvp.Key))
                    {
                        keysToRemove.Add(kvp.Value.Value.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    if (_cache.TryGetValue(key, out var node))
                    {
                        _cache.Remove(key);
                        _lruList.Remove(node);
                    }
                }
            }
        }

        private class CacheItem
        {
            public TKey Key { get; set; }
            public TValue Value { get; set; }
        }
    }
}
