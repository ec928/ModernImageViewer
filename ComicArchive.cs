using SharpCompress.Archives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace ModernImageViewer
{
    public sealed class ComicPageInfo
    {
        public string EntryKey { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public long Size { get; init; }
        public DateTime LastModified { get; init; }
    }

    public static class ComicArchive
    {
        public const string EntrySeparator = "::|::";

        private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cbz", ".cbr"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif"
        };

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> ArchiveLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, List<ComicPageInfo>> PageListCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly object ByteCacheGate = new();
        private static readonly Dictionary<string, byte[]> ByteCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> ByteCacheOrder = new();
        private static long _byteCacheTotalBytes;
        private const long MaxByteCacheBytes = 96L * 1024 * 1024; // 96 MB LRU for fast spread/thumbnail access

        public static bool IsComicArchivePath(string? path)
        {
            if (string.IsNullOrEmpty(path) || IsEntryPath(path)) return false;
            return ArchiveExtensions.Contains(Path.GetExtension(path));
        }

        public static bool IsEntryPath(string? path)
        {
            return !string.IsNullOrEmpty(path) && path.Contains(EntrySeparator, StringComparison.Ordinal);
        }

        public static string BuildEntryPath(string archivePath, string entryKey)
        {
            return $"{archivePath}{EntrySeparator}{entryKey}";
        }

        public static bool TrySplitEntryPath(string? path, out string archivePath, out string entryKey)
        {
            archivePath = string.Empty;
            entryKey = string.Empty;
            if (string.IsNullOrEmpty(path)) return false;
            int idx = path.IndexOf(EntrySeparator, StringComparison.Ordinal);
            if (idx <= 0) return false;
            archivePath = path.Substring(0, idx);
            entryKey = path.Substring(idx + EntrySeparator.Length);
            return !string.IsNullOrEmpty(archivePath) && !string.IsNullOrEmpty(entryKey);
        }

        private static bool IsValidImageEntry(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            string normalized = key.Replace('\\', '/');
            if (normalized.EndsWith('/')) return false;
            if (normalized.Contains("__MACOSX/", StringComparison.OrdinalIgnoreCase)) return false;
            string fileName = Path.GetFileName(normalized);
            if (string.IsNullOrEmpty(fileName) || fileName.StartsWith('.')) return false;
            return ImageExtensions.Contains(Path.GetExtension(fileName));
        }

        private static bool IsZipFileHeader(FileStream fs)
        {
            if (fs.Length < 4) return false;
            fs.Position = 0;
            int b0 = fs.ReadByte();
            int b1 = fs.ReadByte();
            int b2 = fs.ReadByte();
            int b3 = fs.ReadByte();
            fs.Position = 0;
            return b0 == 'P' && b1 == 'K' && b2 == 3 && b3 == 4;
        }

        public static List<ComicPageInfo> ListPages(string archivePath)
        {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
                return [];

            if (PageListCache.TryGetValue(archivePath, out var cached) && cached.Count > 0)
                return cached;

            var pages = new List<ComicPageInfo>();
            try
            {
                using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                DateTime fallbackDate = File.GetLastWriteTime(archivePath);

                if (IsZipFileHeader(fs))
                {
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
                    foreach (var entry in zip.Entries)
                    {
                        if (!IsValidImageEntry(entry.FullName) || entry.Length <= 0) continue;
                        pages.Add(new ComicPageInfo
                        {
                            EntryKey = entry.FullName,
                            Name = Path.GetFileName(entry.FullName.Replace('\\', '/')),
                            Size = entry.Length,
                            LastModified = entry.LastWriteTime.Year > 1980 ? entry.LastWriteTime.LocalDateTime : fallbackDate
                        });
                    }
                }
                else
                {
                    using var archive = ArchiveFactory.OpenArchive(fs);
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.IsDirectory || !IsValidImageEntry(entry.Key) || entry.Size <= 0) continue;
                        string key = entry.Key!;
                        pages.Add(new ComicPageInfo
                        {
                            EntryKey = key,
                            Name = Path.GetFileName(key.Replace('\\', '/')),
                            Size = entry.Size,
                            LastModified = entry.LastModifiedTime ?? fallbackDate
                        });
                    }
                }

                pages.Sort((a, b) => NaturalSort.Compare(a.EntryKey.Replace('\\', '/'), b.EntryKey.Replace('\\', '/')));
                if (pages.Count > 0)
                    PageListCache[archivePath] = pages;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ComicArchive.ListPages] {archivePath}: {ex.Message}");
            }
            return pages;
        }

        public static async Task<byte[]?> ReadEntryBytesAsync(string entryOrArchivePath, CancellationToken token = default)
        {
            if (TrySplitEntryPath(entryOrArchivePath, out string archivePath, out string entryKey))
                return await Task.Run(() => ReadEntryBytesCore(archivePath, entryKey, token), token);

            if (IsComicArchivePath(entryOrArchivePath))
            {
                return await Task.Run(() =>
                {
                    var pages = ListPages(entryOrArchivePath);
                    if (pages.Count == 0 || token.IsCancellationRequested) return null;
                    return ReadEntryBytesCore(entryOrArchivePath, pages[0].EntryKey, token);
                }, token);
            }

            return null;
        }

        public static async Task<IRandomAccessStream?> OpenEntryStreamAsync(string entryOrArchivePath, CancellationToken token = default)
        {
            byte[]? bytes = await ReadEntryBytesAsync(entryOrArchivePath, token);
            if (bytes == null || bytes.Length == 0 || token.IsCancellationRequested) return null;
            var ms = new MemoryStream(bytes, writable: false);
            return ms.AsRandomAccessStream();
        }

        private static byte[]? ReadEntryBytesCore(string archivePath, string entryKey, CancellationToken token)
        {
            string fullKey = BuildEntryPath(archivePath, entryKey);
            if (TryGetCachedBytes(fullKey, out var cachedBytes))
                return cachedBytes;

            if (token.IsCancellationRequested || !File.Exists(archivePath)) return null;

            var gate = ArchiveLocks.GetOrAdd(archivePath, _ => new SemaphoreSlim(2, 2));
            gate.Wait(token);
            try
            {
                if (TryGetCachedBytes(fullKey, out cachedBytes))
                    return cachedBytes;

                using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (IsZipFileHeader(fs))
                {
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
                    var entry = zip.GetEntry(entryKey)
                                ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, entryKey, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return null;
                    using var entryStream = entry.Open();
                    using var ms = new MemoryStream((int)Math.Clamp(entry.Length, 4096, 64 * 1024 * 1024));
                    entryStream.CopyTo(ms);
                    var bytes = ms.ToArray();
                    StoreCachedBytes(fullKey, bytes);
                    return bytes;
                }
                else
                {
                    using var archive = ArchiveFactory.OpenArchive(fs);
                    var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Key, entryKey, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return null;
                    using var entryStream = entry.OpenEntryStream();
                    using var ms = new MemoryStream((int)Math.Clamp(entry.Size, 4096, 64 * 1024 * 1024));
                    entryStream.CopyTo(ms);
                    var bytes = ms.ToArray();
                    StoreCachedBytes(fullKey, bytes);
                    return bytes;
                }
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ComicArchive.ReadEntry] {fullKey}: {ex.Message}");
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        private static bool TryGetCachedBytes(string key, out byte[]? bytes)
        {
            lock (ByteCacheGate)
            {
                if (ByteCache.TryGetValue(key, out bytes))
                {
                    ByteCacheOrder.Remove(key);
                    ByteCacheOrder.AddLast(key);
                    return true;
                }
                return false;
            }
        }

        private static void StoreCachedBytes(string key, byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > 32 * 1024 * 1024) return;
            lock (ByteCacheGate)
            {
                if (ByteCache.TryGetValue(key, out var existing))
                {
                    _byteCacheTotalBytes -= existing.Length;
                    ByteCacheOrder.Remove(key);
                }

                while (_byteCacheTotalBytes + bytes.Length > MaxByteCacheBytes && ByteCacheOrder.First != null)
                {
                    string oldest = ByteCacheOrder.First.Value;
                    ByteCacheOrder.RemoveFirst();
                    if (ByteCache.Remove(oldest, out var evicted))
                        _byteCacheTotalBytes -= evicted.Length;
                }

                ByteCache[key] = bytes;
                ByteCacheOrder.AddLast(key);
                _byteCacheTotalBytes += bytes.Length;
            }
        }
    }
}
