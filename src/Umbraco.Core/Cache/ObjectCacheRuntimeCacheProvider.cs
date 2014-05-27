﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Caching;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Caching;
using Umbraco.Core.Logging;
using CacheItemPriority = System.Web.Caching.CacheItemPriority;

namespace Umbraco.Core.Cache
{
    /// <summary>
    /// A cache provider that wraps the logic of a System.Runtime.Caching.ObjectCache
    /// </summary>
    internal class ObjectCacheRuntimeCacheProvider : IRuntimeCacheProvider
    {
        private readonly ReaderWriterLockSlim _locker = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        internal ObjectCache MemoryCache;

        public ObjectCacheRuntimeCacheProvider()
        {
            MemoryCache = new MemoryCache("in-memory");
        }

        #region Clear

        public virtual void ClearAllCache()
        {
            using (new WriteLock(_locker))
            {
                MemoryCache.DisposeIfDisposable();
                MemoryCache = new MemoryCache("in-memory");
            }
        }

        public virtual void ClearCacheItem(string key)
        {
            using (new WriteLock(_locker))
            {
                if (MemoryCache[key] == null) return;
                MemoryCache.Remove(key);
            }            
        }

        public virtual void ClearCacheObjectTypes(string typeName)
        {
            using (new WriteLock(_locker))
            {
                foreach (var key in MemoryCache
                    .Where(x =>
                    {
                        // x.Value is Lazy<object> and not null, its value may be null
                        // remove null values as well, does not hurt
                        var value = ((Lazy<object>) x.Value).Value;
                        return value == null || value.GetType().ToString().InvariantEquals(typeName);
                    })
                    .Select(x => x.Key)
                    .ToArray()) // ToArray required to remove
                    MemoryCache.Remove(key);
            }
        }

        public virtual void ClearCacheObjectTypes<T>()
        {
            using (new WriteLock(_locker))
            {
                var typeOfT = typeof (T);
                foreach (var key in MemoryCache
                    .Where(x =>
                    {
                        // x.Value is Lazy<object> and not null, its value may be null
                        // remove null values as well, does not hurt
                        var value = ((Lazy<object>)x.Value).Value;
                        return value == null || value.GetType() == typeOfT;
                    })
                    .Select(x => x.Key)
                    .ToArray()) // ToArray required to remove
                    MemoryCache.Remove(key);
            }
        }

        public virtual void ClearCacheObjectTypes<T>(Func<string, T, bool> predicate)
        {
            using (new WriteLock(_locker))
            {
                var typeOfT = typeof(T);
                foreach (var key in MemoryCache
                    .Where(x =>
                    {
                        // x.Value is Lazy<object> and not null, its value may be null
                        // remove null values as well, does not hurt
                        var value = ((Lazy<object>)x.Value).Value;
                        if (value == null) return true;
                        return value.GetType() == typeOfT
                            && predicate(x.Key, (T) value);
                    })
                    .Select(x => x.Key)
                    .ToArray()) // ToArray required to remove
                    MemoryCache.Remove(key);
            }
        }

        public virtual void ClearCacheByKeySearch(string keyStartsWith)
        {
            using (new WriteLock(_locker))
            {
                foreach (var key in MemoryCache
                    .Where(x => x.Key.InvariantStartsWith(keyStartsWith))
                    .Select(x => x.Key)
                    .ToArray()) // ToArray required to remove
                    MemoryCache.Remove(key);
            }            
        }

        public virtual void ClearCacheByKeyExpression(string regexString)
        {
            using (new WriteLock(_locker))
            {
                foreach (var key in MemoryCache
                    .Where(x => Regex.IsMatch(x.Key, regexString))
                    .Select(x => x.Key)
                    .ToArray()) // ToArray required to remove
                    MemoryCache.Remove(key);
            }     
        }

        #endregion

        #region Get

        public IEnumerable<object> GetCacheItemsByKeySearch(string keyStartsWith)
        {
            using (new ReadLock(_locker))
            {
                return MemoryCache
                    .Where(x => x.Key.InvariantStartsWith(keyStartsWith))
                    .Select(x => ((Lazy<object>) x.Value).Value)
                    .Where(x => x != null) // backward compat, don't store null values in the cache
                    .ToList();
            }
        }

