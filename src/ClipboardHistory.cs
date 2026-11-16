using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace Orbit
{
    internal sealed class ClipboardEntry
    {
        public string Id { get; set; }
        public string Text { get; set; }
        // Display metadata only. Never used to construct a filesystem path.
        public string FileName { get; set; }
        public byte[] ImagePng { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
        public bool IsImage { get { return ImagePng != null && ImagePng.Length != 0; } }
    }

    /// <summary>
    /// The three latest clipboard captures. This class never accesses the system
    /// clipboard: the monitor supplies captures and the UI requests promotions.
    /// </summary>
    internal sealed class ClipboardHistory
    {
        private const int Capacity = 3;
        // Keep capture limits aligned with ClipboardMonitor, while allowing the
        // persistence class to be built and tested without desktop dependencies.
        private const int MaximumImageBytes = 32 * 1024 * 1024;
        private const long MaximumImagePixels = 24L * 1024 * 1024;
        private const string MetadataName = "clipboard-history.json";
        private const string CacheName = "clipboard-history-images";
        private static readonly Regex CacheFilePattern = new Regex(@"\A[0-9a-f]{32}\.png\z", RegexOptions.CultureInvariant);
        private readonly object gate = new object();
        private readonly string directory;
        private readonly string metadataPath;
        private readonly string cacheDirectory;
        private List<Record> records = new List<Record>();
        private bool savingBlocked;

        public ClipboardHistory(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A data directory is required.", "directory");
            this.directory = Path.GetFullPath(directory);
            metadataPath = Path.Combine(this.directory, MetadataName);
            cacheDirectory = Path.Combine(this.directory, CacheName);
            Load();
        }

        public IReadOnlyList<ClipboardEntry> Entries
        {
            get
            {
                lock (gate) return records.Select(record => record.Entry).ToList().AsReadOnly();
            }
        }

        public event EventHandler Changed;
        public string LastError { get; private set; }

        /// <summary>Every external capture creates a new entry, even for identical content.</summary>
        public bool Add(ClipboardEntry entry)
        {
            bool saved;
            lock (gate)
            {
                if (!CanSave()) return false;
                Record added;
                try { added = Normalize(entry); }
                catch (Exception error)
                {
                    if (IsFatal(error)) throw;
                    LastError = "这次剪贴板内容未能保存：" + error.Message;
                    return false;
                }
                var next = new List<Record> { added };
                next.AddRange(records.Take(Capacity - 1));
                saved = Commit(next);
            }
            if (saved) NotifyChanged();
            return saved;
        }

        /// <summary>Taking a history item moves the existing record; it is not a new capture.</summary>
        public bool Promote(string id)
        {
            bool saved;
            lock (gate)
            {
                if (!CanSave()) return false;
                int index = records.FindIndex(record => record.Entry.Id == id);
                if (index < 0) { LastError = "找不到这条剪贴板记录。"; return false; }
                if (index == 0) return true;
                var next = new List<Record>(records);
                Record item = next[index];
                next.RemoveAt(index);
                next.Insert(0, item);
                saved = Commit(next);
            }
            if (saved) NotifyChanged();
            return saved;
        }

        public bool Remove(string id)
        {
            bool saved;
            lock (gate)
            {
                if (!CanSave()) return false;
                int index = records.FindIndex(record => record.Entry.Id == id);
                if (index < 0) { LastError = "找不到这条剪贴板记录。"; return false; }
                var next = new List<Record>(records);
                next.RemoveAt(index);
                saved = Commit(next);
            }
            if (saved) NotifyChanged();
            return saved;
        }

        private Record Normalize(ClipboardEntry input)
        {
            if (input == null) throw new ArgumentException("剪贴板内容为空。", "input");
            bool image = input.ImagePng != null && input.ImagePng.Length != 0;
            if (!image && String.IsNullOrEmpty(input.Text)) throw new ArgumentException("剪贴板内容为空。", "input");
            string id = input.Id;
            if (String.IsNullOrWhiteSpace(id) || records.Any(record => record.Entry.Id == id)) id = Guid.NewGuid().ToString("N");
            var entry = new ClipboardEntry
            {
                Id = id,
                Text = input.Text,
                FileName = input.FileName,
                CapturedAtUtc = input.CapturedAtUtc == default(DateTimeOffset) ? DateTimeOffset.UtcNow : input.CapturedAtUtc.ToUniversalTime()
            };
            string imageFile = null;
            if (image)
            {
                if (input.ImagePng.Length > MaximumImageBytes) throw new InvalidDataException("图片超过 32 MB，暂不记录。");
                entry.ImagePng = (byte[])input.ImagePng.Clone();
                int width, height;
                ReadPngDimensions(entry.ImagePng, out width, out height);
                entry.PixelWidth = width;
                entry.PixelHeight = height;
                imageFile = Guid.NewGuid().ToString("N") + ".png";
            }
            return new Record { Entry = entry, ImageFile = imageFile };
        }

        private void Load()
        {
            try
            {
                if (Directory.Exists(metadataPath)) throw new IOException("历史数据路径被文件夹占用。 ");
                if (File.Exists(metadataPath))
                {
                    HistoryData data;
                    using (var input = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("历史记录文件过大。 ");
                        data = new DataContractJsonSerializer(typeof(HistoryData)).ReadObject(input) as HistoryData;
                    }
                    if (data == null || data.Version != 1 || data.Entries == null || data.Entries.Count > Capacity)
                        throw new InvalidDataException("历史记录格式无法识别。 ");
                    var restored = new List<Record>();
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (StoredEntry stored in data.Entries)
                    {
                        if (stored == null || String.IsNullOrWhiteSpace(stored.Id) || !ids.Add(stored.Id))
                            throw new InvalidDataException("历史记录包含无效编号。 ");
                        var entry = new ClipboardEntry
                        {
                            Id = stored.Id,
                            Text = stored.Text,
                            FileName = stored.FileName,
                            CapturedAtUtc = stored.CapturedAtUtc.ToUniversalTime()
                        };
                        if (!String.IsNullOrEmpty(stored.ImageFile))
                        {
                            string imagePath = CachePath(stored.ImageFile);
                            RejectLinkedCacheFile(imagePath);
                            entry.ImagePng = ReadCachedPng(imagePath);
                            int width, height;
                            ReadPngDimensions(entry.ImagePng, out width, out height);
                            entry.PixelWidth = width;
                            entry.PixelHeight = height;
                        }
                        else if (String.IsNullOrEmpty(entry.Text)) throw new InvalidDataException("历史记录的内容为空。 ");
                        restored.Add(new Record { Entry = entry, ImageFile = stored.ImageFile });
                    }
                    records = restored;
                    return;
                }
            }
            catch (Exception error)
            {
                if (IsFatal(error)) throw;
                savingBlocked = true;
                LastError = "剪贴板历史读取失败，原文件已保留；已暂停覆盖保存。" + error.Message;
                return;
            }
            MigrateLegacySlots();
        }

        private void MigrateLegacySlots()
        {
            string legacyPath = Path.Combine(directory, "clipboard-slots.json");
            if (!File.Exists(legacyPath)) return;
            try
            {
                LegacySlots legacy;
                using (var input = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    legacy = new DataContractJsonSerializer(typeof(LegacySlots)).ReadObject(input) as LegacySlots;
                if (legacy == null || legacy.Items == null || legacy.Items.Length != 2)
                    throw new InvalidDataException("旧槽位格式无法识别。 ");
                DateTimeOffset savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(legacyPath));
                foreach (string text in legacy.Items)
                {
                    if (String.IsNullOrEmpty(text)) continue;
                    records.Add(new Record { Entry = new ClipboardEntry { Id = Guid.NewGuid().ToString("N"), Text = text, CapturedAtUtc = savedAt } });
                }
                // Keep these readable legacy records in memory if migration cannot
                // yet be written; the untouched legacy file remains their source.
                Commit(new List<Record>(records));
            }
            catch (Exception error)
            {
                if (IsFatal(error)) throw;
                LastError = "旧剪贴板槽位读取失败，原文件已保留。" + error.Message;
            }
        }

        private bool Commit(List<Record> next)
        {
            string temporaryMetadata = Path.Combine(directory, ".orbit-history-" + Guid.NewGuid().ToString("N") + ".tmp");
            var newlyCreatedImages = new List<string>();
            var oldImages = new HashSet<string>(records.Where(record => record.ImageFile != null).Select(record => record.ImageFile), StringComparer.Ordinal);
            bool committed = false;
            try
            {
                Directory.CreateDirectory(directory);
                foreach (Record record in next)
                {
                    if (!record.Entry.IsImage) continue;
                    string path = CachePath(record.ImageFile);
                    if (File.Exists(path)) { RejectLinkedCacheFile(path); continue; }
                    Directory.CreateDirectory(cacheDirectory);
                    WriteImageAtomically(path, record.Entry.ImagePng);
                    // Only a completed move establishes ownership. A failed move
                    // must never cause cleanup of a conflicting existing file.
                    if (!oldImages.Contains(record.ImageFile)) newlyCreatedImages.Add(path);
                }
                var data = new HistoryData { Version = 1, Entries = next.Select(ToStoredEntry).ToList() };
                using (var output = new FileStream(temporaryMetadata, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    new DataContractJsonSerializer(typeof(HistoryData)).WriteObject(output, data);
                    output.Flush(true);
                }
                if (File.Exists(metadataPath)) File.Replace(temporaryMetadata, metadataPath, null);
                else File.Move(temporaryMetadata, metadataPath);
                committed = true;
            }
            catch (Exception error)
            {
                if (IsFatal(error)) throw;
                LastError = "剪贴板历史保存失败，上一份记录已保留。" + error.Message;
                return false;
            }
            finally
            {
                DeleteTemporary(temporaryMetadata);
                if (!committed) foreach (string path in newlyCreatedImages) DeleteTemporary(path);
            }

            records = next;
            LastError = null;
            var retained = new HashSet<string>(next.Where(record => record.ImageFile != null).Select(record => record.ImageFile), StringComparer.Ordinal);
            foreach (string file in oldImages)
            {
                if (retained.Contains(file)) continue;
                try
                {
                    string path = CachePath(file);
                    if (File.Exists(path)) { RejectLinkedCacheFile(path); File.Delete(path); }
                }
                catch (Exception error)
                {
                    if (IsFatal(error)) throw;
                    LastError = "历史已保存，但旧图片缓存暂未清理。" + error.Message;
                }
            }
            return true;
        }

        private string CachePath(string fileName)
        {
            if (fileName == null || !CacheFilePattern.IsMatch(fileName)) throw new InvalidDataException("图片缓存文件名不正确。 ");
            if (Directory.Exists(cacheDirectory) && (File.GetAttributes(cacheDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("图片缓存目录不能是链接。 ");
            string path = Path.GetFullPath(Path.Combine(cacheDirectory, fileName));
            if (!String.Equals(Path.GetDirectoryName(path), cacheDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("图片缓存路径越界。 ");
            return path;
        }

        private static void RejectLinkedCacheFile(string path)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("图片缓存文件不能是链接。 ");
        }

        private static void WriteImageAtomically(string path, byte[] bytes)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(path), ".orbit-image-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(true);
                }
                // Cache names are unique. Never replace an unrelated existing file.
                File.Move(temporary, path);
            }
            finally { DeleteTemporary(temporary); }
        }

        private static void ReadPngDimensions(byte[] bytes, out int width, out int height)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes == null || bytes.Length < 33 || !signature.SequenceEqual(bytes.Take(signature.Length)))
                throw new InvalidDataException("图片不是有效的 PNG。 ");
            if (bytes.Length > MaximumImageBytes) throw new InvalidDataException("图片超过 32 MB，暂不记录。");
            // PNG's first chunk must be its 13-byte IHDR. Check dimensions before
            // asking GDI+ to allocate a potentially enormous decoded bitmap.
            if (ReadBigEndian(bytes, 8) != 13 || bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82)
                throw new InvalidDataException("PNG 图片头无法识别。 ");
            long headerWidth = ReadBigEndian(bytes, 16), headerHeight = ReadBigEndian(bytes, 20);
            if (headerWidth < 1 || headerHeight < 1 || headerWidth > 16384 || headerHeight > 16384 || headerWidth * headerHeight > MaximumImagePixels)
                throw new InvalidDataException("图片尺寸过大或无效，暂不记录。 ");
            try
            {
                using (var input = new MemoryStream(bytes, false))
                using (Image image = Image.FromStream(input, false, true))
                {
                    if (image.RawFormat.Guid != ImageFormat.Png.Guid || image.Width != headerWidth || image.Height != headerHeight)
                        throw new InvalidDataException("图片不是有效的 PNG。 ");
                    width = image.Width;
                    height = image.Height;
                }
            }
            catch (OutOfMemoryException error)
            {
                // GDI+ uses this exception for invalid image data as well as
                // allocation failures. Treat either decode failure as bad input.
                throw new InvalidDataException("PNG 图片未能解码，原记录未更改。", error);
            }
        }

        private static byte[] ReadCachedPng(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 1 || input.Length > MaximumImageBytes)
                    throw new InvalidDataException("图片缓存大小无效或超过 32 MB。 ");
                byte[] bytes = new byte[(int)input.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = input.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException("图片缓存读取不完整。 ");
                    offset += read;
                }
                return bytes;
            }
        }

        private static long ReadBigEndian(byte[] bytes, int offset)
        {
            return ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16) |
                ((long)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static StoredEntry ToStoredEntry(Record record)
        {
            return new StoredEntry
            {
                Id = record.Entry.Id, Text = record.Entry.Text, FileName = record.Entry.FileName,
                ImageFile = record.ImageFile, PixelWidth = record.Entry.PixelWidth, PixelHeight = record.Entry.PixelHeight,
                CapturedAtUtc = record.Entry.CapturedAtUtc
            };
        }

        private bool CanSave()
        {
            if (!savingBlocked) return true;
            if (String.IsNullOrEmpty(LastError)) LastError = "现有历史未能完整读取，已暂停覆盖保存以保护原文件。";
            return false;
        }

        private void NotifyChanged()
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static void DeleteTemporary(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool IsFatal(Exception error)
        {
            return error is OutOfMemoryException || error is StackOverflowException || error is AccessViolationException;
        }

        private sealed class Record
        {
            internal ClipboardEntry Entry;
            internal string ImageFile;
        }

        [DataContract]
        private sealed class HistoryData
        {
            [DataMember(Order = 0)] public int Version { get; set; }
            [DataMember(Order = 1)] public List<StoredEntry> Entries { get; set; }
        }

        [DataContract]
        private sealed class StoredEntry
        {
            [DataMember(Order = 0)] public string Id { get; set; }
            [DataMember(Order = 1)] public string Text { get; set; }
            [DataMember(Order = 2)] public string FileName { get; set; }
            [DataMember(Order = 3)] public string ImageFile { get; set; }
            [DataMember(Order = 4)] public int PixelWidth { get; set; }
            [DataMember(Order = 5)] public int PixelHeight { get; set; }
            [DataMember(Order = 6)] public DateTimeOffset CapturedAtUtc { get; set; }
        }

        [DataContract]
        private sealed class LegacySlots
        {
            [DataMember(Order = 0)] public string[] Items { get; set; }
        }
    }
}
