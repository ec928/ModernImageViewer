using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer
{
    /// <summary>
    /// Sole writer of the shared decode cache. Callers borrow via TryGet;
    /// extra holders (detached windows) must AddRef/Release.
    /// </summary>
    public static class ImageCache
    {
        private static readonly ConcurrentDictionary<string, ImageCacheEntry> Entries =
            new(StringComparer.OrdinalIgnoreCase);

        public static bool TryGet(string path, out ImageCacheEntry? entry)
        {
            if (string.IsNullOrEmpty(path))
            {
                entry = null;
                return false;
            }
            return Entries.TryGetValue(path, out entry);
        }

        public static bool Contains(string path) =>
            !string.IsNullOrEmpty(path) && Entries.ContainsKey(path);

        public static ICollection<string> Keys => Entries.Keys;

        /// <summary>
        /// Inserts <paramref name="entry"/> as the cache-owned instance (its initial refcount).
        /// Releases any different previous occupant. Will not replace a high-fidelity
        /// entry with a preview — the incoming entry is Released instead.
        /// </summary>
        public static bool HasUsableDecode(string path) =>
            TryGet(path, out var existing) && existing != null && existing.HasDecodableContent;

        public static void Publish(string path, ImageCacheEntry entry)
        {
            if (string.IsNullOrEmpty(path) || entry == null) return;

            while (true)
            {
                if (Entries.TryGetValue(path, out var existing))
                {
                    if (ReferenceEquals(existing, entry)) return;

                    if (existing.IsHighFidelity && !entry.IsHighFidelity)
                    {
                        entry.Release();
                        return;
                    }

                    if (!Entries.TryRemove(path, out var removed))
                        continue;

                    if (removed != null && !ReferenceEquals(removed, entry))
                        removed.Release();
                }

                if (Entries.TryAdd(path, entry))
                    return;
            }
        }

        public static void Evict(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (Entries.TryRemove(path, out var entry) && entry != null)
                entry.Release();
        }

        public static void EvictWhere(Func<string, bool> shouldEvict)
        {
            foreach (var key in Entries.Keys.Where(shouldEvict).ToList())
                Evict(key);
        }

        public static void Rename(string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
            if (oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase)) return;

            if (!Entries.TryRemove(oldPath, out var entry) || entry == null) return;

            if (Entries.TryRemove(newPath, out var displaced) && displaced != null && !ReferenceEquals(displaced, entry))
                displaced.Release();

            Entries[newPath] = entry;
        }

        public static void Clear()
        {
            foreach (var key in Entries.Keys.ToList())
                Evict(key);
        }
    }
}
