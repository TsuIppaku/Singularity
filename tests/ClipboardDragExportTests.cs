using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace Orbit
{
    internal static class ClipboardDragExportTests
    {
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 11, 13, 17, 0, 0, TimeSpan.Zero);
        private static int failures;
        private static string temporaryRoot;

        [STAThread]
        private static int Main()
        {
            temporaryRoot = Path.Combine(Path.GetTempPath(), "Orbit.ClipboardDragExportTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                Run("text retains complete Unicode and native file-drop formats", TextExport);
                Run("PNG bytes, dimensions and readable file names survive export", ImageExport);
                Run("file names are safe and source directories are not disclosed", SafeNames);
                Run("repeated exports reuse files and refresh retention", ReuseAndRetention);
                Run("history eviction and service lifetime do not invalidate exports", IndependentLifetime);
                Run("cleanup only removes owned expired files", CleanupBoundaries);
                Run("invalid input and occupied directories preserve existing files", InvalidAndOccupied);
                Console.WriteLine(failures == 0 ? "All 7 clipboard drag export test groups passed." : failures + " clipboard drag export test group(s) failed.");
                return failures == 0 ? 0 : 1;
            }
            finally
            {
                string resolved = Path.GetFullPath(temporaryRoot);
                string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (String.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("Orbit.ClipboardDragExportTests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
            }
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
        }

        private static string DirectoryFor(string name)
        {
            string path = Path.Combine(temporaryRoot, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static ClipboardEntry TextEntry(string id, string text)
        {
            return new ClipboardEntry { Id = id, Text = text, CapturedAtUtc = Epoch };
        }

        private static ClipboardEntry ImageEntry(string id, string fileName)
        {
            return new ClipboardEntry { Id = id, FileName = fileName, ImagePng = MakePng(17, 11), PixelWidth = 17, PixelHeight = 11, CapturedAtUtc = Epoch };
        }

        private static void TextExport()
        {
            var service = new ClipboardDragExport(DirectoryFor("text"));
            string text = "第一行\r\nsecond line\n\t空格 🪐 e\u0301\r\n" + new String('长', 1000001);
            ClipboardDragPayload payload = service.Prepare(TextEntry("text-one", text));
            Assert(File.ReadAllText(payload.FilePath, Encoding.UTF8) == text, "The exported text file must preserve all lines, whitespace, Unicode and long text.");
            Assert(File.ReadAllBytes(payload.FilePath).SequenceEqual(new UTF8Encoding(false).GetBytes(text)), "Text export must be exact UTF-8 without a hidden prefix.");
            Assert(payload.Data.GetText(TextDataFormat.UnicodeText) == text, "UnicodeText must contain the full original content.");
            Assert(Path.GetExtension(payload.FilePath) == ".txt", "Text exports must use a .txt extension.");
            AssertDropFormats(payload);
            Assert(!payload.Data.GetDataPresent("Orbit.Clipboard.Origin.v1", false), "Drag payload must not pretend to be an internal clipboard write.");
        }

        private static void ImageExport()
        {
            var service = new ClipboardDragExport(DirectoryFor("image"));
            ClipboardEntry entry = ImageEntry("image-one", "风景 原图.JPEG");
            byte[] before = (byte[])entry.ImagePng.Clone();
            ClipboardDragPayload payload = service.Prepare(entry);
            Assert(Path.GetFileName(payload.FilePath) == "风景 原图.png", "The original human-readable stem remains while the extension matches PNG encoding.");
            Assert(File.ReadAllBytes(payload.FilePath).SequenceEqual(before), "PNG file bytes must be preserved without resizing or recompression.");
            using (var bitmap = new Bitmap(payload.FilePath)) Assert(bitmap.Width == 17 && bitmap.Height == 11, "The file retains original pixel dimensions.");
            Assert(payload.Data.GetImage().PixelWidth == 17 && payload.Data.GetImage().PixelHeight == 11, "The native bitmap payload has the full original dimensions.");
            foreach (string format in new[] { "PNG", "image/png" })
            {
                var stream = payload.Data.GetData(format, false) as MemoryStream;
                Assert(stream != null && stream.ToArray().SequenceEqual(before), "The " + format + " format must retain original bytes.");
            }
            AssertDropFormats(payload);
            entry.ImagePng[0] = 0;
            Assert(File.ReadAllBytes(payload.FilePath).SequenceEqual(before), "Mutating the source entry must not alter the export.");
        }

        private static void SafeNames()
        {
            string directory = DirectoryFor("names");
            var service = new ClipboardDragExport(directory);
            string[] names = { "C:\\secret\\照片.jpg", "../../目录/作品.webp", "CON.jpg", "LPT1.jpeg", "COM¹.png", "..", "bad<>:\"|?*\0.png", new String('名', 160) + ".jpeg" };
            foreach (string name in names)
            {
                ClipboardDragPayload payload = service.Prepare(ImageEntry(Guid.NewGuid().ToString("N"), name));
                string actual = Path.GetFileName(payload.FilePath);
                Assert(actual.EndsWith(".png", StringComparison.Ordinal) && actual.Length <= 85 && actual.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
                    "Exports must use bounded valid file names: " + actual);
                Assert(Path.GetDirectoryName(Path.GetDirectoryName(payload.FilePath)) == Path.Combine(directory, "clipboard-drag-exports"), "File names cannot escape the private export directory.");
                Assert(!payload.FilePath.Contains("secret") && !payload.FilePath.Contains("目录"), "Original source folders must not be included in export paths.");
                Assert(File.Exists(payload.FilePath), "Even reserved or unusual names must produce usable files.");
            }
        }

        private static void ReuseAndRetention()
        {
            string directory = DirectoryFor("retention");
            DateTimeOffset now = Epoch;
            var service = new ClipboardDragExport(directory, delegate { return now; });
            var entry = TextEntry("stable", "repeat");
            string first = service.Prepare(entry).FilePath;
            now = Epoch.AddDays(6);
            string second = service.Prepare(entry).FilePath;
            Assert(first == second && Directory.GetDirectories(Path.Combine(directory, "clipboard-drag-exports")).Length == 1, "Repeated drags of one item must reuse one file.");
            now = Epoch.AddDays(8);
            new ClipboardDragExport(directory, delegate { return now; });
            Assert(File.Exists(first), "Retention must be refreshed by reuse, not measured from initial creation.");
            var reopened = new ClipboardDragExport(directory, delegate { return now; });
            Assert(reopened.Prepare(entry).FilePath == first, "Reusing an item after application restart must keep the same file path.");
            File.WriteAllText(first, "target edited its copy");
            Assert(reopened.Prepare(entry).FilePath == first && File.ReadAllText(first) == "repeat", "A reused export restores the original content if a target modified its private file.");
            now = Epoch.AddDays(16);
            new ClipboardDragExport(directory, delegate { return now; });
            Assert(!File.Exists(first) && !Directory.Exists(Path.GetDirectoryName(first)), "Owned exports expire only after seven days since the latest use.");
        }

        private static void IndependentLifetime()
        {
            string directory = DirectoryFor("history-independent");
            var history = new ClipboardHistory(directory);
            Assert(history.Add(ImageEntry("old-image", "淘汰后仍可上传.jpg")), "Initial history image saves.");
            var service = new ClipboardDragExport(directory);
            ClipboardDragPayload payload = service.Prepare(history.Entries[0]);
            byte[] original = File.ReadAllBytes(payload.FilePath);
            for (int index = 0; index < 3; index++) Assert(history.Add(TextEntry("next-" + index, "new text")), "Additional capture saves.");
            Assert(history.Entries.All(entry => entry.Id != "old-image"), "The source image has been evicted from history.");
            Assert(Directory.GetFiles(Path.Combine(directory, "clipboard-history-images")).Length == 0, "The source image's history cache has been cleaned.");
            service = null;
            Assert(File.ReadAllBytes(payload.FilePath).SequenceEqual(original), "The exported file remains readable after history eviction and panel/service closure.");
            AssertDropFormats(payload);
        }

        private static void CleanupBoundaries()
        {
            string directory = DirectoryFor("cleanup");
            var service = new ClipboardDragExport(directory, delegate { return Epoch; });
            string removable = service.Prepare(TextEntry("expire", "old owned file")).FilePath;
            string extraFileExport = service.Prepare(TextEntry("extra", "owned plus other")).FilePath;
            string unrelatedFile = Path.Combine(Path.GetDirectoryName(extraFileExport), "用户文件.txt");
            File.WriteAllText(unrelatedFile, "keep");
            string subdirectoryExport = service.Prepare(TextEntry("subdirectory", "owned plus directory")).FilePath;
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(subdirectoryExport), "user-folder"));
            string damaged = service.Prepare(TextEntry("damaged", "preserve malformed manifest")).FilePath;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(damaged), ".orbit-drag-export.json"), "not json");
            string exportRoot = Path.Combine(directory, "clipboard-drag-exports");
            string unknown = Path.Combine(exportRoot, "export-" + new String('a', 32));
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown, "mine.txt"), "not owned");
            string outside = Path.Combine(directory, "do-not-delete.txt");
            File.WriteAllText(outside, "outside");
            string traversal = service.Prepare(TextEntry("traversal", "owned fixture")).FilePath;
            string traversalMarker = Path.Combine(Path.GetDirectoryName(traversal), ".orbit-drag-export.json");
            string marker = File.ReadAllText(traversalMarker).Replace("剪贴板文字.txt", "..\\\\..\\\\do-not-delete.txt");
            File.WriteAllText(traversalMarker, marker);

            new ClipboardDragExport(directory, delegate { return Epoch.AddDays(8); });
            Assert(!File.Exists(removable), "An expired valid owned export should be cleaned.");
            Assert(File.Exists(extraFileExport) && File.ReadAllText(unrelatedFile) == "keep", "Cleanup must leave exports containing unrelated files untouched.");
            Assert(File.Exists(subdirectoryExport) && File.Exists(damaged) && Directory.Exists(unknown), "Unexpected directories, malformed manifests and unowned names must remain untouched.");
            Assert(File.Exists(traversal) && File.ReadAllText(outside) == "outside", "Manifest traversal must not permit removal outside its own directory.");
        }

        private static void InvalidAndOccupied()
        {
            string directory = DirectoryFor("invalid");
            var service = new ClipboardDragExport(directory);
            AssertThrows(delegate { service.Prepare(null); }, "Null entries must fail clearly.");
            AssertThrows(delegate { service.Prepare(new ClipboardEntry()); }, "Empty entries must fail clearly.");
            AssertThrows(delegate { service.Prepare(new ClipboardEntry { ImagePng = new byte[] { 1, 2, 3 } }); }, "Invalid PNG bytes must not be exported with a PNG extension.");
            Assert(!Directory.Exists(Path.Combine(directory, "clipboard-drag-exports")), "Validation failures must not leave export files behind.");
            string occupied = DirectoryFor("occupied");
            string occupiedRoot = Path.Combine(occupied, "clipboard-drag-exports");
            File.WriteAllText(occupiedRoot, "existing user file");
            var blocked = new ClipboardDragExport(occupied);
            AssertThrows(delegate { blocked.Prepare(TextEntry("blocked", "new")); }, "An occupied export root must fail instead of replacing existing data.");
            Assert(File.ReadAllText(occupiedRoot) == "existing user file", "An occupied path must retain its original bytes.");
        }

        private static void AssertDropFormats(ClipboardDragPayload payload)
        {
            Assert(payload.Data.GetDataPresent(DataFormats.FileDrop, false), "The payload must provide an explicit native file-drop format.");
            Assert(payload.Data.GetFileDropList().Count == 1 && payload.Data.GetFileDropList()[0] == payload.FilePath, "FileDrop must point to the actual exported file.");
            var effect = payload.Data.GetData("Preferred DropEffect", false) as MemoryStream;
            Assert(effect != null && effect.ToArray().SequenceEqual(new byte[] { 1, 0, 0, 0 }), "Preferred DropEffect must be the native DWORD for Copy.");
        }

        private static byte[] MakePng(int width, int height)
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (var output = new MemoryStream())
            {
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, Color.FromArgb(100 + x + y, x * 11, y * 17, x + y));
                bitmap.Save(output, ImageFormat.Png);
                return output.ToArray();
            }
        }

        private static void AssertThrows(Action action, string message)
        {
            try { action(); }
            catch (Exception) { return; }
            throw new Exception(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
