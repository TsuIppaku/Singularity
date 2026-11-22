using System;
using System.Collections.Generic;

namespace Orbit
{
    internal static class StartupRegistrationTests
    {
        private static int failures;

        private static int Main()
        {
            Run("current-user startup registration", RegisterAndRemove);
            Run("startup registration errors are contained", RegistryFailures);
            Console.WriteLine(failures == 0 ? "All 2 startup registration test groups passed." : failures + " startup registration test group(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        private static void RegisterAndRemove()
        {
            var registry = new MemoryRegistry();
            var startup = new StartupRegistration(registry);
            string error;
            Assert(startup.TrySet(true, @"C:\Program Files\Singularity\Orbit.exe", out error), "Registration should succeed.");
            Assert(error == null, "Successful registration should not report an error.");
            Assert(registry.Values[StartupRegistration.ValueName] == "\"C:\\Program Files\\Singularity\\Orbit.exe\"", "Executable path must be quoted for Windows Startup Apps.");
            bool enabled;
            Assert(startup.TryIsEnabled(out enabled, out error) && enabled && error == null, "Existing startup value should be reported as enabled.");
            Assert(startup.TrySet(false, @"C:\Program Files\Singularity\Orbit.exe", out error), "Removal should succeed.");
            Assert(!registry.Values.ContainsKey(StartupRegistration.ValueName), "Removal should delete only Orbit's startup value.");
            Assert(startup.TryIsEnabled(out enabled, out error) && !enabled, "Removed startup value should be reported as disabled.");
        }

        private static void RegistryFailures()
        {
            var startup = new StartupRegistration(new ThrowingRegistry());
            string error;
            Assert(!startup.TrySet(true, @"C:\Orbit.exe", out error) && !String.IsNullOrWhiteSpace(error), "Registry write failures must be returned without crashing Orbit.");
            bool enabled;
            Assert(!startup.TryIsEnabled(out enabled, out error) && !enabled && !String.IsNullOrWhiteSpace(error), "Registry read failures must be returned without guessing the startup state.");
        }

        private static void Run(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; Console.WriteLine("FAIL " + name + ": " + exception.Message); }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class MemoryRegistry : IStartupRegistry
        {
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
            public string Read(string name) { string value; return Values.TryGetValue(name, out value) ? value : null; }
            public void Write(string name, string command) { Values[name] = command; }
            public void Remove(string name) { Values.Remove(name); }
        }

        private sealed class ThrowingRegistry : IStartupRegistry
        {
            public string Read(string name) { throw new UnauthorizedAccessException("Denied for test."); }
            public void Write(string name, string command) { throw new UnauthorizedAccessException("Denied for test."); }
            public void Remove(string name) { throw new UnauthorizedAccessException("Denied for test."); }
        }
    }
}
