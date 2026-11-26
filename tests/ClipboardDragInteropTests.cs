using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Orbit
{
    // Native transfer-format checks only. No Clipboard API, OS mouse input,
    // Explorer window, registered drop target or real drag loop is used.
    // --keep-fixtures retains this run's isolated temporary exports for a
    // separate, explicitly invoked headless-browser acceptance fixture.
    internal static class ClipboardDragInteropTests
    {
        private static int passed;
        private static string fixtureRoot;

        [STAThread]
        public static int Main(string[] args)
        {
            fixtureRoot = Path.Combine(Path.GetTempPath(), "OrbitDragInterop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            bool retain = args.Contains("--keep-fixtures");
            try
            {
                var exporter = new ClipboardDragExport(fixtureRoot);
                string text = "Orbit 中文拖出\r\nsecond line — café 🌙";
                var textPayload = exporter.Prepare(new ClipboardEntry
                {
                    Id = "interop-text", Text = text, FileName = "拖动文字.txt", CapturedAtUtc = DateTimeOffset.UtcNow
                });
                var textNative = (ComDataObject)textPayload.Data;
                Assert(ReadUnicodeText(textNative) == text, "CF_UNICODETEXT preserves Unicode, line breaks and emoji");
                Assert(File.ReadAllText(textPayload.FilePath, Encoding.UTF8) == text, "Text export is a readable UTF-8 file");
                VerifyFileDrop(textNative, textPayload.FilePath, "text");
                VerifyCopyEffect(textNative);
                Assert(ReadUnicodeText(textNative) == text, "Retrieving CF_HDROP does not consume UnicodeText");

                byte[] png = TransparentPng();
                var imagePayload = exporter.Prepare(new ClipboardEntry
                {
                    Id = "interop-image", ImagePng = png, FileName = "透明样图.png", PixelWidth = 2, PixelHeight = 2,
                    CapturedAtUtc = DateTimeOffset.UtcNow
                });
                var imageNative = (ComDataObject)imagePayload.Data;
                VerifyFileDrop(imageNative, imagePayload.FilePath, "image");
                VerifyCopyEffect(imageNative);
                byte[] nativePng = ReadGlobalBytes(imageNative, RegisteredFormat("PNG"));
                Assert(nativePng.Take(png.Length).SequenceEqual(png), "PNG native HGLOBAL begins with exact PNG bytes");
                Assert(ReadGlobalBytes(imageNative, RegisteredFormat("image/png")).Take(png.Length).SequenceEqual(png), "image/png native HGLOBAL carries the same PNG");
                Assert(File.ReadAllBytes(imagePayload.FilePath).SequenceEqual(png), "FileDrop PNG preserves original bytes");
                VerifyAlpha(nativePng);
                FORMATETC bitmapFormat = Format(2, TYMED.TYMED_GDI);
                Assert(imageNative.QueryGetData(ref bitmapFormat) == 0, "CF_BITMAP is advertised as native TYMED_GDI");
                STGMEDIUM bitmapMedium;
                imageNative.GetData(ref bitmapFormat, out bitmapMedium);
                try
                {
                    Assert(bitmapMedium.tymed == TYMED.TYMED_GDI && bitmapMedium.unionmember != IntPtr.Zero, "CF_BITMAP returns a nonempty GDI bitmap");
                    using (var bitmap = System.Drawing.Image.FromHbitmap(bitmapMedium.unionmember))
                        Assert(bitmap.Width == 2 && bitmap.Height == 2, "Native GDI bitmap has expected dimensions");
                }
                finally { ReleaseStgMedium(ref bitmapMedium); }
                Assert(ReadGlobalBytes(imageNative, RegisteredFormat("PNG")).Take(png.Length).SequenceEqual(png), "Repeated native retrieval keeps PNG available");

                string ordinarySource = Path.Combine(fixtureRoot, "原始文件.PDF");
                byte[] ordinaryBytes = { 37, 80, 68, 70, 45, 0, 255, 13, 10 };
                File.WriteAllBytes(ordinarySource, ordinaryBytes);
                var ordinaryPayload = exporter.Prepare(new ClipboardEntry
                {
                    Id = "interop-ordinary", FilePath = ordinarySource, FileName = Path.GetFileName(ordinarySource),
                    CapturedAtUtc = DateTimeOffset.UtcNow
                });
                File.Delete(ordinarySource);
                var ordinaryNative = (ComDataObject)ordinaryPayload.Data;
                VerifyFileDrop(ordinaryNative, ordinaryPayload.FilePath, "ordinary file");
                VerifyCopyEffect(ordinaryNative);
                Assert(Path.GetFileName(ordinaryPayload.FilePath) == "原始文件.PDF" && File.ReadAllBytes(ordinaryPayload.FilePath).SequenceEqual(ordinaryBytes),
                    "Native ordinary FileDrop keeps the real filename and bytes after source deletion");
                FORMATETC textFormat = Format(13, TYMED.TYMED_HGLOBAL);
                Assert(ordinaryNative.QueryGetData(ref textFormat) != 0, "Ordinary file payload does not advertise fabricated UnicodeText");

                // A target resolves CF_HDROP paths and reads/copies bytes itself.
                // This receiver is isolated in our own temporary directory.
                string receiverDirectory = Path.Combine(fixtureRoot, "receiver");
                Directory.CreateDirectory(receiverDirectory);
                string targetText = Path.Combine(receiverDirectory, Path.GetFileName(textPayload.FilePath));
                string targetImage = Path.Combine(receiverDirectory, Path.GetFileName(imagePayload.FilePath));
                File.Copy(ReadFiles(textNative)[0], targetText, false);
                File.Copy(ReadFiles(imageNative)[0], targetImage, false);
                Assert(File.ReadAllText(targetText, Encoding.UTF8) == text, "Isolated receiver can copy exported text");
                Assert(File.ReadAllBytes(targetImage).SequenceEqual(png), "Isolated receiver can copy exported image");
                Assert(File.Exists(textPayload.FilePath) && File.Exists(imagePayload.FilePath), "Copy receiver leaves both source exports intact");

                File.WriteAllText(Path.Combine(fixtureRoot, "text-path.txt"), textPayload.FilePath, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(fixtureRoot, "image-path.txt"), imagePayload.FilePath, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(fixtureRoot, "expected-text.txt"), text, new UTF8Encoding(false));
                Console.WriteLine("PASS: " + passed + " native IDataObject transfer-format checks.");
                Console.WriteLine("Scope: in-process COM GetData + native HGLOBAL/GDI reads and isolated receiver copies; not a cross-application drag test.");
                if (retain) Console.WriteLine("FIXTURE_ROOT=" + fixtureRoot);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                if (!retain) CleanupOwnDirectory();
            }
        }

        private static void VerifyFileDrop(ComDataObject source, string expectedPath, string label)
        {
            string[] paths = ReadFiles(source);
            Assert(paths.Length == 1, label + " CF_HDROP contains exactly one file");
            Assert(paths[0] == expectedPath && Path.IsPathRooted(paths[0]), label + " CF_HDROP has exact fully qualified Unicode path");
            Assert(File.Exists(paths[0]), label + " native advertised file already exists");
        }

        private static void VerifyCopyEffect(ComDataObject source)
        {
            byte[] effect = ReadGlobalBytes(source, RegisteredFormat("Preferred DropEffect"));
            Assert(effect.Length >= 4 && BitConverter.ToInt32(effect, 0) == 1, "Preferred DropEffect native DWORD requests Copy");
        }

        private static string[] ReadFiles(ComDataObject source)
        {
            FORMATETC format = Format(15, TYMED.TYMED_HGLOBAL);
            if (source.QueryGetData(ref format) != 0) throw new Exception("CF_HDROP is unavailable.");
            STGMEDIUM medium;
            source.GetData(ref format, out medium);
            try
            {
                if (medium.tymed != TYMED.TYMED_HGLOBAL) throw new Exception("CF_HDROP did not provide HGLOBAL.");
                uint count = DragQueryFile(medium.unionmember, UInt32.MaxValue, null, 0);
                if (count > 10) throw new Exception("Unexpected file count.");
                string[] paths = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    uint length = DragQueryFile(medium.unionmember, i, null, 0);
                    var path = new StringBuilder(checked((int)length + 1));
                    if (DragQueryFile(medium.unionmember, i, path, length + 1) != length) throw new Exception("DragQueryFile length mismatch.");
                    paths[i] = path.ToString();
                }
                return paths;
            }
            finally { ReleaseStgMedium(ref medium); }
        }

        private static string ReadUnicodeText(ComDataObject source)
        {
            byte[] bytes = ReadGlobalBytes(source, 13);
            string text = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
            int terminator = text.IndexOf('\0');
            return terminator >= 0 ? text.Substring(0, terminator) : text;
        }

        private static byte[] ReadGlobalBytes(ComDataObject source, short formatId)
        {
            FORMATETC format = Format(formatId, TYMED.TYMED_HGLOBAL);
            if (source.QueryGetData(ref format) != 0) throw new Exception("Native format unavailable: " + formatId);
            STGMEDIUM medium;
            source.GetData(ref format, out medium);
            try
            {
                if (medium.tymed != TYMED.TYMED_HGLOBAL) throw new Exception("Expected HGLOBAL.");
                ulong size = GlobalSize(medium.unionmember).ToUInt64();
                if (size == 0 || size > 4 * 1024 * 1024) throw new Exception("Unexpected fixture HGLOBAL size.");
                IntPtr data = GlobalLock(medium.unionmember);
                if (data == IntPtr.Zero) throw new Exception("GlobalLock failed.");
                try
                {
                    byte[] bytes = new byte[(int)size];
                    Marshal.Copy(data, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally { GlobalUnlock(medium.unionmember); }
            }
            finally { ReleaseStgMedium(ref medium); }
        }

        private static FORMATETC Format(short format, TYMED medium)
        {
            return new FORMATETC { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, ptd = IntPtr.Zero, tymed = medium };
        }

        private static short RegisteredFormat(string name)
        {
            // Name registration does not open or alter the system clipboard.
            uint id = RegisterClipboardFormat(name);
            if (id == 0) throw new Exception("Format registration failed.");
            return unchecked((short)id);
        }

        private static byte[] TransparentPng()
        {
            byte[] pixels = { 0, 0, 255, 128, 0, 255, 0, 255, 255, 0, 0, 0, 20, 30, 40, 255 };
            BitmapSource source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var output = new MemoryStream()) { encoder.Save(output); return output.ToArray(); }
        }

        private static void VerifyAlpha(byte[] png)
        {
            using (var input = new MemoryStream(png, false))
            {
                BitmapSource frame = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                byte[] pixels = new byte[16];
                converted.CopyPixels(pixels, 8, 0);
                Assert(pixels[3] == 128 && pixels[7] == 255 && pixels[11] == 0, "Native PNG retains partial, opaque and transparent alpha");
            }
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void CleanupOwnDirectory()
        {
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string actual = Path.GetFullPath(fixtureRoot);
            if (!String.Equals(Path.GetDirectoryName(actual), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(actual).StartsWith("OrbitDragInterop-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to clean a directory outside this fixture.");
            Directory.Delete(actual, true);
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
        private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder path, uint length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
        private static extern uint RegisterClipboardFormat(string name);
    }
}