        public IEnumerable<object> GetCacheItemsByKeyExpression(string regexString)
        {
            using (new ReadLock(_locker))
            {
                return MemoryCache
                    .Where(x => Regex.IsMatch(x.Key, regexString))
                    .Select(x => ((Lazy<object>) x.Value).Value)
                    .Where(x => x != null) // backward compat, don't store null values in the cache
                    .ToList();
            }
        }

        public object GetCacheItem(string cacheKey)
        {
            using (new ReadLock(_locker))
            {
                var result = MemoryCache.Get(cacheKey) as Lazy<object>;
                return result == null ? null : result.Value;
            }
        }

        public object GetCacheItem(string cacheKey, Func<object> getCacheItem)
        {
            return GetCacheItem(cacheKey, getCacheItem, null);
        }

        public object GetCacheItem(string cacheKey, Func<object> getCacheItem, TimeSpan? timeout, bool isSliding = false, CacheItemPriority priority = CacheItemPriority.Normal,CacheItemRemovedCallback removedCallback = null, string[] dependentFiles = null)
        {
            // see notes in HttpRuntimeCacheProvider

            Lazy<object> result;

            using (var lck = new UpgradeableReadLock(_locker))
            {
                result = MemoryCache.Get(cacheKey) as Lazy<object>;
                if (result == null || (result.IsValueCreated && result.Value == null))
                {
                    lck.UpgradeToWriteLock();

                    result = new Lazy<object>(getCacheItem);
                    var policy = GetPolicy(timeout, isSliding, removedCallback, dependentFiles);
                    MemoryCache.Set(cacheKey, result, policy);
                }
            }

            return result.Value;
        }

        #endregion

        #region Insert

        public void InsertCacheItem(string cacheKey, Func<object> getCacheItem, TimeSpan? timeout = null, bool isSliding = false, CacheItemPriority priority = CacheItemPriority.Normal, CacheItemRemovedCallback removedCallback = null, string[] dependentFiles = null)
        {
            // NOTE - here also we must insert a Lazy<object> but we can evaluate it right now
            // and make sure we don't store a null value.

            var result = new Lazy<object>(getCacheItem);
            var value = result.Value; // force evaluation now
            if (value == null) return; // do not store null values (backward compat)

            var policy = GetPolicy(timeout, isSliding, removedCallback, dependentFiles);
            MemoryCache.Set(cacheKey, result, policy);
        }

        #endregion

        private static CacheItemPolicy GetPolicy(TimeSpan? timeout = null, bool isSliding = false, CacheItemRemovedCallback removedCallback = null, string[] dependentFiles = null)
        {
            var absolute = isSliding ? ObjectCache.InfiniteAbsoluteExpiration : (timeout == null ? ObjectCache.InfiniteAbsoluteExpiration : DateTime.Now.Add(timeout.Value));
            var sliding = isSliding == false ? ObjectCache.NoSlidingExpiration : (timeout ?? ObjectCache.NoSlidingExpiration);

            var policy = new CacheItemPolicy
            {
                AbsoluteExpiration = absolute,
                SlidingExpiration = sliding
            };

            if (dependentFiles != null && dependentFiles.Any())
            {
                policy.ChangeMonitors.Add(new HostFileChangeMonitor(dependentFiles.ToList()));
            }
            
            if (removedCallback != null)
            {
                policy.RemovedCallback = arguments =>
                {
                    //convert the reason
                    var reason = CacheItemRemovedReason.Removed;
                    switch (arguments.RemovedReason)
                    {
                        case CacheEntryRemovedReason.Removed:
                            reason = CacheItemRemovedReason.Removed;
                            break;
                        case CacheEntryRemovedReason.Expired:
                            reason = CacheItemRemovedReason.Expired;
                            break;
                        case CacheEntryRemovedReason.Evicted:
                            reason = CacheItemRemovedReason.Underused;
                            break;
                        case CacheEntryRemovedReason.ChangeMonitorChanged:
                            reason = CacheItemRemovedReason.Expired;
                            break;
                        case CacheEntryRemovedReason.CacheSpecificEviction:
                            reason = CacheItemRemovedReason.Underused;
                            break;
                    }
                    //call the callback
                    removedCallback(arguments.CacheItem.Key, arguments.CacheItem.Value, reason);
                };
            }
            return policy;
        }
        
    }
}