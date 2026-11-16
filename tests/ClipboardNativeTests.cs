using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Orbit
{
    // Run as a separate process. The private window station owns a separate
    // clipboard; never fall back to the user's interactive station.
    internal static class ClipboardNativeTests
    {
        private static string stationName;
        private static int checks;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length != 1) { Console.WriteLine("An isolated output directory is required."); return 2; }
            try
            {
                stationName = "OrbitClipboardTests_" + Guid.NewGuid().ToString("N");
                IntPtr station = CreateWindowStation(stationName, 0, 0x10000000, IntPtr.Zero);
                if (station == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Console.WriteLine("SKIP: Private station unavailable (Win32 " + error + ": " + new Win32Exception(error).Message + "). No clipboard access attempted.");
                    return 2;
                }
                if (!SetProcessWindowStation(station)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enter private station");
                IntPtr desktop = CreateDesktop("OrbitClipboardDesktop", null, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
                if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enter private desktop");
                AssertPrivateStation();
                Run(Path.GetFullPath(args[0]));
                Console.WriteLine("PASS: " + checks + " isolated native clipboard checks. Interactive clipboard untouched.");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL: " + error); return 1; }
            // Process exit releases this station and desktop. Do not reconnect
            // WPF/OLE to WinSta0 during teardown.
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(string output)
        {
            AssertPrivateStation();
            Directory.CreateDirectory(output);
            var history = new ClipboardHistory(Path.Combine(output, "state"));
            var window = new Window { Width = 40, Height = 40, ShowInTaskbar = false, ShowActivated = false, Title = "Orbit isolated clipboard test" };
            window.Show();
            var monitor = new ClipboardMonitor(window, delegate(ClipboardEntry entry)
            {
                if (!history.Add(entry)) throw new InvalidOperationException(history.LastError);
            }, delegate(string message) { Console.WriteLine("NOTICE " + message); });
            try
            {
                monitor.Start();
                for (int i = 1; i <= 4; i++)
                {
                    string value = "原生剪贴板 · " + i;
                    AssertPrivateStation();
                    Clipboard.SetText(value, TextDataFormat.UnicodeText);
                    PumpUntil(delegate { return history.Entries.Count > 0 && history.Entries[0].Text == value; });
                }
                Check(history.Entries.Count == 3 && history.Entries[2].Text == "原生剪贴板 · 2", "Windows clipboard update messages capture text and evict the fourth record");
                AssertPrivateStation();
                Clipboard.SetText("原生剪贴板 · 4", TextDataFormat.UnicodeText);
                string oldFirst = history.Entries[0].Id;
                PumpUntil(delegate { return history.Entries[0].Id != oldFirst; });
                Check(history.Entries[0].Text == history.Entries[1].Text, "A new native copy of identical text is still a new history event");

                byte[] pixels = { 25, 50, 100, 0, 80, 100, 120, 64, 150, 170, 190, 128, 200, 220, 240, 255 };
                BitmapSource image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
                image.Freeze();
                byte[] png;
                using (var buffer = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(buffer); png = buffer.ToArray();
                }
                var source = new ClipboardEntry { ImagePng = png, FileName = "透明测试.png", PixelWidth = 2, PixelHeight = 2, CapturedAtUtc = DateTimeOffset.UtcNow };
                AssertPrivateStation();
                Clipboard.SetDataObject(ClipboardMonitor.CreateDataObject(source, null), true);
                PumpUntil(delegate { return history.Entries[0].IsImage; });
                Check(history.Entries[0].PixelWidth == 2 && history.Entries[0].PixelHeight == 2, "Native PNG and bitmap formats are captured as a real image");
                Check(Alpha(history.Entries[0].ImagePng).SequenceEqual(new byte[] { 0, 64, 128, 255 }), "Native PNG capture preserves transparent and translucent pixels");
                string[] ids = history.Entries.Select(entry => entry.Id).ToArray();
                AssertPrivateStation();
                Check(monitor.Copy(history.Entries[0]), "Saved image writes back to the Windows clipboard");
                PumpFor(150);
                Check(ids.SequenceEqual(history.Entries.Select(entry => entry.Id)), "Native self-copy notification does not duplicate history");
                AssertPrivateStation();
                Check(Clipboard.ContainsImage() && Clipboard.GetImage() != null, "Native image consumers can read the bitmap representation");
                var returned = ClipboardMonitor.ReadData(Clipboard.GetDataObject(), DateTimeOffset.UtcNow);
                Check(returned.IsImage && Alpha(returned.ImagePng).SequenceEqual(new byte[] { 0, 64, 128, 255 }), "PNG can be pasted back with alpha intact");

                string originalFile = Path.Combine(output, "原始图片.png");
                File.WriteAllBytes(originalFile, png);
                var paths = new StringCollection { originalFile };
                AssertPrivateStation();
                Clipboard.SetFileDropList(paths);
                PumpUntil(delegate { return history.Entries[0].FileName == "原始图片.png"; });
                Check(history.Entries[0].IsImage, "Explorer-style file copies retain the original image filename");
                File.Delete(originalFile);
                AssertPrivateStation();
                Check(monitor.Copy(history.Entries[0]), "Saved image can be copied after the original file is removed");
                PumpFor(100);
                var restored = new ClipboardHistory(Path.Combine(output, "state"));
                Check(restored.Entries[0].FileName == "原始图片.png" && restored.Entries[0].IsImage, "Native image capture survives a storage reload without its source file");

                monitor.Dispose();
                string top = history.Entries[0].Id;
                AssertPrivateStation();
                Clipboard.SetText("监听关闭后不再收集", TextDataFormat.UnicodeText);
                PumpFor(100);
                Check(history.Entries[0].Id == top, "Disposing the monitor unregisters the native listener");
            }
            finally { monitor.Dispose(); window.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }

        private static byte[] Alpha(byte[] png)
        {
            using (var input = new MemoryStream(png, false))
            {
                var frame = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                byte[] pixels = new byte[16]; converted.CopyPixels(pixels, 8, 0);
                return new[] { pixels[3], pixels[7], pixels[11], pixels[15] };
            }
        }

        private static void AssertPrivateStation()
        {
            var name = new StringBuilder(256); int needed;
            if (!GetUserObjectInformation(GetProcessWindowStation(), 2, name, name.Capacity * 2, out needed) || name.ToString() != stationName || name.ToString() == "WinSta0")
                throw new InvalidOperationException("Refusing clipboard access outside the private station.");
        }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; Console.WriteLine("PASS " + message); }
        private static void PumpUntil(Func<bool> predicate)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.ElapsedMilliseconds < 4000) PumpFor(15);
            if (!predicate()) throw new TimeoutException("Native clipboard notification was not observed.");
        }
        private static void PumpFor(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowStation(string name, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(IntPtr station);
        [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string name, string device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr value, int index, StringBuilder text, int length, out int needed);
    }
}
