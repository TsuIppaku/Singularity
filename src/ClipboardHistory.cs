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
        // Ordinary files use a source path on input and an owned snapshot after saving.
        public string FilePath { get; set; }
        public bool IsFile { get { return !String.IsNullOrEmpty(FilePath); } }
        public byte[] ImagePng { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
        public bool IsImage { get { return ImagePng != null && ImagePng.Length != 0; } }
        // Only used while processing an unnamed image from the system clipboard.
        internal string ImagePixelKey { get; set; }
    }

    /// <summary>
    /// The three-entry clipboard history. This class never accesses the system
    /// clipboard: the monitor supplies captures and the UI requests promotions.
    /// </summary>
    internal sealed class ClipboardHistory
    {
        private const int Capacity = 3;
        // Keep capture limits aligned with ClipboardMonitor, while allowing the
        // persistence class to be built and tested without desktop dependencies.
        private const int MaximumImageBytes = 32 * 1024 * 1024;
        private const long MaximumFileBytes = 64L * 1024 * 1024;
        private const long MaximumImagePixels = 24L * 1024 * 1024;
        private const string MetadataName = "clipboard-history.json";
        private const string CacheName = "clipboard-history-images";
        private static readonly Regex CacheFilePattern = new Regex(@"\A[0-9a-f]{32}\.png\z", RegexOptions.CultureInvariant);
        private readonly object gate = new object();
        private readonly string directory;
        private readonly string metadataPath;
        private readonly string cacheDirectory;
        private List<Record> records = new List<Record>();
        private Record lockedRecord;
        private bool confirmLockedReplacement = true;
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
        public ClipboardEntry LockedEntry { get { lock (gate) return lockedRecord == null ? null : lockedRecord.Entry; } }
        public bool ConfirmLockedReplacement { get { lock (gate) return confirmLockedReplacement; } }

        public bool SetConfirmLockedReplacement(bool value)
        {
            bool saved;
            lock (gate)
            {
                if (!CanSave()) return false;
                if (value == confirmLockedReplacement) return true;
                saved = Commit(new List<Record>(records), lockedRecord, value);
            }
            if (saved) NotifyChanged();
            return saved;
        }

        public bool Lock(string id)
        {
            bool saved;
            lock (gate)
            {
                if (!CanSave()) return false;
                if (lockedRecord != null && lockedRecord.Entry.Id == id) return true;
                Record target = records.FirstOrDefault(record => record.Entry.Id == id);
                if (target == null) { LastError = "找不到这条剪贴板记录。"; return false; }
                var next = new List<Record>(records);
                next.Remove(target);
                saved = Commit(next, target, confirmLockedReplacement);
            }
            if (saved) NotifyChanged();
            return saved;
        }

        public bool LockDropped(ClipboardEntry entry)
        {
            bool saved;
            lock (gate)
            {
                if (!CanSave()) return false;
                Record target;
                try { target = Normalize(entry, true); }
                catch (Exception error)
                {
                    if (IsFatal(error)) throw;
                    LastError = "这次内容未能锁定：" + error.Message;
                    return false;
                }
                saved = Commit(new List<Record>(records), target, confirmLockedReplacement);
            }
            if (saved) NotifyChanged();
            return saved;
        }

        public bool Unlock()
        {
            bool saved;
            lock (gate)
            {
                if (!CanSave()) return false;
                if (lockedRecord == null) return true;
                var next = new List<Record>(records);
                if (!lockedRecord.Entry.IsFile)
                {
                    if (next.Count == Capacity) next.RemoveAt(next.Count - 1);
                    next.Add(lockedRecord);
                }
                saved = Commit(next, null, confirmLockedReplacement);
            }
            if (saved) NotifyChanged();
            return saved;
        }

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
                if (lockedRecord != null && lockedRecord.Entry.Id == id) return true;
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
                if (lockedRecord != null && lockedRecord.Entry.Id == id)
                { LastError = "请先解锁这条记录。"; return false; }
                int index = records.FindIndex(record => record.Entry.Id == id);
                if (index < 0) { LastError = "找不到这条剪贴板记录。"; return false; }
                var next = new List<Record>(records);
                next.RemoveAt(index);
                saved = Commit(next);
            }
            if (saved) NotifyChanged();
            return saved;
        }

        private Record Normalize(ClipboardEntry input, bool allowFile = false)
        {
            if (input == null) throw new ArgumentException("剪贴板内容为空。", "input");
            bool image = input.ImagePng != null && input.ImagePng.Length != 0;
            if (input.IsFile && (!allowFile || image || !String.IsNullOrEmpty(input.Text)))
                throw new ArgumentException("普通文件只能单独放入锁定槽。", "input");
            if (!input.IsFile && !image && String.IsNullOrEmpty(input.Text)) throw new ArgumentException("剪贴板内容为空。", "input");
            string id = input.Id;
            if (String.IsNullOrWhiteSpace(id) || records.Any(record => record.Entry.Id == id) ||
                (lockedRecord != null && lockedRecord.Entry.Id == id)) id = Guid.NewGuid().ToString("N");
            var entry = new ClipboardEntry
            {
                Id = id,
                Text = input.Text,
                FileName = input.FileName,
                CapturedAtUtc = input.CapturedAtUtc == default(DateTimeOffset) ? DateTimeOffset.UtcNow : input.CapturedAtUtc.ToUniversalTime()
            };
            if (input.IsFile)
            {
                string source = ValidateSourceFile(input.FilePath);
                string name = Path.GetFileName(source);
                string folder = Guid.NewGuid().ToString("N");
                entry.FileName = name;
                entry.FilePath = FileCachePath(folder, name);
                return new Record { Entry = entry, FileDirectory = folder, CachedFileName = name, SourceFilePath = source };
            }
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
                        restored.Add(RestoreRecord(stored, ids, false));
                    Record restoredLock = data.LockedEntry == null ? null : RestoreRecord(data.LockedEntry, ids, true);
                    records = restored;
                    lockedRecord = restoredLock;
                    confirmLockedReplacement = data.ConfirmLockedReplacement ?? true;
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

        private Record RestoreRecord(StoredEntry stored, HashSet<string> ids, bool allowFile)
        {
            if (stored == null || String.IsNullOrWhiteSpace(stored.Id) || !ids.Add(stored.Id))
                throw new InvalidDataException("历史记录包含无效编号。 ");
            var entry = new ClipboardEntry
            {
                Id = stored.Id, Text = stored.Text, FileName = stored.FileName,
                CapturedAtUtc = stored.CapturedAtUtc.ToUniversalTime()
            };
            if (!String.IsNullOrEmpty(stored.FileDirectory))
            {
                if (!allowFile || !String.IsNullOrEmpty(stored.ImageFile) || !String.IsNullOrEmpty(stored.Text))
                    throw new InvalidDataException("普通文件的锁定记录格式不正确。 ");
                entry.FilePath = FileCachePath(stored.FileDirectory, stored.CachedFileName);
                ValidateSourceFile(entry.FilePath);
            }
            else if (!String.IsNullOrEmpty(stored.CachedFileName)) throw new InvalidDataException("文件缓存记录不完整。 ");
            else if (!String.IsNullOrEmpty(stored.ImageFile))
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
            return new Record { Entry = entry, ImageFile = stored.ImageFile, FileDirectory = stored.FileDirectory, CachedFileName = stored.CachedFileName };
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
            return Commit(next, lockedRecord, confirmLockedReplacement);
        }

        private bool Commit(List<Record> next, Record nextLocked, bool nextConfirm)
        {
            string temporaryMetadata = Path.Combine(directory, ".orbit-history-" + Guid.NewGuid().ToString("N") + ".tmp");
            var newlyCreatedImages = new List<string>();
            var oldRecords = WithLock(records, lockedRecord);
            var nextRecords = WithLock(next, nextLocked);
            var oldImages = new HashSet<string>(oldRecords.Where(record => record.ImageFile != null).Select(record => record.ImageFile), StringComparer.Ordinal);
            var newFiles = new List<Record>();
            bool committed = false;
            try
            {
                Directory.CreateDirectory(directory);
                foreach (Record record in nextRecords)
                {
                    if (record.Entry.IsFile)
                    {
                        if (record.SourceFilePath != null)
                        {
                            SnapshotFile(record);
                            newFiles.Add(record);
                        }
                        else ValidateSourceFile(FileCachePath(record.FileDirectory, record.CachedFileName));
                        continue;
                    }
                    if (!record.Entry.IsImage) continue;
                    string path = CachePath(record.ImageFile);
                    if (File.Exists(path)) { RejectLinkedCacheFile(path); continue; }
                    Directory.CreateDirectory(cacheDirectory);
                    WriteImageAtomically(path, record.Entry.ImagePng);
                    // Only a completed move establishes ownership. A failed move
                    // must never cause cleanup of a conflicting existing file.
                    if (!oldImages.Contains(record.ImageFile)) newlyCreatedImages.Add(path);
                }
                var data = new HistoryData
                {
                    Version = 1, Entries = next.Select(ToStoredEntry).ToList(),
                    LockedEntry = nextLocked == null ? null : ToStoredEntry(nextLocked), ConfirmLockedReplacement = nextConfirm
                };
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
                if (!committed)
                {
                    foreach (string path in newlyCreatedImages) DeleteTemporary(path);
                    foreach (Record record in newFiles) DeleteOwnedFile(record);
                }
            }

            records = next;
            lockedRecord = nextLocked;
            confirmLockedReplacement = nextConfirm;
            foreach (Record record in newFiles) record.SourceFilePath = null;
            LastError = null;
            var retained = new HashSet<string>(nextRecords.Where(record => record.ImageFile != null).Select(record => record.ImageFile), StringComparer.Ordinal);
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
            var retainedFiles = new HashSet<string>(nextRecords.Where(record => record.FileDirectory != null).Select(record => record.FileDirectory), StringComparer.Ordinal);
            foreach (Record record in oldRecords)
                if (record.FileDirectory != null && !retainedFiles.Contains(record.FileDirectory)) DeleteOwnedFile(record);
            return true;
        }

        private static List<Record> WithLock(List<Record> items, Record pinned)
        {
            var result = new List<Record>(items);
            if (pinned != null) result.Add(pinned);
            return result;
        }

        private string FileCachePath(string folder, string name)
        {
            if (!Regex.IsMatch(folder ?? String.Empty, @"\A[0-9a-f]{32}\z") || !IsSafeFileName(name))
                throw new InvalidDataException("锁定文件的缓存路径不正确。 ");
            string parent = Path.GetFullPath(Path.Combine(cacheDirectory, folder));
            string path = Path.GetFullPath(Path.Combine(parent, name));
            if (!String.Equals(Path.GetDirectoryName(parent), cacheDirectory, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("锁定文件的缓存路径越界。 ");
            RejectPathLinks(path);
            return path;
        }

        private static bool IsSafeFileName(string name)
        {
            return !String.IsNullOrWhiteSpace(name) && name != "." && name != ".." &&
                name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name == name.TrimEnd(' ', '.') &&
                name == Path.GetFileName(name);
        }

        private static string ValidateSourceFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]")) throw new InvalidDataException("请拖入本机文件。 ");
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || !IsSafeFileName(Path.GetFileName(full)))
                throw new InvalidDataException("只支持本机普通文件。 ");
            RejectPathLinks(full);
            if (!File.Exists(full) || Directory.Exists(full)) throw new FileNotFoundException("找不到可读取的普通文件。 ");
            if (new FileInfo(full).Length > MaximumFileBytes) throw new InvalidDataException("文件超过 64 MiB，暂不能锁定。 ");
            return full;
        }

        private static void RejectPathLinks(string path)
        {
            for (string current = path; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("锁定文件和缓存路径不能经过链接。 ");
        }

        private void SnapshotFile(Record record)
        {
            string source = ValidateSourceFile(record.SourceFilePath);
            string path = FileCachePath(record.FileDirectory, record.CachedFileName);
            string folder = Path.GetDirectoryName(path);
            bool createdFile = false, createdFolder = false, finished = false;
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length > MaximumFileBytes) throw new InvalidDataException("文件超过 64 MiB，暂不能锁定。 ");
                    Directory.CreateDirectory(cacheDirectory);
                    RejectPathLinks(cacheDirectory);
                    if (File.Exists(folder) || Directory.Exists(folder)) throw new IOException("文件缓存目录已被占用。 ");
                    Directory.CreateDirectory(folder);
                    createdFolder = true;
                    RejectPathLinks(folder);
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                    {
                        createdFile = true;
                        byte[] buffer = new byte[81920];
                        long total = 0;
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            total += count;
                            if (total > MaximumFileBytes) throw new InvalidDataException("文件超过 64 MiB，暂不能锁定。 ");
                            output.Write(buffer, 0, count);
                        }
                        output.Flush(true);
                    }
                }
                finished = true;
            }
            finally
            {
                if (!finished)
                {
                    if (createdFile) DeleteTemporary(path);
                    if (createdFolder) DeleteEmptyFolder(folder);
                }
            }
        }

        private void DeleteOwnedFile(Record record)
        {
            try
            {
                string path = FileCachePath(record.FileDirectory, record.CachedFileName);
                if (File.Exists(path)) File.Delete(path);
                DeleteEmptyFolder(Path.GetDirectoryName(path));
            }
            catch (Exception error)
            {
                if (IsFatal(error)) throw;
                LastError = (LastError ?? "历史已保存。") + "旧文件副本暂未清理。" + error.Message;
            }
        }

        private static void DeleteEmptyFolder(string path)
        {
            try
            {
                RejectPathLinks(path);
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
                CapturedAtUtc = record.Entry.CapturedAtUtc, FileDirectory = record.FileDirectory, CachedFileName = record.CachedFileName
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
            internal string FileDirectory;
            internal string CachedFileName;
            internal string SourceFilePath;
        }

        [DataContract]
        private sealed class HistoryData
        {
            [DataMember(Order = 0)] public int Version { get; set; }
            [DataMember(Order = 1)] public List<StoredEntry> Entries { get; set; }
            [DataMember(Order = 2, EmitDefaultValue = false)] public StoredEntry LockedEntry { get; set; }
            [DataMember(Order = 3, EmitDefaultValue = false)] public bool? ConfirmLockedReplacement { get; set; }
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
            [DataMember(Order = 7, EmitDefaultValue = false)] public string FileDirectory { get; set; }
            [DataMember(Order = 8, EmitDefaultValue = false)] public string CachedFileName { get; set; }
        }

        [DataContract]
        private sealed class LegacySlots
        {
            [DataMember(Order = 0)] public string[] Items { get; set; }
        }
    }
}
