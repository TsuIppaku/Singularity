using System;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Orbit
{
    internal sealed class ClipboardDragPayload
    {
        public DataObject Data { get; private set; }
        public string FilePath { get; private set; }
        internal ClipboardDragPayload(DataObject data, string filePath) { Data = data; FilePath = filePath; }
    }

    /// <summary>
    /// Creates real, independently retained files for native file-drop targets.
    /// It never reads or writes the system clipboard or the history image cache.
    /// </summary>
    internal sealed class ClipboardDragExport
    {
        private const string DirectoryName = "clipboard-drag-exports";
        private const string ManifestName = ".orbit-drag-export.json";
        private const string ManifestOwner = "Orbit.ClipboardDragExport.v1";
        private const int MaximumImageBytes = 32 * 1024 * 1024;
        private const int MaximumFileBytes = 64 * 1024 * 1024;
        private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
        private static readonly Regex OwnedDirectory = new Regex(@"\Aexport-[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
        private readonly string directory;
        private readonly Func<DateTimeOffset> clock;
        private DateTimeOffset? lastCleanup;

        public ClipboardDragExport(string dataDirectory) : this(dataDirectory, delegate { return DateTimeOffset.UtcNow; }) { }

        internal ClipboardDragExport(string dataDirectory, Func<DateTimeOffset> clock)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("需要一个数据目录。", "dataDirectory");
            if (clock == null) throw new ArgumentNullException("clock");
            directory = Path.Combine(Path.GetFullPath(dataDirectory), DirectoryName);
            this.clock = clock;
            // Cleanup is best effort: a locked or damaged old export must not
            // prevent application startup or the creation of another export.
            CleanupExpired(clock().ToUniversalTime());
        }

        public ClipboardDragPayload Prepare(ClipboardEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            bool isImage = entry.IsImage;
            bool isFile = entry.IsFile;
            if (isFile && (isImage || !String.IsNullOrEmpty(entry.Text))) throw new InvalidDataException("文件记录不能同时包含图片或文字。");
            if (!isFile && !isImage && String.IsNullOrEmpty(entry.Text)) throw new ArgumentException("这条记录没有可拖出的内容。", "entry");
            if (isImage && entry.ImagePng.Length > MaximumImageBytes) throw new InvalidDataException("图片超过 32 MB，暂不能拖出。");
            byte[] bytes = isFile ? ReadFileBytes(entry.FilePath) : isImage ? (byte[])entry.ImagePng.Clone() : new UTF8Encoding(false).GetBytes(entry.Text);
            DataObject data = CreateData(entry, bytes);
            DateTimeOffset now = clock().ToUniversalTime();
            string fileName = isFile ? OrdinaryFileName(String.IsNullOrEmpty(entry.FileName) ? Path.GetFileName(entry.FilePath) : entry.FileName) : SafeFileName(entry.FileName, isImage);
            string digest = Hash(bytes);
            // A 128-bit directory key avoids spending legacy Windows' path
            // budget on a full digest; the manifest retains the full content hash.
            string key = Hash(Encoding.UTF8.GetBytes((entry.Id ?? String.Empty) + "\n" + fileName + "\n" + digest)).Substring(0, 32);
            string itemDirectory = ChildPath(directory, "export-" + key);
            bool nestedFile = fileName.Equals(ManifestName, StringComparison.OrdinalIgnoreCase);
            string payloadDirectory = nestedFile ? ChildPath(itemDirectory, "file") : itemDirectory;
            string filePath = ChildPath(payloadDirectory, fileName);
            string manifestPath = ChildPath(itemDirectory, ManifestName);

            RejectLink(directory);
            Directory.CreateDirectory(directory);
            RejectLink(directory);
            CleanupExpired(now);
            bool created = false;
            try
            {
                if (Directory.Exists(itemDirectory))
                {
                    RejectLink(itemDirectory);
                    ExportManifest existing = ReadOwnedManifest(itemDirectory);
                    if (existing.FileName != fileName || existing.ContentHash != digest || existing.NestedFile != nestedFile)
                        throw new IOException("拖出目录已有不匹配的文件，未覆盖现有内容。");
                    RejectLink(payloadDirectory);
                    if (nestedFile) Directory.CreateDirectory(payloadDirectory);
                    RejectLink(filePath);
                    // A drag target may have edited or removed a previously
                    // exported file. Restore only the exact file we own.
                    if (!File.Exists(filePath) || !FileEquals(filePath, bytes)) WriteAtomically(filePath, bytes);
                }
                else
                {
                    if (File.Exists(itemDirectory)) throw new IOException("拖出目录被文件占用。");
                    Directory.CreateDirectory(itemDirectory);
                    created = true;
                    RejectLink(itemDirectory);
                    if (nestedFile) Directory.CreateDirectory(payloadDirectory);
                    WriteAtomically(filePath, bytes);
                }
                var manifest = new ExportManifest
                {
                    Owner = ManifestOwner, FileName = fileName, ContentHash = digest, LastUsedUtc = now, NestedFile = nestedFile
                };
                WriteManifest(manifestPath, manifest);
            }
            catch
            {
                if (created) CleanupIncomplete(itemDirectory, payloadDirectory, filePath, manifestPath);
                throw;
            }
            var files = new StringCollection();
            files.Add(filePath);
            data.SetFileDropList(files);
            // Shell CFSTR_PREFERREDDROPEFFECT is a little-endian DWORD in HGLOBAL.
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy), false), false);
            return new ClipboardDragPayload(data, filePath);
        }

        private static DataObject CreateData(ClipboardEntry entry, byte[] bytes)
        {
            var data = new DataObject();
            if (entry.IsFile) return data;
            if (!entry.IsImage)
            {
                data.SetText(entry.Text, TextDataFormat.UnicodeText);
                return data;
            }
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes.Length < 33 || !signature.SequenceEqual(bytes.Take(8)) || ReadBigEndian(bytes, 8) != 13 ||
                bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82)
                throw new InvalidDataException("图片不是有效的 PNG，未创建拖出文件。");
            long width = ReadBigEndian(bytes, 16), height = ReadBigEndian(bytes, 20);
            if (width < 1 || height < 1 || width > 16384 || height > 16384 || width * height > 24L * 1024 * 1024)
                throw new InvalidDataException("图片尺寸过大或无效，暂不能拖出。");
            try
            {
                using (var input = new MemoryStream(bytes, false))
                {
                    var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame frame = decoder.Frames[0];
                    if (frame.PixelWidth != width || frame.PixelHeight != height) throw new InvalidDataException("PNG 图片尺寸不一致。");
                    frame.Freeze();
                    data.SetImage(frame);
                }
            }
            catch (OutOfMemoryException error) { throw new InvalidDataException("PNG 图片未能解码。", error); }
            data.SetData("PNG", new MemoryStream(bytes, false), false);
            data.SetData("image/png", new MemoryStream(bytes, false), false);
            return data;
        }

        private void CleanupExpired(DateTimeOffset now)
        {
            if (lastCleanup.HasValue && now >= lastCleanup.Value && now - lastCleanup.Value < TimeSpan.FromHours(1)) return;
            lastCleanup = now;
            try
            {
                if (!Directory.Exists(directory)) return;
                RejectLink(directory);
                foreach (string child in Directory.GetDirectories(directory))
                {
                    if (!OwnedDirectory.IsMatch(Path.GetFileName(child))) continue;
                    try
                    {
                        RejectLink(child);
                        ExportManifest manifest = ReadOwnedManifest(child);
                        if (now - manifest.LastUsedUtc < Retention) continue;
                        string payloadDirectory = manifest.NestedFile ? ChildPath(child, "file") : child;
                        RejectLink(payloadDirectory);
                        string file = ChildPath(payloadDirectory, manifest.FileName);
                        string marker = ChildPath(child, ManifestName);
                        RejectLink(file);
                        // Do not delete directories containing anything outside
                        // this service's exact two-file ownership manifest.
                        if (manifest.NestedFile)
                        {
                            if (Directory.GetDirectories(child).Any(path => path != payloadDirectory) ||
                                Directory.GetFiles(child).Any(path => path != marker)) continue;
                            if (Directory.Exists(payloadDirectory) && (Directory.GetDirectories(payloadDirectory).Length != 0 ||
                                Directory.GetFiles(payloadDirectory).Any(path => path != file))) continue;
                        }
                        else if (Directory.GetDirectories(child).Length != 0 || Directory.GetFiles(child).Any(path => path != file && path != marker)) continue;
                        if (File.Exists(file)) File.Delete(file);
                        if (manifest.NestedFile && Directory.Exists(payloadDirectory)) Directory.Delete(payloadDirectory, false);
                        File.Delete(marker);
                        Directory.Delete(child, false);
                    }
                    catch (Exception error) { if (IsFatal(error)) throw; }
                }
            }
            catch (Exception error) { if (IsFatal(error)) throw; }
        }

        private static ExportManifest ReadOwnedManifest(string itemDirectory)
        {
            string path = ChildPath(itemDirectory, ManifestName);
            RejectLink(path);
            ExportManifest manifest;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > 8192) throw new InvalidDataException("拖出文件清单无法识别。");
                manifest = new DataContractJsonSerializer(typeof(ExportManifest)).ReadObject(input) as ExportManifest;
            }
            if (manifest == null || manifest.Owner != ManifestOwner || String.IsNullOrEmpty(manifest.FileName) ||
                manifest.FileName != OrdinaryFileName(manifest.FileName) ||
                manifest.NestedFile != manifest.FileName.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(manifest.ContentHash ?? String.Empty, @"\A[0-9a-f]{64}\z") || manifest.LastUsedUtc == default(DateTimeOffset))
                throw new InvalidDataException("拖出文件清单无法识别，原目录已保留。");
            return manifest;
        }

        private static string OrdinaryFileName(string original)
        {
            // Real file names/extensions must survive a file drop unchanged.
            // Reject unsafe metadata instead of turning a PDF/DOCX into text.
            if (String.IsNullOrWhiteSpace(original) || original.Length > 255 || original == "." || original == ".." ||
                original.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || original != original.TrimEnd(' ', '.') ||
                Regex.IsMatch(original.Split('.')[0].TrimEnd(' '), @"\A(CON|PRN|AUX|NUL|CLOCK\$|COM[1-9¹²³]|LPT[1-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("文件名无法用于拖出缓存，请使用普通的本地文件名。");
            return original;
        }

        private static byte[] ReadFileBytes(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length < 3 || !Char.IsLetter(path[0]) || path[1] != ':' ||
                (path[2] != '\\' && path[2] != '/') || path.IndexOf(':', 2) >= 0)
                throw new InvalidDataException("拖出的文件必须位于本机，暂不支持网络路径或链接。");
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (new DriveInfo(Path.GetPathRoot(fullPath)).DriveType == DriveType.Network)
                    throw new InvalidDataException("暂不支持网络位置的文件，请先保存到本机。");
                if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0) throw new InvalidDataException("暂不支持拖出文件夹。");
                for (string current = fullPath; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) RejectLink(current);
                using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length > MaximumFileBytes) throw new InvalidDataException("文件超过 64 MB，暂不能拖出。");
                    byte[] bytes = new byte[(int)input.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = input.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0) throw new EndOfStreamException("文件读取不完整。");
                        offset += read;
                    }
                    return bytes;
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException) &&
                    !(error is ArgumentException) && !(error is NotSupportedException)) throw;
                throw new InvalidDataException("文件不存在、正被占用或无法读取，暂不能拖出。", error);
            }
        }

        private static string SafeFileName(string original, bool image)
        {
            string name = original ?? String.Empty;
            int separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            name = name.Substring(separator + 1);
            int extension = name.LastIndexOf('.');
            if (extension >= 0) name = name.Substring(0, extension);
            char[] forbidden = Path.GetInvalidFileNameChars();
            name = new String(name.Select(character => character < 32 || forbidden.Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
            if (name.Length > 80)
            {
                name = name.Substring(0, 80);
                if (Char.IsHighSurrogate(name[name.Length - 1])) name = name.Substring(0, name.Length - 1);
                name = name.TrimEnd(' ', '.');
            }
            if (String.IsNullOrWhiteSpace(name)) name = image ? "图片" : "剪贴板文字";
            string deviceStem = name.Split('.')[0].TrimEnd(' ');
            if (Regex.IsMatch(deviceStem, @"\A(CON|PRN|AUX|NUL|CLOCK\$|COM[1-9¹²³]|LPT[1-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) name = "_" + name;
            return name + (image ? ".png" : ".txt");
        }

        private static string ChildPath(string parent, string name)
        {
            string path = Path.GetFullPath(Path.Combine(parent, name));
            if (!String.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("拖出文件路径越界。");
            return path;
        }

        private static void RejectLink(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("拖出文件目录与文件不能是链接。");
        }

        private static bool FileEquals(string path, byte[] bytes)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != bytes.Length) return false;
                byte[] buffer = new byte[81920];
                int offset = 0, read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    for (int index = 0; index < read; index++) if (buffer[index] != bytes[offset + index]) return false;
                    offset += read;
                }
                return offset == bytes.Length;
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", String.Empty).ToLowerInvariant();
        }

        private static void WriteManifest(string path, ExportManifest manifest)
        {
            using (var buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ExportManifest)).WriteObject(buffer, manifest);
                WriteAtomically(path, buffer.ToArray());
            }
        }

        private static void WriteAtomically(string path, byte[] bytes)
        {
            RejectLink(path);
            string temporary = ChildPath(Path.GetDirectoryName(path), ".orbit-drag-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private static void CleanupIncomplete(string itemDirectory, string payloadDirectory, string filePath, string manifestPath)
        {
            try
            {
                RejectLink(itemDirectory);
                if (!File.Exists(manifestPath))
                {
                    RejectLink(filePath);
                    if (File.Exists(filePath)) File.Delete(filePath);
                    if (payloadDirectory != itemDirectory && Directory.Exists(payloadDirectory))
                    {
                        RejectLink(payloadDirectory);
                        if (Directory.GetFileSystemEntries(payloadDirectory).Length == 0) Directory.Delete(payloadDirectory, false);
                    }
                    if (Directory.GetFileSystemEntries(itemDirectory).Length == 0) Directory.Delete(itemDirectory, false);
                }
            }
            catch (Exception error) { if (IsFatal(error)) throw; }
        }

        private static long ReadBigEndian(byte[] bytes, int offset)
        {
            return ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16) | ((long)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static bool IsFatal(Exception error)
        {
            return error is OutOfMemoryException || error is StackOverflowException || error is AccessViolationException;
        }

        [DataContract]
        private sealed class ExportManifest
        {
            [DataMember(Order = 0)] public string Owner { get; set; }
            [DataMember(Order = 1)] public string FileName { get; set; }
            [DataMember(Order = 2)] public string ContentHash { get; set; }
            [DataMember(Order = 3)] public DateTimeOffset LastUsedUtc { get; set; }
            [DataMember(Order = 4, EmitDefaultValue = false)] public bool NestedFile { get; set; }
        }
    }
}
