﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FUR10N.NullContracts
{
    internal enum CacheItemPriority
    {
        Normal,
        High
    }

    internal class CacheProvider : IDisposable
    {
        private IDictionary<object, CacheItem> _cache = new Dictionary<object, CacheItem>();
        private IDictionary<object, SliderDetails> _slidingTime = new Dictionary<object, SliderDetails>();

        private const int _monitorWaitDefault = 1000;
        private const int _monitorWaitToUpdateSliding = 500;

        /// <summary>
        /// The sync object to make accessing the dictionary is thread safe.
        /// </summary>
        private readonly object _sync = new object();

        private readonly TimeSpan interval;

        private Timer timer;

        public event EventHandler KeyRemoved;

        public CacheProvider(TimeSpan interval)
        {
            this.interval = interval;
            timer = new Timer(TryPurgeItems, null, interval, interval);
        }

        /// <summary>
        /// Add a value to the cache with a relative expiry time, e.g 10 minutes.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <typeparam name="TValue">The type of the value.</typeparam>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="slidingExpiry">The sliding time when the key value pair should expire and be purged from the cache.</param>
        /// <param name="priority">Normal priority will be purged on low memory warning.</param>
        public void Add<TKey, TValue>(TKey key, TValue value, TimeSpan slidingExpiry, CacheItemPriority priority = CacheItemPriority.Normal) where TValue : class
        {
            Add(key, value, slidingExpiry, priority, true);
        }

        /// <summary>
        /// Add a value to the cache with an absolute time, e.g. 01/01/2020.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <typeparam name="TValue">The type of the value.</typeparam>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="absoluteExpiry">The absolute date time when the cache should expire and be purged the value.</param>
        /// <param name="priority">Normal priority will be purged on low memory warning.</param>
        public void Add<TKey, TValue>(TKey key, TValue value, DateTime absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal) where TValue : class
        {
            if (absoluteExpiry < DateTime.Now)
            {
                return;
            }

            var diff = absoluteExpiry - DateTime.Now;
            Add(key, value, diff, priority, false);
        }

        /// <summary>
        /// Gets a value from the cache for specified key.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <typeparam name="TValue">The type of the value.</typeparam>
        /// <param name="key">The key.</param>
        /// <returns>
        /// If the key exists in the cache then the value is returned, if the key does not exist then null is returned.
        /// </returns>
        public TValue Get<TKey, TValue>(TKey key) where TValue : class
        {
            if (_cache.TryGetValue(key, out var cacheItem))
            {
                if (cacheItem.RelativeExpiry.HasValue)
                {
                    if (Monitor.TryEnter(_sync, _monitorWaitToUpdateSliding))
                    {
                        try
                        {
                            _slidingTime[key].Viewed();
                        }
                        finally
                        {
                            Monitor.Exit(_sync);
                        }
                    }
                }

                return (TValue)cacheItem.Value;
            }
            return null;
        }

        /// <summary>
        /// Remove a value from the cache for specified key.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="key">The key.</param>
        public void Remove<TKey>(TKey key)
        {
            if (!Equals(key, null))
            {
                _cache.Remove(key);
                _slidingTime.Remove(key);

                if (KeyRemoved != null)
                {
                    KeyRemoved(key, new EventArgs());
                }
            }
        }

        /// <summary>
        /// Clears the contents of the cache.
        /// </summary>
        public void Clear()
        {
            if (!Monitor.TryEnter(_sync, _monitorWaitDefault))
            {
                return;
            }

            try
            {
                _cache.Clear();
                _slidingTime.Clear();
            }
            finally
            {
                Monitor.Exit(_sync);
            }
        }

        /// <summary>
        /// Gets an enumerator for keys of a specific type.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <returns>
        /// Returns an enumerator for keys of a specific type.
        /// </returns>
        public IEnumerable<TKey> Keys<TKey>()
        {
            if (!Monitor.TryEnter(_sync, _monitorWaitDefault))
            {
                return Enumerable.Empty<TKey>();
            }

            try
            {
                return _cache.Keys.Where(k => k.GetType() == typeof(TKey)).Cast<TKey>().ToList();
            }
            finally
            {
                Monitor.Exit(_sync);
            }
        }

        /// <summary>
        /// Gets an enumerator for all the keys
        /// </summary>
        /// <returns>
        /// Returns an enumerator for all the keys.
        /// </returns>
        public IEnumerable<object> Keys()
        {
            if (!Monitor.TryEnter(_sync, _monitorWaitDefault))
            {
                return Enumerable.Empty<object>();
            }

            try
            {
                return _cache.Keys.ToList();
            }
            finally
            {
                Monitor.Exit(_sync);
            }
        }

        /// <summary>
        /// Gets the total count of items in cache
        /// </summary>
        /// <returns>
        /// -1 if failed
        /// </returns>
        public int TotalItems()
        {
            if (!Monitor.TryEnter(_sync, _monitorWaitDefault))
            {
                return -1;
            }

            try
            {
                return _cache.Keys.Count;
            }
            finally
            {
                Monitor.Exit(_sync);
            }
        }

        /// <summary>
        /// Purges all cache item with normal priorities.
        /// </summary>
        /// <returns>
        /// Number of items removed (-1 if failed)
        /// </returns>
        public int PurgeNormalPriorities()
        {
            if (Monitor.TryEnter(_sync, _monitorWaitDefault))
            {
                try
                {
                    var keysToRemove = (from cacheItem in _cache where cacheItem.Value.Priority == CacheItemPriority.Normal select cacheItem.Key).ToList();

                    return keysToRemove.Count(key => _cache.Remove(key));
                }
                finally
                {
                    Monitor.Exit(_sync);
                }
            }

            return -1;
        }


        #region Private class helper

        private void Add<TKey, TValue>(TKey key, TValue value, TimeSpan timeSpan, CacheItemPriority priority, bool isSliding) where TValue : class
        {
            if (!Monitor.TryEnter(_sync, _monitorWaitDefault))
            {
                return;
            }

            try
            {
                // add to cache
                _cache.Add(key, new CacheItem(value, priority, ((isSliding) ? timeSpan : (TimeSpan?)null)));

                // keep sliding track
                if (isSliding)
                {
                    _slidingTime.Add(key, new SliderDetails(timeSpan));
                }
            }
            finally
            {
                Monitor.Exit(_sync);
            }
        }

        private void TryPurgeItems(object state)
        {
            if (Monitor.TryEnter(_sync, _monitorWaitDefault))
            {
                try
                {
                    var toRemove = new List<object>();
                    foreach (var entry in _slidingTime.ToList())
                    {
                        if (entry.Value.CanExpire(out var tryAfter))
                        {
                            Remove(entry.Key);
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(_sync);
                }
            }
        }

        public void Dispose()
        {
            timer.Dispose();
        }

        private class CacheItem
        {
            public CacheItem() { }

            public CacheItem(object value, CacheItemPriority priority, TimeSpan? relativeExpiry = null)
            {
                Value = value;
                Priority = priority;
                RelativeExpiry = relativeExpiry;
            }

            public object Value { get; set; }

            public CacheItemPriority Priority { get; set; }

            public TimeSpan? RelativeExpiry { get; set; }
        }


        private class SliderDetails
        {
            public SliderDetails(TimeSpan relativeExpiry)
            {
                RelativeExpiry = relativeExpiry;
                Viewed();
            }

            private TimeSpan RelativeExpiry { get; set; }

            private DateTime ExpireAt { get; set; }

            public bool CanExpire(out TimeSpan tryAfter)
            {
                tryAfter = (ExpireAt - DateTime.Now);
                return (0 > tryAfter.Ticks);
            }

            public void Viewed()
            {
                ExpireAt = DateTime.Now.Add(RelativeExpiry);
                var z = ExpireAt;
            }
        }

        #endregion
    }

#if PORTABLE
    public sealed class Timer : CancellationTokenSource, IDisposable
    {
        public Timer(Action<object> callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            System.Threading.Tasks.Task.Delay(dueTime, Token).ContinueWith(async (t, s) =>
            {
                var tuple = (Tuple<Action<object>, object>)s;

                while (true)
                {
                    if (IsCancellationRequested)
                        break;
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    System.Threading.Tasks.Task.Run(() => tuple.Item1(tuple.Item2));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    await System.Threading.Tasks.Task.Delay(period);
                }

            }, Tuple.Create(callback, state), CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously | System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion,
                System.Threading.Tasks.TaskScheduler.Default);
        }

        public new void Dispose() { base.Cancel(); }
        }
#endif
}
