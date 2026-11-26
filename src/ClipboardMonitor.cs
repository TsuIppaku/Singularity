using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace Orbit
{
    /// <summary>Event-driven clipboard capture. No clipboard access occurs on a polling timer.</summary>
    internal sealed class ClipboardMonitor : IDisposable
    {
        internal const string OriginFormat = "Orbit.Clipboard.Origin.v1";
        internal const int MaximumImageBytes = 32 * 1024 * 1024;
        internal const int MaximumFileBytes = 64 * 1024 * 1024;
        internal const long MaximumImagePixels = 24L * 1024 * 1024;
        internal const int MaximumTextCharacters = 1000000;
        private const int WmClipboardUpdate = 0x031D;
        private static readonly int[] RetryDelays = { 25, 50, 100, 200, 350 };
        private static readonly string[] PngFormats = { "PNG", "image/png" };
        private static readonly long ImageRepeatWindowTicks = Stopwatch.Frequency * 2;
        private readonly Window owner;
        private readonly Dispatcher dispatcher;
        private readonly Action<ClipboardEntry> captured;
        private readonly Action<string> status;
        private readonly Func<uint> sequence;
        private readonly Func<IDataObject> read;
        private readonly Action<IDataObject> write;
        private readonly DispatcherTimer retryTimer;
        private readonly HashSet<string> ownWrites = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> writeOrder = new Queue<string>();
        private readonly HwndSourceHook hook;
        private HwndSource source;
        private IntPtr handle;
        private bool registered;
        private bool started;
        private bool disposed;
        private bool queued;
        private bool hasHandledSequence;
        private uint handledSequence;
        private uint retrySequence;
        private int retryAttempt;
        private string lastImagePixelKey;
        private long lastImageSeenAt;

        internal ClipboardMonitor(Window owner, Action<ClipboardEntry> captured, Action<string> status)
            : this(owner, captured, status, GetClipboardSequenceNumber,
                delegate { return Clipboard.GetDataObject(); },
                delegate(IDataObject data) { Clipboard.SetDataObject(data, true); }) { }

        // The injected transport lets fixtures exercise retries and self-write suppression
        // without registering a native listener or touching the real clipboard.
        internal ClipboardMonitor(Window owner, Action<ClipboardEntry> captured, Action<string> status,
            Func<uint> sequence, Func<IDataObject> read, Action<IDataObject> write)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (captured == null) throw new ArgumentNullException("captured");
            if (sequence == null || read == null || write == null) throw new ArgumentNullException("clipboard transport");
            this.owner = owner;
            dispatcher = owner.Dispatcher;
            this.captured = captured;
            this.status = status ?? delegate { };
            this.sequence = sequence;
            this.read = read;
            this.write = write;
            hook = OnWindowMessage;
            retryTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher);
            retryTimer.Tick += delegate { retryTimer.Stop(); QueueRead(); };
            owner.Closed += OnOwnerClosed;
        }

        internal void Start()
        {
            dispatcher.VerifyAccess();
            if (disposed || started) return;
            started = true;
            handle = new WindowInteropHelper(owner).Handle;
            if (handle == IntPtr.Zero) owner.SourceInitialized += OnSourceInitialized;
            else Attach();
        }

        private void OnSourceInitialized(object sender, EventArgs args)
        {
            owner.SourceInitialized -= OnSourceInitialized;
            if (!disposed && started) { handle = new WindowInteropHelper(owner).Handle; Attach(); }
        }

        private void Attach()
        {
            source = HwndSource.FromHwnd(handle);
            if (source == null) { started = false; status("剪贴板监听暂时无法启动。"); return; }
            source.AddHook(hook);
            if (!RegisterWithBaseline(delegate { return AddClipboardFormatListener(handle); }))
            {
                int error = Marshal.GetLastWin32Error();
                source.RemoveHook(hook);
                source = null;
                started = false;
                status("剪贴板监听无法启动：" + new Win32Exception(error).Message);
                return;
            }
            registered = true;
        }

        // Shared with in-memory fixtures so listener startup can be exercised
        // without registering a window or accessing the real clipboard.
        internal bool RegisterWithBaseline(Func<bool> registerListener)
        {
            dispatcher.VerifyAccess();
            if (registerListener == null) throw new ArgumentNullException("registerListener");
            if (disposed) return false;
            uint before = sequence();
            if (!registerListener()) return false;
            uint after = sequence();
            // Existing clipboard contents predate this listener. Treat them as
            // handled, but retain a copy that happened while registration ran.
            // Zero is unknown: only two known, different numbers prove a change.
            MarkHandled(before != 0 ? before : after);
            if (before != 0 && after != 0 && after != before) RequestCapture();
            return true;
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmClipboardUpdate && !disposed)
            {
                RequestCapture();
                handled = true;
            }
            return IntPtr.Zero;
        }

        internal void RequestCapture()
        {
            if (disposed || dispatcher.HasShutdownStarted) return;
            if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(RequestCapture)); return; }
            uint current = sequence();
            if (current != 0 && hasHandledSequence && current == handledSequence) return;
            if (current != retrySequence) { retrySequence = current; retryAttempt = 0; }
            retryTimer.Stop();
            QueueRead();
        }

        private void QueueRead()
        {
            if (disposed || queued || dispatcher.HasShutdownStarted) return;
            queued = true;
            // Run promptly after the native message, not after a fixed debounce window.
            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate
            {
                queued = false;
                if (!disposed) CapturePending();
            }));
        }

        private void CapturePending()
        {
            uint before = sequence();
            if (before != 0 && hasHandledSequence && before == handledSequence) return;
            if (before != retrySequence) retrySequence = before;
            ClipboardEntry entry = null;
            string notice = null;
            try
            {
                IDataObject data = read();
                string token = ReadOrigin(data);
                uint afterMarker = sequence();
                if (afterMarker != before) { Retry(afterMarker, false); return; }
                if (token != null && ownWrites.Remove(token)) { MarkHandled(before); return; }
                if (data != null) entry = ReadData(data, DateTimeOffset.UtcNow, out notice);
                uint after = sequence();
                if (after != before) { Retry(after, false); return; }
                MarkHandled(before);
            }
            catch (ExternalException)
            {
                Retry(sequence(), true);
                return;
            }
            catch (Exception error)
            {
                if (!IsExpectedFailure(error)) throw;
                MarkHandled(before);
                status("未记录这次剪贴板内容：" + FriendlyMessage(error));
                return;
            }
            // Keep application callback failures separate from codec failures.
            if (entry != null && entry.ImagePixelKey != null)
            {
                long now = Stopwatch.GetTimestamp();
                bool repeated = entry.ImagePixelKey == lastImagePixelKey &&
                    now - lastImageSeenAt < ImageRepeatWindowTicks;
                lastImagePixelKey = entry.ImagePixelKey;
                lastImageSeenAt = now;
                if (repeated) return;
            }
            if (entry != null) captured(entry);
            if (!String.IsNullOrEmpty(notice)) status(notice);
        }

        private void Retry(uint current, bool busy)
        {
            if (disposed) return;
            retrySequence = current;
            if (retryAttempt >= RetryDelays.Length)
            {
                if (busy) status("剪贴板仍被占用，这次未能读取；下次复制时会自动重试。");
                return;
            }
            retryTimer.Stop();
            retryTimer.Interval = TimeSpan.FromMilliseconds(busy ? RetryDelays[retryAttempt] : 15);
            retryAttempt++;
            retryTimer.Start();
        }

        private void MarkHandled(uint value)
        {
            handledSequence = value;
            hasHandledSequence = true;
            retryAttempt = 0;
            retryTimer.Stop();
        }

        internal bool Copy(ClipboardEntry entry)
        {
            dispatcher.VerifyAccess();
            if (disposed) return false;
            string token = Guid.NewGuid().ToString("N");
            try
            {
                DataObject data = CreateDataObject(entry, token);
                ownWrites.Add(token);
                writeOrder.Enqueue(token);
                while (writeOrder.Count > 32) ownWrites.Remove(writeOrder.Dequeue());
                write(data);
                // Do not blindly suppress GetClipboardSequenceNumber() here: another
                // application may already have copied something after our write.
                return true;
            }
            catch (Exception error)
            {
                if (!IsExpectedFailure(error)) throw;
                ownWrites.Remove(token);
                status(error is ExternalException ? "剪贴板正忙，复制未完成。请再试一次。" : "复制未完成：" + FriendlyMessage(error));
                return false;
            }
        }

        internal static DataObject CreateDataObject(ClipboardEntry entry, string originToken)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            var data = new DataObject();
            if (entry.IsFile)
            {
                if (entry.IsImage || !String.IsNullOrEmpty(entry.Text)) throw new InvalidDataException("文件记录不能同时包含图片或文字。");
                string path = ValidateLocalFilePath(entry.FilePath);
                CheckReadableFile(path);
                var files = new StringCollection();
                files.Add(path);
                data.SetFileDropList(files);
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy), false), false);
            }
            else if (entry.ImagePng != null && entry.ImagePng.Length > 0)
            {
                bool animated;
                BitmapSource image = DecodeEncodedImage(entry.ImagePng, out animated);
                // PNG carries full alpha. Bitmap makes the same pixels available to
                // native image editors and chat applications expecting CF_BITMAP.
                data.SetData("PNG", new MemoryStream(entry.ImagePng, false), false);
                data.SetData("image/png", new MemoryStream(entry.ImagePng, false), false);
                data.SetImage(image);
            }
            else
            {
                if (String.IsNullOrEmpty(entry.Text)) throw new ArgumentException("这条记录没有可复制的内容。");
                if (entry.Text.Length > MaximumTextCharacters) throw new InvalidDataException("文字超过 100 万字符，暂不处理。");
                data.SetText(entry.Text, TextDataFormat.UnicodeText);
            }
            if (!String.IsNullOrEmpty(originToken))
                data.SetData(OriginFormat, new MemoryStream(Encoding.ASCII.GetBytes(originToken), false), false);
            return data;
        }

        // Explicit drop/pin input only. Automatic clipboard capture deliberately
        // continues to accept just text and readable images in ReadData below.
        internal static ClipboardEntry ReadDroppedFile(string path, DateTimeOffset now)
        {
            string localPath = ValidateLocalFilePath(path);
            long length = CheckReadableFile(localPath);
            if (IsImageFileName(localPath))
            {
                string extension = Path.GetExtension(localPath).ToLowerInvariant();
                bool optionalCodec = extension == ".webp" || extension == ".heic" || extension == ".heif";
                if (length > MaximumImageBytes)
                {
                    if (!optionalCodec) throw new InvalidDataException("图片超过 32 MB，暂不能显示预览。");
                }
                else
                {
                    byte[] bytes;
                    try
                    {
                        using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            bytes = ReadBytes(input, MaximumImageBytes);
                    }
                    catch (Exception error)
                    {
                        if (!IsExpectedFailure(error)) throw;
                        throw new InvalidDataException("图片文件正被占用或无法读取，请稍后重试。", error);
                    }
                    try
                    {
                        bool animated;
                        BitmapSource bitmap = DecodeEncodedImage(bytes, out animated);
                        return ImageEntry(bitmap, Path.GetFileName(localPath), now);
                    }
                    catch (Exception error)
                    {
                        if (!IsExpectedImageFailure(error)) throw;
                        // Optional WIC codecs depend on the computer. Preserve
                        // unsupported formats as ordinary files, without a preview.
                        if (!optionalCodec) throw new InvalidDataException(FriendlyMessage(error), error);
                    }
                }
            }
            return new ClipboardEntry
            {
                Id = Guid.NewGuid().ToString("N"), FilePath = localPath, FileName = Path.GetFileName(localPath),
                CapturedAtUtc = now.ToUniversalTime()
            };
        }

        private static string ValidateLocalFilePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length < 3 || !Char.IsLetter(path[0]) || path[1] != ':' ||
                (path[2] != '\\' && path[2] != '/') || path.IndexOf(':', 2) >= 0)
                throw new InvalidDataException("请拖入一个本地文件；暂不支持文件夹、网络路径或链接。");
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (new DriveInfo(Path.GetPathRoot(fullPath)).DriveType == DriveType.Network)
                    throw new InvalidDataException("暂不支持网络位置的文件，请先保存到本机。");
                FileAttributes attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.Directory) != 0) throw new InvalidDataException("暂不支持文件夹，请拖入单个文件。");
                for (string current = fullPath; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("暂不支持链接或重定向路径，请拖入原始本地文件。");
                return fullPath;
            }
            catch (InvalidDataException) { throw; }
            catch (Exception error)
            {
                if (!IsExpectedFailure(error)) throw;
                throw new InvalidDataException("文件不存在、没有读取权限或路径无法使用。", error);
            }
        }

        private static long CheckReadableFile(string path)
        {
            try
            {
                // Inspect access and length only; ordinary file bytes are copied
                // by the history store after the user locks the item.
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (input.Length > MaximumFileBytes) throw new InvalidDataException("文件超过 64 MB，暂不能收存。");
                    return input.Length;
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception error)
            {
                if (!IsExpectedFailure(error)) throw;
                throw new InvalidDataException("文件正被占用或无法读取，请稍后重试。", error);
            }
        }

        internal static ClipboardEntry ReadData(IDataObject data, DateTimeOffset now)
        {
            string notice;
            return ReadData(data, now, out notice);
        }

        internal static ClipboardEntry ReadData(IDataObject data, DateTimeOffset now, out string notice)
        {
            notice = null;
            if (data == null) return null;
            Exception imageError = null;
            foreach (string format in PngFormats)
            {
                if (!data.GetDataPresent(format, false)) continue;
                object value = data.GetData(format, false);
                try
                {
                    bool animated;
                    BitmapSource bitmap = DecodeEncodedImage(ReadBytes(value, MaximumImageBytes), out animated);
                    if (animated) notice = "动图仅记录第一帧。";
                    return ImageEntry(bitmap, null, now);
                }
                catch (Exception error) { if (!IsExpectedImageFailure(error)) throw; imageError = error; }
            }

            if (data.GetDataPresent(DataFormats.FileDrop, false))
            {
                string[] files = FileNames(data.GetData(DataFormats.FileDrop, false));
                foreach (string file in files)
                {
                    if (!IsImageFileName(file)) continue;
                    try
                    {
                        byte[] bytes;
                        using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            bytes = ReadBytes(input, MaximumImageBytes);
                        bool animated;
                        BitmapSource bitmap = DecodeEncodedImage(bytes, out animated);
                        if (files.Length > 1) notice = "已记录所选文件中的首张可读图片。";
                        if (animated) notice = (notice ?? String.Empty) + "动图仅记录第一帧。";
                        return ImageEntry(bitmap, Path.GetFileName(file), now);
                    }
                    catch (Exception error) { if (!IsExpectedImageFailure(error)) throw; imageError = error; }
                }
                if (imageError != null) throw new InvalidDataException(FriendlyMessage(imageError), imageError);
                notice = "当前复制的是文件；只自动记录文字或可读取的图片。";
                return null;
            }

            if (data.GetDataPresent(DataFormats.Dib, false))
            {
                object value = data.GetData(DataFormats.Dib, false);
                try { return ImageEntry(DecodeDib(ReadBytes(value, MaximumImageBytes), null), null, now); }
                catch (Exception error) { if (!IsExpectedImageFailure(error)) throw; imageError = error; }
            }
            if (data.GetDataPresent(DataFormats.Bitmap, true))
            {
                object value = data.GetData(DataFormats.Bitmap, true);
                try
                {
                    BitmapSource bitmap = value as BitmapSource;
                    if (bitmap != null) return ImageEntry(NormalizeLegacyBitmap(bitmap), null, now);
                    Drawing.Image drawing = value as Drawing.Image;
                    if (drawing != null)
                    {
                        ValidateDimensions(drawing.Width, drawing.Height);
                        using (var buffer = new LimitedMemoryStream(MaximumImageBytes))
                        {
                            try { drawing.Save(buffer, Drawing.Imaging.ImageFormat.Png); }
                            catch (OutOfMemoryException error) { throw new InvalidDataException("图片编码器无法读取这张图片。", error); }
                            bool animated;
                            return ImageEntry(DecodeEncodedImage(buffer.ToArray(), out animated), null, now);
                        }
                    }
                    if (value != null) throw new InvalidDataException("剪贴板位图格式暂时无法读取。");
                }
                catch (Exception error) { if (!IsExpectedImageFailure(error)) throw; imageError = error; }
            }
            if (imageError != null) throw new InvalidDataException(FriendlyMessage(imageError), imageError);
            if (data.GetDataPresent(DataFormats.UnicodeText, false))
            {
                string text = data.GetData(DataFormats.UnicodeText, false) as string;
                if (String.IsNullOrEmpty(text)) return null;
                if (text.Length > MaximumTextCharacters) throw new InvalidDataException("文字超过 100 万字符，暂不记录。");
                return new ClipboardEntry { Id = Guid.NewGuid().ToString("N"), Text = text, CapturedAtUtc = now.ToUniversalTime() };
            }
            return null;
        }

        private static ClipboardEntry ImageEntry(BitmapSource bitmap, string name, DateTimeOffset now)
        {
            ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
            byte[] png;
            using (var output = new LimitedMemoryStream(MaximumImageBytes))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
                png = output.ToArray();
            }
            return new ClipboardEntry
            {
                Id = Guid.NewGuid().ToString("N"), ImagePng = png,
                FileName = String.IsNullOrEmpty(name) ? "图片-" + now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png" : name,
                ImagePixelKey = String.IsNullOrEmpty(name) ? TryGetImagePixelKey(bitmap) : null,
                PixelWidth = bitmap.PixelWidth, PixelHeight = bitmap.PixelHeight, CapturedAtUtc = now.ToUniversalTime()
            };
        }

        private static string TryGetImagePixelKey(BitmapSource bitmap)
        {
            try
            {
                // Compare identical image pixels independently of DPI, PNG
                // metadata, or which clipboard format supplied the image.
                BitmapSource pixels = bitmap.Format == PixelFormats.Bgra32 ? bitmap :
                    new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int stride = pixels.PixelWidth * 4;
                int rowsPerBlock = Math.Min(pixels.PixelHeight, Math.Max(1, 65536 / stride));
                var buffer = new byte[stride * rowsPerBlock];
                using (SHA256 hash = SHA256.Create())
                {
                    for (int y = 0; y < pixels.PixelHeight; y += rowsPerBlock)
                    {
                        int rows = Math.Min(rowsPerBlock, pixels.PixelHeight - y);
                        pixels.CopyPixels(new Int32Rect(0, y, pixels.PixelWidth, rows), buffer, stride, 0);
                        hash.TransformBlock(buffer, 0, rows * stride, buffer, 0);
                    }
                    hash.TransformFinalBlock(buffer, 0, 0);
                    return String.Format(CultureInfo.InvariantCulture, "{0}x{1}:{2}",
                        pixels.PixelWidth, pixels.PixelHeight, Convert.ToBase64String(hash.Hash));
                }
            }
            catch (Exception error)
            {
                if (!IsExpectedImageFailure(error) && !(error is CryptographicException)) throw;
                // A comparison failure must not block a valid image capture.
                return null;
            }
        }

        private static BitmapSource DecodeEncodedImage(byte[] bytes, out bool animated)
        {
            animated = false;
            if (bytes == null || bytes.Length < 8) throw new InvalidDataException("图片数据不完整。");
            if (bytes.Length > MaximumImageBytes) throw new InvalidDataException("图片超过 32 MB，暂不记录。");
            if (bytes.Length >= 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)
            {
                long width = ((long)bytes[16] << 24) | ((long)bytes[17] << 16) | ((long)bytes[18] << 8) | bytes[19];
                long height = ((long)bytes[20] << 24) | ((long)bytes[21] << 16) | ((long)bytes[22] << 8) | bytes[23];
                if (width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("PNG 图片尺寸过大。");
                ValidateDimensions((int)width, (int)height);
            }
            if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            {
                if (bytes.Length < 26) throw new InvalidDataException("BMP 图片数据不完整。");
                uint offset = UInt32(bytes, 10);
                if (offset < 14 || offset > bytes.Length) throw new InvalidDataException("BMP 像素位置无效。");
                byte[] dib = new byte[bytes.Length - 14];
                Buffer.BlockCopy(bytes, 14, dib, 0, dib.Length);
                return DecodeDib(dib, checked((int)offset - 14));
            }
            try
            {
                // Inspect dimensions before requesting the full pixel cache, then
                // load into memory so no decoder keeps the source file locked.
                using (var probe = new MemoryStream(bytes, false))
                {
                    BitmapDecoder header = BitmapDecoder.Create(probe, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                    if (header.Frames.Count == 0) throw new InvalidDataException("图片没有可读取的帧。");
                    ValidateDimensions(header.Frames[0].PixelWidth, header.Frames[0].PixelHeight);
                    animated = header.Frames.Count > 1;
                }
                using (var input = new MemoryStream(bytes, false))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame frame = decoder.Frames[0];
                    ValidateDimensions(frame.PixelWidth, frame.PixelHeight);
                    frame.Freeze();
                    return frame;
                }
            }
            catch (ExternalException error) { throw new InvalidDataException("图片数据无法解码。", error); }
            catch (OutOfMemoryException error) { throw new InvalidDataException("图片解码器无法读取这张图片。", error); }
        }

        private static BitmapSource NormalizeLegacyBitmap(BitmapSource source)
        {
            ValidateDimensions(source.PixelWidth, source.PixelHeight);
            if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32) return source;
            int stride = checked(source.PixelWidth * 4);
            byte[] pixels = new byte[checked(stride * source.PixelHeight)];
            source.CopyPixels(pixels, stride, 0);
            bool hasAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
            if (hasAlpha) return source;
            // CF_BITMAP frequently supplies undefined, all-zero alpha. PNG and
            // explicitly masked DIB alpha never pass through this normalization.
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            BitmapSource opaque = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            opaque.Freeze();
            return opaque;
        }

        private static BitmapSource DecodeDib(byte[] bytes, int? suppliedOffset)
        {
            if (bytes.Length < 12) throw new InvalidDataException("DIB 图片头不完整。");
            uint headerSize = UInt32(bytes, 0);
            if (headerSize != 12 && (headerSize < 40 || headerSize > bytes.Length)) throw new InvalidDataException("不支持这类 DIB 图片头。");
            int width = headerSize == 12 ? UInt16(bytes, 4) : Int32(bytes, 4);
            int rawHeight = headerSize == 12 ? UInt16(bytes, 6) : Int32(bytes, 8);
            if (rawHeight == int.MinValue) throw new InvalidDataException("图片尺寸无效。");
            int height = Math.Abs(rawHeight);
            ValidateDimensions(width, height);
            int bits = UInt16(bytes, headerSize == 12 ? 10 : 14);
            if (bits != 1 && bits != 4 && bits != 8 && bits != 16 && bits != 24 && bits != 32)
                throw new InvalidDataException("DIB 像素格式不支持。");
            uint compression = headerSize == 12 ? 0 : UInt32(bytes, 16);
            uint colors = headerSize == 12 ? 0 : UInt32(bytes, 32);
            if (colors == 0 && bits <= 8) colors = (uint)(1 << bits);
            long offset = headerSize + (long)colors * (headerSize == 12 ? 3 : 4);
            if (headerSize == 40 && (compression == 3 || compression == 6)) offset += compression == 6 ? 16 : 12;
            if (suppliedOffset.HasValue) offset = suppliedOffset.Value;
            if (offset < headerSize || offset > bytes.Length) throw new InvalidDataException("DIB 像素位置无效。");
            if (bits == 32 && (compression == 0 || compression == 3 || compression == 6))
            {
                uint red = 0x00ff0000, green = 0x0000ff00, blue = 0x000000ff, alpha = 0;
                if (compression == 3 || compression == 6 || headerSize >= 56)
                {
                    red = UInt32(bytes, 40); green = UInt32(bytes, 44); blue = UInt32(bytes, 48);
                    if (headerSize >= 56 || compression == 6) alpha = UInt32(bytes, 52);
                    if (red == 0 && green == 0 && blue == 0 && compression == 0)
                    { red = 0x00ff0000; green = 0x0000ff00; blue = 0x000000ff; }
                }
                if (red == 0 || green == 0 || blue == 0) throw new InvalidDataException("DIB 颜色掩码无效。");
                if ((red & green) != 0 || (red & blue) != 0 || (green & blue) != 0 || (alpha & (red | green | blue)) != 0)
                    throw new InvalidDataException("DIB 颜色掩码重叠。");
                int stride = checked(width * 4);
                int length = checked(stride * height);
                if (offset + length > bytes.Length) throw new InvalidDataException("DIB 像素数据不完整。");
                byte[] pixels = new byte[length];
                bool standard = red == 0x00ff0000 && green == 0x0000ff00 && blue == 0x000000ff && (alpha == 0 || alpha == 0xff000000);
                for (int y = 0; y < height; y++)
                {
                    int input = checked((int)offset + (rawHeight > 0 ? height - 1 - y : y) * stride);
                    int output = y * stride;
                    if (standard)
                    {
                        Buffer.BlockCopy(bytes, input, pixels, output, stride);
                        if (alpha == 0) for (int x = 3; x < stride; x += 4) pixels[output + x] = 255;
                        continue;
                    }
                    for (int x = 0; x < width; x++)
                    {
                        uint value = UInt32(bytes, input + x * 4);
                        pixels[output + x * 4] = Channel(value, blue);
                        pixels[output + x * 4 + 1] = Channel(value, green);
                        pixels[output + x * 4 + 2] = Channel(value, red);
                        pixels[output + x * 4 + 3] = alpha == 0 ? (byte)255 : Channel(value, alpha);
                    }
                }
                BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                bitmap.Freeze();
                return bitmap;
            }
            using (var input = new MemoryStream())
            {
                using (var writer = new BinaryWriter(input, Encoding.ASCII, true))
                {
                    writer.Write((ushort)0x4d42); writer.Write(checked(bytes.Length + 14)); writer.Write(0);
                    writer.Write(checked((int)offset + 14)); writer.Write(bytes); writer.Flush();
                }
                input.Position = 0;
                BitmapDecoder decoder = new BmpBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame frame = decoder.Frames[0];
                ValidateDimensions(frame.PixelWidth, frame.PixelHeight);
                frame.Freeze();
                return frame;
            }
        }

        private static byte Channel(uint value, uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0;
            while ((mask & 1) == 0) { mask >>= 1; shift++; }
            if ((mask & (mask + 1)) != 0) throw new InvalidDataException("DIB 颜色掩码不连续。");
            ulong component = (value >> shift) & mask;
            return (byte)((component * 255 + mask / 2) / mask);
        }

        private static void ValidateDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > MaximumImagePixels)
                throw new InvalidDataException("图片尺寸过大或无效；最多支持约 2500 万像素，单边不超过 16384 像素。");
        }

        private static byte[] ReadBytes(object value, int maximum)
        {
            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                if (bytes.Length > maximum) throw new InvalidDataException("图片超过 32 MB，暂不记录。");
                return bytes;
            }
            Stream stream = value as Stream;
            if (stream == null) throw new InvalidDataException("图片数据格式暂时无法读取。");
            long previous = stream.CanSeek ? stream.Position : 0;
            try
            {
                if (stream.CanSeek)
                {
                    if (stream.Length > maximum) throw new InvalidDataException("图片超过 32 MB，暂不记录。");
                    stream.Position = 0;
                }
                using (var output = new LimitedMemoryStream(maximum))
                {
                    byte[] buffer = new byte[81920];
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, count);
                    return output.ToArray();
                }
            }
            finally { if (stream.CanSeek) stream.Position = previous; }
        }

        private static string ReadOrigin(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(OriginFormat, false)) return null;
            object value = data.GetData(OriginFormat, false);
            string text = value as string;
            if (text != null) return text.Length <= 128 ? text : null;
            try { return Encoding.ASCII.GetString(ReadBytes(value, 128)).TrimEnd('\0'); }
            catch (InvalidDataException) { return null; }
        }

        private static string[] FileNames(object value)
        {
            string[] names = value as string[];
            if (names != null) return names;
            var collection = value as StringCollection;
            if (collection == null) return new string[0];
            names = new string[collection.Count];
            collection.CopyTo(names, 0);
            return names;
        }

        private static bool IsImageFileName(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            string extension;
            try { extension = Path.GetExtension(path).ToLowerInvariant(); }
            catch (ArgumentException) { return false; }
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".bmp" ||
                extension == ".gif" || extension == ".tif" || extension == ".tiff" || extension == ".ico" ||
                extension == ".webp" || extension == ".heic" || extension == ".heif";
        }

        private static uint UInt32(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 4) throw new InvalidDataException("图片数据不完整。");
            return BitConverter.ToUInt32(bytes, offset);
        }

        private static int Int32(byte[] bytes, int offset)
        {
            return unchecked((int)UInt32(bytes, offset));
        }

        private static int UInt16(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 2) throw new InvalidDataException("图片数据不完整。");
            return BitConverter.ToUInt16(bytes, offset);
        }

        private static bool IsExpectedImageFailure(Exception error)
        {
            return IsExpectedFailure(error);
        }

        private static bool IsExpectedFailure(Exception error)
        {
            return error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is SecurityException ||
                error is ArgumentException || error is FormatException || error is NotSupportedException ||
                error is InvalidOperationException || error is ExternalException || error is OverflowException;
        }

        private static string FriendlyMessage(Exception error)
        {
            if (error is InvalidDataException) return error.Message;
            if (error is UnauthorizedAccessException || error is SecurityException) return "没有权限读取这张图片。";
            if (error is FileNotFoundException || error is DirectoryNotFoundException) return "原图片文件已不存在。";
            if (error is IOException) return "图片文件正在使用或无法读取。";
            return "图片格式不支持或数据已损坏。";
        }

        private void OnOwnerClosed(object sender, EventArgs args) { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            dispatcher.VerifyAccess();
            disposed = true;
            retryTimer.Stop();
            owner.SourceInitialized -= OnSourceInitialized;
            owner.Closed -= OnOwnerClosed;
            if (registered && handle != IntPtr.Zero) RemoveClipboardFormatListener(handle);
            registered = false;
            if (source != null)
            {
                try { source.RemoveHook(hook); }
                catch (ObjectDisposedException) { }
                source = null;
            }
            ownWrites.Clear();
            writeOrder.Clear();
        }

        private sealed class LimitedMemoryStream : MemoryStream
        {
            private readonly int maximum;
            internal LimitedMemoryStream(int maximum) { this.maximum = maximum; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Position + count > maximum) throw new InvalidDataException("图片超过 32 MB，暂不记录。");
                base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                if (Position >= maximum) throw new InvalidDataException("图片超过 32 MB，暂不记录。");
                base.WriteByte(value);
            }
            public override void SetLength(long value)
            {
                if (value > maximum) throw new InvalidDataException("图片超过 32 MB，暂不记录。");
                base.SetLength(value);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
    }
}
