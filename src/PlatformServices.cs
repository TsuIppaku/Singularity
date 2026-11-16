using System;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Orbit
{
    /// <summary>Local, atomic persistence. Failed reads never overwrite the original file.</summary>
    public sealed class StateStore
    {
        private readonly string directory;
        private readonly string statePath;
        private readonly object sync = new object();
        private bool savingBlocked;

        public string LastError { get; private set; }

        public StateStore(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("A storage directory is required.", "directory");
            this.directory = Path.GetFullPath(directory);
            statePath = Path.Combine(this.directory, "state.json");
        }

        public OrbitState Load()
        {
            lock (sync)
            {
                LastError = null;
                savingBlocked = false;
                if (!File.Exists(statePath))
                    return new OrbitState();

                try
                {
                    using (FileStream input = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (input.Length > 16 * 1024 * 1024)
                            throw new SerializationException("The saved state is unexpectedly large.");
                        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(OrbitState));
                        OrbitState state = serializer.ReadObject(input) as OrbitState;
                        if (state == null)
                            throw new SerializationException("The saved state is empty.");
                        return state;
                    }
                }
                catch (Exception error)
                {
                    if (IsFatal(error)) throw;
                    if (error is SerializationException || error is System.Xml.XmlException ||
                        error is InvalidDataException || error is ArgumentException)
                    {
                        string backup = Path.Combine(directory, "state-" +
                            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                            Guid.NewGuid().ToString("N").Substring(0, 8) + ".corrupt.json");
                        try
                        {
                            File.Move(statePath, backup);
                            LastError = "保存的数据无法读取，已备份原文件：" + Path.GetFileName(backup);
                        }
                        catch (Exception backupError)
                        {
                            if (IsFatal(backupError)) throw;
                            savingBlocked = true;
                            LastError = "保存的数据无法读取，原文件仍保留。" + backupError.Message;
                        }
                    }
                    else
                    {
                        savingBlocked = true;
                        LastError = "无法读取本地数据：" + error.Message;
                    }
                    return new OrbitState();
                }
            }
        }

        public bool Save(OrbitState state)
        {
            lock (sync)
            {
                if (savingBlocked)
                {
                    LastError = "现有数据未能读取或备份，已暂停保存以保护原文件。";
                    return false;
                }
                LastError = null;
                if (state == null)
                {
                    LastError = "无法保存空数据。";
                    return false;
                }

                string temporaryPath = Path.Combine(directory, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    Directory.CreateDirectory(directory);
                    using (FileStream output = new FileStream(temporaryPath, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(OrbitState));
                        serializer.WriteObject(output, state);
                        output.Flush(true);
                    }

                    if (File.Exists(statePath))
                        File.Replace(temporaryPath, statePath, null);
                    else
                        File.Move(temporaryPath, statePath);
                    return true;
                }
                catch (Exception error)
                {
                    if (IsFatal(error)) throw;
                    LastError = "无法保存本地数据：" + error.Message;
                    return false;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static bool IsFatal(Exception error)
        {
            return error is OutOfMemoryException || error is StackOverflowException ||
                error is AccessViolationException;
        }
    }

    /// <summary>A short generated chime. Repeat timing belongs to the application's UI timer.</summary>
    public sealed class AlertSound : IDisposable
    {
        private MemoryStream wave;
        private SoundPlayer player;
        private bool disposed;

        public AlertSound()
        {
            try
            {
                wave = new MemoryStream(CreateChime(), false);
                player = new SoundPlayer(wave);
                player.Load();
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException) throw;
                if (player != null) player.Dispose();
                if (wave != null) wave.Dispose();
                player = null;
                wave = null;
            }
        }

        public void Play()
        {
            if (disposed) return;
            try
            {
                if (player != null)
                {
                    player.Play();
                    return;
                }
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException) throw;
            }
            try { SystemSounds.Exclamation.Play(); }
            catch (InvalidOperationException) { }
            catch (ExternalException) { }
        }

        public void Stop()
        {
            if (disposed || player == null) return;
            try { player.Stop(); }
            catch (InvalidOperationException) { }
            catch (ExternalException) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
            if (player != null) player.Dispose();
            if (wave != null) wave.Dispose();
        }

        private static byte[] CreateChime()
        {
            const int sampleRate = 44100;
            int count = (int)(sampleRate * 0.72);
            using (MemoryStream output = new MemoryStream(44 + count * 2))
            using (BinaryWriter writer = new BinaryWriter(output, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + count * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(count * 2);
                for (int sample = 0; sample < count; sample++)
                {
                    double time = (double)sample / sampleRate;
                    double value = Tone(time, 660.0, 0.36) + Tone(time - 0.25, 880.0, 0.43);
                    writer.Write((short)(Math.Max(-1, Math.Min(1, value * 0.23)) * Int16.MaxValue));
                }
                writer.Flush();
                return output.ToArray();
            }
        }

        private static double Tone(double time, double frequency, double duration)
        {
            if (time < 0 || time >= duration) return 0;
            double attack = Math.Min(1, time / 0.012);
            double release = Math.Min(1, (duration - time) / 0.035);
            double envelope = attack * release * Math.Exp(-5.5 * time);
            double phase = 2 * Math.PI * frequency * time;
            return envelope * (Math.Sin(phase) + 0.12 * Math.Sin(phase * 2));
        }
    }

    public static class NativeDesktop
    {
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoActivate = 0x08000000L;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr handle, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr handle, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);

        /// <summary>Cursor position in physical screen pixels, including negative monitor coordinates.</summary>
        public static Point CursorPosition
        {
            get
            {
                NativePoint point;
                return GetCursorPos(out point) ? new Point(point.X, point.Y) : new Point(0, 0);
            }
        }

        /// <summary>Call after SourceInitialized; the task panel should remain normally activatable.</summary>
        public static void PreventActivation(Window window)
        {
            if (window == null) throw new ArgumentNullException("window");
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Call PreventActivation after SourceInitialized.");
            long style = IntPtr.Size == 8
                ? GetWindowLongPtr64(handle, GwlExStyle).ToInt64()
                : (uint)GetWindowLong32(handle, GwlExStyle);
            style |= WsExToolWindow | WsExNoActivate;
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(style));
            else
                SetWindowLong32(handle, GwlExStyle, unchecked((int)style));
        }
    }
}
