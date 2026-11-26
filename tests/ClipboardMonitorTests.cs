using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Orbit
{
    /// <summary>Codec and event-flow fixtures. Never calls the real Clipboard API.</summary>
    internal static class ClipboardMonitorTests
    {
        private static int checks;
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 13, 14, 30, 12, TimeSpan.Zero);

        [STAThread]
        public static int Main()
        {
            string directory = Path.Combine(Path.GetTempPath(), "Orbit-clipboard-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                if (Application.Current == null) new Application();
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                TestText();
                TestPngAndBitmap();
                TestDib();
                TestFiles(directory);
                TestInvalidImages();
                TestStartupBaseline();
                TestTransport();
                Console.WriteLine("PASS " + checks + " clipboard monitor checks; all clipboard access used in-memory fixtures.");
                foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
                Directory.Delete(directory);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine("Fixtures retained at: " + directory);
                return 1;
            }
        }

        private static void TestText()
        {
            string text = "你好，Orbit 🌱\r\nhttps://example.invalid/image.png";
            ClipboardEntry entry = ClipboardMonitor.ReadData(TextData(text), Now);
            Check(entry != null && !entry.IsImage && entry.Text == text, "Unicode text preserved without fetching links");
            Check(entry.CapturedAtUtc == Now && !String.IsNullOrEmpty(entry.Id), "capture timestamp and id assigned");
            Check(ClipboardMonitor.ReadData(new DataObject(), Now) == null, "empty clipboard ignored");
            DataObject output = ClipboardMonitor.CreateDataObject(entry, "fixture-token");
            Check(output.GetDataPresent(DataFormats.UnicodeText, false), "text copy exposes UnicodeText");
            Check((string)output.GetData(DataFormats.UnicodeText, false) == text, "text copy keeps exact content");
            Check(output.GetDataPresent(ClipboardMonitor.OriginFormat, false), "self-write marker present");
            Throws<InvalidDataException>(delegate { ClipboardMonitor.ReadData(TextData(new string('x', ClipboardMonitor.MaximumTextCharacters + 1)), Now); }, "oversized text rejected");
        }

        private static void TestPngAndBitmap()
        {
            byte[] pixels = { 10, 20, 30, 0, 0, 0, 255, 128, 0, 255, 0, 255, 255, 0, 0, 64 };
            BitmapSource source = Bitmap(pixels);
            byte[] png = Encode(source, new PngBitmapEncoder());
            var stream = new MemoryStream(png, false);
            stream.Position = stream.Length;
            var data = new DataObject();
            data.SetData("PNG", stream, false);
            data.SetImage(Bitmap(new byte[] { 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255 }));
            data.SetText("https://example.invalid/original.png", TextDataFormat.UnicodeText);
            ClipboardEntry entry = ClipboardMonitor.ReadData(data, Now);
            Check(entry.IsImage && entry.PixelWidth == 2 && entry.PixelHeight == 2, "PNG wins over bitmap and link text");
            byte[] actual = Pixels(entry.ImagePng);
            Check(actual[3] == 0 && actual[7] == 128 && actual[11] == 255 && actual[15] == 64, "PNG alpha preserved");
            Check(actual[6] == 255 && actual[9] == 255, "PNG colors preserved");
            Check(stream.CanRead && stream.Position == stream.Length, "caller-owned PNG stream stays open at its original position");
            Check(entry.FileName.StartsWith("图片-", StringComparison.Ordinal) && entry.FileName.EndsWith(".png", StringComparison.Ordinal), "unnamed image gets an honest generated filename");

            DataObject copied = ClipboardMonitor.CreateDataObject(entry, "png-copy");
            Check(copied.GetDataPresent("PNG", false) && copied.GetDataPresent("image/png", false), "image copy exposes PNG formats");
            Check(copied.GetDataPresent(DataFormats.Bitmap, true), "image copy exposes native bitmap representation");
            ClipboardEntry copiedEntry = ClipboardMonitor.ReadData(copied, Now);
            Check(Pixels(copiedEntry.ImagePng)[7] == 128, "image copy roundtrip keeps PNG transparency");

            byte[] zeroAlpha = { 0, 0, 200, 0, 0, 180, 0, 0, 160, 0, 0, 0, 30, 40, 50, 0 };
            var bitmapData = new DataObject();
            bitmapData.SetImage(Bitmap(zeroAlpha));
            byte[] bitmapPixels = Pixels(ClipboardMonitor.ReadData(bitmapData, Now).ImagePng);
            Check(bitmapPixels[3] == 255 && bitmapPixels[7] == 255 && bitmapPixels[2] == 200, "legacy bitmap zero alpha becomes opaque without losing color");
            var pngData = new DataObject();
            pngData.SetData("PNG", new MemoryStream(Encode(Bitmap(zeroAlpha), new PngBitmapEncoder()), false), false);
            byte[] pngPixels = Pixels(ClipboardMonitor.ReadData(pngData, Now).ImagePng);
            Check(pngPixels[3] == 0 && pngPixels[7] == 0, "fully transparent PNG is not treated as a legacy bitmap");
        }

        private static void TestDib()
        {
            byte[] topDown = { 0, 0, 220, 0, 0, 210, 0, 0, 200, 0, 0, 0, 80, 90, 100, 0 };
            byte[] dib = MakeDib(topDown, false, false);
            var data = new FixtureData();
            data.SetData(DataFormats.Dib, new MemoryStream(dib, false), false);
            ClipboardEntry entry = ClipboardMonitor.ReadData(data, Now);
            byte[] actual = Pixels(entry.ImagePng);
            Check(actual[2] == 220 && actual[4 + 1] == 210 && actual[8] == 200, "bottom-up DIB row order decoded correctly");
            Check(actual[3] == 255 && actual[15] == 255, "BI_RGB undefined alpha becomes opaque");
            var direct = new FixtureData();
            direct.SetData(DataFormats.Dib, new MemoryStream(MakeDib(topDown, true, false), false), false);
            Check(Pixels(ClipboardMonitor.ReadData(direct, Now).ImagePng)[2] == 220, "top-down DIB supported");

            byte[] alpha = { 0, 0, 220, 0, 0, 210, 0, 128, 200, 0, 0, 255, 80, 90, 100, 64 };
            var v5 = new FixtureData();
            v5.SetData(DataFormats.Dib, new MemoryStream(MakeDib(alpha, false, true), false), false);
            byte[] v5Pixels = Pixels(ClipboardMonitor.ReadData(v5, Now).ImagePng);
            Check(v5Pixels[3] == 0 && v5Pixels[7] == 128 && v5Pixels[15] == 64, "explicit DIBV5 alpha masks preserve transparency");
        }

        private static void TestFiles(string directory)
        {
            string textPath = Path.Combine(directory, "普通文件.txt");
            string imagePath = Path.Combine(directory, "原名-透明图.png");
            string secondPath = Path.Combine(directory, "第二张.png");
            byte[] image = Encode(Bitmap(new byte[] { 0, 0, 255, 128, 0, 255, 0, 255, 255, 0, 0, 255, 1, 2, 3, 255 }), new PngBitmapEncoder());
            File.WriteAllText(textPath, "ordinary file");
            File.WriteAllBytes(imagePath, image);
            File.WriteAllBytes(secondPath, image);
            var files = new DataObject();
            files.SetFileDropList(new StringCollection { textPath, imagePath, secondPath });
            string notice;
            ClipboardEntry entry = ClipboardMonitor.ReadData(files, Now, out notice);
            Check(entry.FileName == "原名-透明图.png", "FileDrop preserves the real first supported image filename");
            Check(!String.IsNullOrEmpty(notice) && notice.Contains("首张"), "multiple selection is explained");
            File.Delete(imagePath);
            Check(!File.Exists(imagePath), "OnLoad capture leaves no source file lock");
            ClipboardEntry copied = ClipboardMonitor.ReadData(ClipboardMonitor.CreateDataObject(entry, "deleted-original"), Now);
            Check(copied.IsImage && Pixels(copied.ImagePng)[3] == 128, "cached image copies after the original file was deleted");

            var plainFiles = new DataObject();
            plainFiles.SetFileDropList(new StringCollection { textPath });
            Check(ClipboardMonitor.ReadData(plainFiles, Now, out notice) == null && notice.Contains("文件"), "ordinary files are not imported");

            string broken = Path.Combine(directory, "broken.png");
            File.WriteAllText(broken, "not an image");
            var mixed = new DataObject();
            mixed.SetFileDropList(new StringCollection { broken, secondPath });
            Check(ClipboardMonitor.ReadData(mixed, Now).FileName == "第二张.png", "multiple selection skips an unreadable image");

            string bmpPath = Path.Combine(directory, "传统截图.bmp");
            byte[] dib = MakeDib(new byte[] { 0, 0, 230, 0, 0, 220, 0, 0, 210, 0, 0, 0, 5, 10, 15, 0 }, false, false);
            using (var output = File.Create(bmpPath))
            using (var writer = new BinaryWriter(output))
            { writer.Write((ushort)0x4d42); writer.Write(dib.Length + 14); writer.Write(0); writer.Write(54); writer.Write(dib); }
            var bmpFiles = new DataObject();
            bmpFiles.SetFileDropList(new StringCollection { bmpPath });
            ClipboardEntry bmp = ClipboardMonitor.ReadData(bmpFiles, Now);
            Check(bmp.FileName == "传统截图.bmp" && Pixels(bmp.ImagePng)[3] == 255, "BMP FileDrop preserves its name and opaque screenshot pixels");

            string gifPath = Path.Combine(directory, "动画.gif");
            var gif = new GifBitmapEncoder();
            gif.Frames.Add(BitmapFrame.Create(Bitmap(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 })));
            gif.Frames.Add(BitmapFrame.Create(Bitmap(new byte[] { 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255 })));
            using (var output = File.Create(gifPath)) gif.Save(output);
            var gifFiles = new DataObject();
            gifFiles.SetFileDropList(new StringCollection { gifPath });
            ClipboardEntry gifEntry = ClipboardMonitor.ReadData(gifFiles, Now, out notice);
            Check(notice != null && notice.Contains("第一帧") && Pixels(gifEntry.ImagePng)[2] > 200, "animated image uses the first frame and explains it");
        }

        private static void TestInvalidImages()
        {
            var bad = new DataObject();
            bad.SetData("PNG", new MemoryStream(Encoding.ASCII.GetBytes("not-a-valid-image"), false), false);
            Throws<InvalidDataException>(delegate { ClipboardMonitor.ReadData(bad, Now); }, "corrupt image rejected cleanly");
            bad.SetImage(Bitmap(new byte[] { 0, 0, 190, 255, 0, 0, 190, 255, 0, 0, 190, 255, 0, 0, 190, 255 }));
            Check(ClipboardMonitor.ReadData(bad, Now).IsImage, "valid bitmap fallback survives malformed PNG format");

            byte[] hugeHeader = MakeDib(new byte[16], false, false);
            Buffer.BlockCopy(BitConverter.GetBytes(int.MaxValue), 0, hugeHeader, 4, 4);
            var huge = new FixtureData();
            huge.SetData(DataFormats.Dib, new MemoryStream(hugeHeader, false), false);
            Throws<InvalidDataException>(delegate { ClipboardMonitor.ReadData(huge, Now); }, "huge DIB rejected before pixel allocation");

            var oversized = new FixtureData();
            oversized.SetData("PNG", new byte[ClipboardMonitor.MaximumImageBytes + 1], false);
            Throws<InvalidDataException>(delegate { ClipboardMonitor.ReadData(oversized, Now); }, "encoded size cap enforced");
        }

        private static void TestStartupBaseline()
        {
            var window = new Window();
            var oldImage = new DataObject();
            oldImage.SetImage(Bitmap(new byte[] { 0, 0, 200, 255, 0, 180, 0, 255, 160, 0, 0, 255, 30, 40, 50, 255 }));
            var transport = new FakeTransport { Sequence = 40, Data = oldImage };
            var captures = new List<ClipboardEntry>();
            using (var monitor = new ClipboardMonitor(window, captures.Add, null,
                delegate { return transport.Sequence; }, transport.Read, transport.Write))
            {
                Check(monitor.RegisterWithBaseline(delegate { return true; }), "fake listener registration succeeds");
                Pump(30);
                Check(captures.Count == 0 && transport.ReadCount == 0, "startup does not replay or read an existing clipboard image");
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 0 && transport.ReadCount == 0, "notification for the startup baseline is ignored");
                transport.Sequence++; transport.Data = TextData("first new copy after startup");
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 1 && captures[0].Text == "first new copy after startup", "first genuinely new copy after startup is captured");
                transport.Sequence++; transport.Data = oldImage;
                monitor.RequestCapture(); Pump(30);
                transport.Sequence++;
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 2 && captures[1].IsImage, "Only the first image is recorded during the short cooldown");
                transport.Sequence++; transport.Data = TextData("text during image cooldown");
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 3 && captures[2].Text == "text during image cooldown", "Image cooldown does not suppress text");
                Pump(150);
                transport.Sequence++; transport.Data = oldImage;
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 4 && captures[3].IsImage, "Images are recorded again after the cooldown");
            }

            captures.Clear();
            transport = new FakeTransport { Sequence = 70, Data = TextData("before registration") };
            using (var monitor = new ClipboardMonitor(window, captures.Add, null,
                delegate { return transport.Sequence; }, transport.Read, transport.Write))
            {
                monitor.RegisterWithBaseline(delegate
                {
                    transport.Sequence++; transport.Data = TextData("copied during registration"); return true;
                });
                Pump(30);
                Check(captures.Count == 1 && captures[0].Text == "copied during registration", "known sequence change during registration is captured");
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 1 && transport.ReadCount == 1, "queued native notification does not replay the registration-race capture");
            }

            uint[] beforeValues = { 0, 0, 80 };
            uint[] afterValues = { 0, 80, 0 };
            for (int i = 0; i < beforeValues.Length; i++)
            {
                captures.Clear();
                transport = new FakeTransport { Sequence = beforeValues[i], Data = TextData("existing unknown-sequence clipboard") };
                uint afterRegistration = afterValues[i];
                using (var monitor = new ClipboardMonitor(window, captures.Add, null,
                    delegate { return transport.Sequence; }, transport.Read, transport.Write))
                {
                    monitor.RegisterWithBaseline(delegate { transport.Sequence = afterRegistration; return true; });
                    Pump(30);
                    Check(captures.Count == 0 && transport.ReadCount == 0, "unknown registration sequence does not imply a new copy: case " + i);
                    if (beforeValues[i] != 0 || afterValues[i] != 0)
                    {
                        transport.Sequence = 80;
                        monitor.RequestCapture(); Pump(30);
                        Check(captures.Count == 0 && transport.ReadCount == 0, "known side of an unknown registration boundary is retained: case " + i);
                    }
                    // Even when the sequence API remains unavailable, an actual
                    // later notification must still be able to capture data.
                    transport.Sequence = i == 0 ? 0u : 81u;
                    transport.Data = TextData("new copy with earlier unknown baseline");
                    monitor.RequestCapture(); Pump(30);
                    Check(captures.Count == 1 && captures[0].Text == "new copy with earlier unknown baseline", "next notification works after an unknown startup sequence: case " + i);
                }
            }

            captures.Clear();
            transport = new FakeTransport { Sequence = 90, Data = TextData("before failed registration") };
            using (var monitor = new ClipboardMonitor(window, captures.Add, null,
                delegate { return transport.Sequence; }, transport.Read, transport.Write))
            {
                Check(!monitor.RegisterWithBaseline(delegate { return false; }), "failed listener registration is reported to Attach");
                Pump(30);
                Check(captures.Count == 0 && transport.ReadCount == 0, "failed registration never queues a clipboard read");
                transport.Sequence++;
                monitor.RegisterWithBaseline(delegate { return true; });
                Pump(30);
                Check(captures.Count == 0 && transport.ReadCount == 0, "successful registration retry establishes its own baseline");
                transport.Sequence++; transport.Data = TextData("new copy after registration retry");
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 1 && captures[0].Text == "new copy after registration retry", "first new event after a registration retry is captured");
            }
            window.Close();
        }

        private static void TestTransport()
        {
            var window = new Window();
            var transport = new FakeTransport { Sequence = 1, Data = TextData("first") };
            var captures = new List<ClipboardEntry>();
            var messages = new List<string>();
            using (var monitor = new ClipboardMonitor(window, captures.Add, messages.Add,
                delegate { return transport.Sequence; }, transport.Read, transport.Write))
            {
                monitor.RequestCapture(); Pump(35);
                Check(captures.Count == 1 && captures[0].Text == "first", "explicit capture request reads current data");
                int reads = transport.ReadCount;
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 1 && transport.ReadCount == reads, "same sequence is deduplicated without reading again");
                transport.Sequence++;
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 2 && captures[0].Id != captures[1].Id, "same external content with a new sequence remains a new capture");

                Check(monitor.Copy(captures[0]), "fake text copy succeeds");
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 2, "own marked write suppressed");
                transport.Sequence++; transport.Data = TextData("user immediately after self copy");
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 3 && captures[2].Text.StartsWith("user", StringComparison.Ordinal), "following external sequence is never suppressed");

                monitor.Copy(captures[0]);
                transport.Sequence++; transport.Data = TextData("external before own notification");
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 4 && captures[3].Text == "external before own notification", "copy-write race cannot suppress the user's next clipboard");

                transport.Sequence++; transport.Data = TextData("stale snapshot");
                transport.AfterRead = delegate { transport.Sequence++; transport.Data = TextData("new snapshot"); };
                monitor.RequestCapture(); Pump(90);
                Check(captures.Count == 5 && captures[4].Text == "new snapshot", "sequence change during read retries the newer snapshot");

                transport.Sequence++; transport.Data = TextData("unlocked"); transport.BusyReads = 2;
                int busyStart = transport.ReadCount;
                monitor.RequestCapture(); Pump(160);
                Check(captures.Count == 6 && captures[5].Text == "unlocked" && transport.ReadCount == busyStart + 3, "bounded short retries recover a busy clipboard");

                transport.Sequence++; transport.AlwaysBusy = true;
                monitor.RequestCapture(); Pump(900);
                int afterBudget = transport.ReadCount;
                Check(messages.Any(m => m.Contains("仍被占用")), "busy retry exhaustion is reported");
                Pump(100);
                Check(transport.ReadCount == afterBudget, "exhausted retry stops instead of polling");
                transport.AlwaysBusy = false; transport.Sequence++; transport.Data = TextData("next event");
                monitor.RequestCapture(); Pump(30);
                Check(captures.Count == 7 && captures[6].Text == "next event", "next event recovers after prior retry exhaustion");

                transport.FailWrite = true;
                int previousCount = captures.Count;
                Check(!monitor.Copy(captures[0]), "copy failure returns false");
                Check(captures.Count == previousCount, "copy failure does not mutate captured history");
                transport.FailWrite = false;
            }

            var pending = new ClipboardMonitor(window, captures.Add, messages.Add,
                delegate { return transport.Sequence; }, transport.Read, transport.Write);
            transport.Sequence++; transport.AlwaysBusy = true;
            pending.RequestCapture(); Pump(10); pending.Dispose();
            int beforeDisposedWait = transport.ReadCount;
            Pump(100);
            Check(transport.ReadCount == beforeDisposedWait, "dispose cancels queued retry reads");
            Check(!pending.Copy(captures[0]), "disposed monitor rejects copy");
            window.Close();
        }

        private static DataObject TextData(string text)
        {
            var data = new DataObject(); data.SetText(text, TextDataFormat.UnicodeText); return data;
        }

        private static BitmapSource Bitmap(byte[] pixels)
        {
            BitmapSource image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            image.Freeze(); return image;
        }

        private static byte[] Encode(BitmapSource bitmap, BitmapEncoder encoder)
        {
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new MemoryStream()) { encoder.Save(output); return output.ToArray(); }
        }

        private static byte[] Pixels(byte[] png)
        {
            using (var input = new MemoryStream(png, false))
            {
                BitmapSource frame = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
                source.CopyPixels(pixels, source.PixelWidth * 4, 0);
                return pixels;
            }
        }

        private static byte[] MakeDib(byte[] topDownPixels, bool topDown, bool v5)
        {
            int header = v5 ? 124 : 40;
            byte[] result = new byte[header + 16];
            using (var stream = new MemoryStream(result, true))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(header); writer.Write(2); writer.Write(topDown ? -2 : 2);
                writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(v5 ? 3 : 0); writer.Write(16);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                if (v5) { writer.Write(0x00ff0000); writer.Write(0x0000ff00); writer.Write(0x000000ff); writer.Write(unchecked((int)0xff000000)); }
            }
            Buffer.BlockCopy(topDownPixels, topDown ? 0 : 8, result, header, 8);
            Buffer.BlockCopy(topDownPixels, topDown ? 8 : 0, result, header + 8, 8);
            return result;
        }

        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }

        private static void Check(bool value, string label)
        {
            if (!value) throw new Exception(label);
            checks++;
        }

        private static void Throws<T>(Action action, string label) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new Exception(label);
        }

        private sealed class FakeTransport
        {
            internal uint Sequence;
            internal IDataObject Data;
            internal int BusyReads;
            internal bool AlwaysBusy;
            internal bool FailWrite;
            internal int ReadCount;
            internal Action AfterRead;
            internal IDataObject Read()
            {
                ReadCount++;
                if (AlwaysBusy || BusyReads-- > 0) throw new COMException("fixture clipboard busy");
                IDataObject result = Data;
                Action next = AfterRead; AfterRead = null;
                if (next != null) next();
                return result;
            }
            internal void Write(IDataObject value)
            {
                if (FailWrite) throw new COMException("fixture clipboard busy");
                Data = value; Sequence++;
            }
        }

        private sealed class FixtureData : IDataObject
        {
            private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
            public object GetData(string format, bool autoConvert) { object value; values.TryGetValue(format, out value); return value; }
            public object GetData(string format) { return GetData(format, false); }
            public object GetData(Type format) { return GetData(format.FullName, false); }
            public bool GetDataPresent(string format, bool autoConvert) { return values.ContainsKey(format); }
            public bool GetDataPresent(string format) { return GetDataPresent(format, false); }
            public bool GetDataPresent(Type format) { return GetDataPresent(format.FullName, false); }
            public string[] GetFormats(bool autoConvert) { return values.Keys.ToArray(); }
            public string[] GetFormats() { return GetFormats(false); }
            public void SetData(string format, object data, bool autoConvert) { values[format] = data; }
            public void SetData(string format, object data) { SetData(format, data, false); }
            public void SetData(Type format, object data) { SetData(format.FullName, data, false); }
            public void SetData(object data) { SetData(data.GetType(), data); }
        }
    }
}
