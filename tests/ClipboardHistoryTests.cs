using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using Orbit;

internal static class ClipboardHistoryTests
{
    private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 11, 13, 17, 0, 0, TimeSpan.Zero);
    private static int failures;
    private static string temporaryRoot;

    private static int Main()
    {
        temporaryRoot = Path.Combine(Path.GetTempPath(), "Orbit.ClipboardHistoryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            Run("capacity, order and promotion", CapacityAndOrder);
            Run("external duplicates remain separate captures", RepeatedCaptures);
            Run("images preserve bytes, dimensions and display names", ImageRoundTrip);
            Run("legacy two-slot migration is non-destructive", LegacyMigration);
            Run("failed commits preserve memory, metadata and cache", AtomicFailure);
            Run("eviction cleans only committed history images", CacheCleanup);
            Run("corrupt records are retained without crashing startup", CorruptState);
            Run("unsafe cache paths cannot escape the image directory", PathSafety);
            Run("invalid image captures fail without side effects", InvalidImages);
            Console.WriteLine(failures == 0 ? "All 9 clipboard history test groups passed." : failures + " clipboard history test group(s) failed.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            string resolved = Path.GetFullPath(temporaryRoot);
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (String.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("Orbit.ClipboardHistoryTests-", StringComparison.Ordinal))
                Directory.Delete(resolved, true);
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

    private static ClipboardEntry TextEntry(string text, int seconds)
    {
        return new ClipboardEntry { Text = text, CapturedAtUtc = Epoch.AddSeconds(seconds) };
    }

    private static void CapacityAndOrder()
    {
        string directory = DirectoryFor("capacity");
        var history = new ClipboardHistory(directory);
        int notifications = 0;
        history.Changed += delegate { notifications++; };
        foreach (string text in new[] { "one", "two", "three", "four" }) Assert(history.Add(TextEntry(text, notifications)), "Capture should save.");
        Assert(history.Entries.Select(entry => entry.Text).SequenceEqual(new[] { "four", "three", "two" }), "Only the latest three captures should remain, newest first.");
        ClipboardEntry promoted = history.Entries[2];
        DateTimeOffset captured = promoted.CapturedAtUtc;
        Assert(history.Promote(promoted.Id), "Promotion should save.");
        Assert(history.Entries[0] == promoted && promoted.CapturedAtUtc == captured && history.Entries.Count == 3, "Promotion reorders the same record without changing capture time.");
        Assert(history.Promote(promoted.Id) && notifications == 5, "Promoting the first record is a successful no-op.");
        Assert(history.Remove(history.Entries[1].Id), "Removal should save.");
        Assert(notifications == 6 && history.Entries.Count == 2, "Changed fires only after committed changes.");
        Assert(!history.Remove("missing") && !history.Promote("missing"), "Unknown IDs should fail clearly.");
        var restored = new ClipboardHistory(directory);
        Assert(restored.Entries.Select(entry => entry.Id).SequenceEqual(history.Entries.Select(entry => entry.Id)), "Order and identities survive relaunch.");
    }

    private static void RepeatedCaptures()
    {
        var history = new ClipboardHistory(DirectoryFor("duplicates"));
        var source = new ClipboardEntry { Id = "same-monitor-id", Text = "same text", CapturedAtUtc = Epoch };
        for (int index = 0; index < 3; index++)
        {
            source.CapturedAtUtc = Epoch.AddSeconds(index);
            Assert(history.Add(source), "Repeated external captures must save.");
        }
        Assert(history.Entries.Count == 3 && history.Entries.Select(entry => entry.Id).Distinct().Count() == 3, "Repeated copies are separate entries with distinct IDs.");
        Assert(history.Entries.Select(entry => entry.CapturedAtUtc).SequenceEqual(new[] { Epoch.AddSeconds(2), Epoch.AddSeconds(1), Epoch }), "Repeated copies retain their individual timestamps.");
        source.Text = "caller changed its object";
        Assert(history.Entries.All(entry => entry.Text == "same text"), "Captured text must not alias the incoming mutable object.");
        Assert(history.Promote(history.Entries[2].Id) && history.Entries.Count == 3, "Application history reuse must not add a fourth copy.");

        string imageDirectory = DirectoryFor("duplicate-images");
        var images = new ClipboardHistory(imageDirectory);
        var image = new ClipboardEntry { ImagePng = MakePng(2, 3), FileName = "same.png", CapturedAtUtc = Epoch };
        Assert(images.Add(image) && images.Add(image), "Identical externally copied PNGs should each save.");
        Assert(images.Entries.Count == 2 && images.Entries[0].Id != images.Entries[1].Id &&
            Directory.GetFiles(Path.Combine(imageDirectory, "clipboard-history-images"), "*.png").Length == 2,
            "Repeated PNG captures keep separate records and independently owned cache files.");
    }

    private static void ImageRoundTrip()
    {
        string directory = DirectoryFor("images");
        byte[] png = MakePng(17, 11);
        byte[] original = (byte[])png.Clone();
        string displayName = "..\\用户原始图片.png";
        var history = new ClipboardHistory(directory);
        Assert(history.Add(new ClipboardEntry { ImagePng = png, PixelWidth = 1, PixelHeight = 2, FileName = displayName, CapturedAtUtc = Epoch }), "PNG should save.");
        ClipboardEntry entry = history.Entries[0];
        Assert(entry.IsImage && entry.PixelWidth == 17 && entry.PixelHeight == 11, "Dimensions must come from the original PNG, not an inaccurate hint.");
        Assert(entry.FileName == displayName && entry.ImagePng.SequenceEqual(original), "Original display name and exact PNG bytes must be retained.");
        png[0] = 0;
        Assert(entry.ImagePng.SequenceEqual(original), "The caller's byte array must not mutate the captured image.");
        string[] images = Directory.GetFiles(Path.Combine(directory, "clipboard-history-images"));
        Assert(images.Length == 1 && File.ReadAllBytes(images[0]).SequenceEqual(original), "PNG is cached independently without recompression or resizing.");
        Assert(Path.GetFileName(images[0]).Length == 36 && Path.GetFileName(images[0]) != displayName, "The display name must never become the cache path.");
        var restored = new ClipboardHistory(directory);
        ClipboardEntry loaded = restored.Entries.Single();
        Assert(loaded.Id == entry.Id && loaded.FileName == displayName && loaded.PixelWidth == 17 && loaded.PixelHeight == 11 && loaded.ImagePng.SequenceEqual(original), "Image metadata and bytes survive relaunch.");
    }

    private static void LegacyMigration()
    {
        string directory = DirectoryFor("legacy");
        string oldPath = Path.Combine(directory, "clipboard-slots.json");
        string oldJson = "{\"Items\":[\"旧槽位一\",\"旧槽位二\"]}";
        File.WriteAllText(oldPath, oldJson, new UTF8Encoding(false));
        var history = new ClipboardHistory(directory);
        Assert(history.Entries.Select(entry => entry.Text).SequenceEqual(new[] { "旧槽位一", "旧槽位二" }), "Both legacy text slots should migrate in their stored order.");
        Assert(File.ReadAllText(oldPath, Encoding.UTF8) == oldJson && File.Exists(Path.Combine(directory, "clipboard-history.json")), "Migration preserves the old file and creates the new metadata.");
        foreach (ClipboardEntry entry in history.Entries.ToList()) Assert(history.Remove(entry.Id), "Migrated item should be removable.");
        Assert(new ClipboardHistory(directory).Entries.Count == 0, "An empty new history must not re-import deleted legacy slots.");
        string brokenDirectory = DirectoryFor("broken-legacy");
        string brokenPath = Path.Combine(brokenDirectory, "clipboard-slots.json");
        File.WriteAllText(brokenPath, "not JSON");
        var broken = new ClipboardHistory(brokenDirectory);
        Assert(!String.IsNullOrEmpty(broken.LastError) && File.ReadAllText(brokenPath) == "not JSON", "Unreadable legacy data must be retained and reported.");
        Assert(broken.Add(TextEntry("new copy", 1)), "A separate damaged legacy file should not prevent creating new-format history.");
    }

    private static void AtomicFailure()
    {
        string directory = DirectoryFor("atomic");
        var history = new ClipboardHistory(directory);
        Assert(history.Add(TextEntry("keep", 0)), "Initial state should save.");
        string metadata = Path.Combine(directory, "clipboard-history.json");
        byte[] before = File.ReadAllBytes(metadata);
        string id = history.Entries[0].Id;
        int notifications = 0;
        history.Changed += delegate { notifications++; };
        using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(!history.Add(new ClipboardEntry { ImagePng = MakePng(5, 3), FileName = "blocked.png", CapturedAtUtc = Epoch }), "A locked metadata file must make commit fail.");
        Assert(history.Entries.Count == 1 && history.Entries[0].Id == id && notifications == 0, "Failed commit must not change memory or publish a change event.");
        Assert(File.ReadAllBytes(metadata).SequenceEqual(before), "Failed commit must preserve exact prior metadata bytes.");
        Assert(!String.IsNullOrEmpty(history.LastError), "Failure must be visible to the UI.");
        string cache = Path.Combine(directory, "clipboard-history-images");
        Assert(!Directory.Exists(cache) || Directory.GetFiles(cache).Length == 0, "A failed capture must not leave an unreferenced new PNG behind.");
        Assert(history.Add(TextEntry("retry", 1)) && history.LastError == null, "A transient write failure should allow retry.");

        string occupied = DirectoryFor("occupied-cache");
        var blocked = new ClipboardHistory(occupied);
        Assert(blocked.Add(TextEntry("existing", 0)), "Initial text should save without an image cache.");
        File.WriteAllText(Path.Combine(occupied, "clipboard-history-images"), "user file");
        Assert(!blocked.Add(new ClipboardEntry { ImagePng = MakePng(2, 2) }) && blocked.Entries[0].Text == "existing", "An occupied cache path must fail without changing history.");
    }

    private static void CacheCleanup()
    {
        string directory = DirectoryFor("cleanup");
        var history = new ClipboardHistory(directory);
        Assert(history.Add(new ClipboardEntry { ImagePng = MakePng(3, 2), FileName = "first.png", CapturedAtUtc = Epoch }), "First image should save.");
        string cache = Path.Combine(directory, "clipboard-history-images");
        string firstImage = Directory.GetFiles(cache, "*.png").Single();
        string unrelated = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllText(unrelated, "not referenced by this history");
        Assert(history.Add(TextEntry("second", 1)) && history.Add(TextEntry("third", 2)), "Fill the history.");
        string metadata = Path.Combine(directory, "clipboard-history.json");
        using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(!history.Add(TextEntry("fourth", 3)), "Eviction must not happen before metadata can commit.");
        Assert(File.Exists(firstImage), "A failed eviction must keep the old PNG.");
        Assert(history.Add(TextEntry("fourth", 3)), "Retry eviction should succeed.");
        Assert(!File.Exists(firstImage) && File.Exists(unrelated), "Only the successfully evicted record's own cache file is removed.");
        Assert(history.Add(new ClipboardEntry { ImagePng = MakePng(4, 4), FileName = "remove.png" }), "New image should save.");
        string removable = Directory.GetFiles(cache, "*.png").Single(path => path != unrelated);
        Assert(history.Remove(history.Entries[0].Id) && !File.Exists(removable) && File.Exists(unrelated), "Explicit removal clears only its own cache after commit.");
    }

    private static void CorruptState()
    {
        string directory = DirectoryFor("corrupt");
        string path = Path.Combine(directory, "clipboard-history.json");
        File.WriteAllText(path, "{broken JSON");
        var history = new ClipboardHistory(directory);
        Assert(history.Entries.Count == 0 && !String.IsNullOrEmpty(history.LastError), "Startup should report malformed metadata without throwing.");
        Assert(!history.Add(TextEntry("do not overwrite", 0)) && File.ReadAllText(path) == "{broken JSON", "Unreadable new history must not be silently overwritten.");

        string imageDirectory = DirectoryFor("corrupt-image");
        var images = new ClipboardHistory(imageDirectory);
        Assert(images.Add(new ClipboardEntry { ImagePng = MakePng(2, 3), FileName = "original.png" }), "Fixture image should save.");
        string cached = Directory.GetFiles(Path.Combine(imageDirectory, "clipboard-history-images")).Single();
        File.WriteAllText(cached, "damaged PNG");
        var restored = new ClipboardHistory(imageDirectory);
        Assert(!String.IsNullOrEmpty(restored.LastError) && restored.Entries.Count == 0 && !restored.Add(TextEntry("blocked", 0)), "Unreadable cached images must protect their metadata from replacement.");
        Assert(File.ReadAllText(cached) == "damaged PNG", "Invalid cached files must be preserved for recovery.");

        using (var oversized = new FileStream(cached, FileMode.Create, FileAccess.Write, FileShare.None))
            oversized.SetLength(32L * 1024 * 1024 + 1);
        var excessive = new ClipboardHistory(imageDirectory);
        Assert(!String.IsNullOrEmpty(excessive.LastError) && excessive.Entries.Count == 0 && new FileInfo(cached).Length == 32L * 1024 * 1024 + 1,
            "Oversized cached images must be rejected before loading their bytes, with the original retained.");
    }

    private static void PathSafety()
    {
        string directory = DirectoryFor("path-safety");
        string outside = Path.Combine(directory, "important.png");
        byte[] original = MakePng(2, 2);
        File.WriteAllBytes(outside, original);
        string malicious = "{\"Version\":1,\"Entries\":[{\"Id\":\"unsafe\",\"ImageFile\":\"../important.png\"}]}";
        string metadata = Path.Combine(directory, "clipboard-history.json");
        File.WriteAllText(metadata, malicious);
        var history = new ClipboardHistory(directory);
        Assert(history.Entries.Count == 0 && !String.IsNullOrEmpty(history.LastError), "Metadata paths outside the private cache must be rejected.");
        Assert(!history.Remove("unsafe") && File.ReadAllBytes(outside).SequenceEqual(original) && File.ReadAllText(metadata) == malicious, "Unsafe metadata must not lead to deletion or overwriting.");
    }

    private static void InvalidImages()
    {
        var history = new ClipboardHistory(DirectoryFor("invalid-images"));
        Assert(!history.Add(new ClipboardEntry { ImagePng = new byte[] { 1, 2, 3 }, FileName = "fake.png" }), "Non-PNG bytes should fail without throwing.");
        Assert(!history.Add(new ClipboardEntry { ImagePng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 } }), "A PNG signature without image data must also fail safely.");
        byte[] badDimensions = MakePng(2, 2);
        badDimensions[16] = 127;
        badDimensions[17] = badDimensions[18] = badDimensions[19] = 255;
        Assert(!history.Add(new ClipboardEntry { ImagePng = badDimensions }), "Huge forged PNG dimensions must fail before decoder allocation.");
        byte[] truncated = MakePng(2, 2).Take(33).ToArray();
        Assert(!history.Add(new ClipboardEntry { ImagePng = truncated }), "Valid image header with no pixel data must fail safely in the decoder.");
        Assert(history.Entries.Count == 0 && !String.IsNullOrEmpty(history.LastError), "Invalid images must not create entries.");
        Assert(!history.Add(null) && !history.Add(new ClipboardEntry()), "Empty captures must fail safely.");
        Assert(history.Add(TextEntry("   ", 0)), "Whitespace-only clipboard text is still a real capture.");
    }

    private static byte[] MakePng(int width, int height)
    {
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var output = new MemoryStream())
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    bitmap.SetPixel(x, y, Color.FromArgb(128 + (x + y) % 128, x * 19 % 256, y * 31 % 256, (x + y) * 7 % 256));
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
