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
            Run("pinned item is separate from normal rotation and replacement", PinnedRotation);
            Run("unlock appends after recent records and persists that order", UnlockAppendOrder);
            Run("pinned state, preference and image ownership survive relaunch", PinnedPersistence);
            Run("failed pin changes preserve the old pin and setting", PinnedAtomicFailure);
            Run("ordinary files are independently snapshotted only in the pin", PinnedFileSnapshot);
            Run("file snapshot bounds and metadata paths protect existing data", PinnedFileBoundaries);
            Console.WriteLine(failures == 0 ? "All 15 clipboard history test groups passed." : failures + " clipboard history test group(s) failed.");
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

    private static void PinnedRotation()
    {
        var history = new ClipboardHistory(DirectoryFor("pin-rotation"));
        foreach (string text in new[] { "one", "two", "three" }) Assert(history.Add(TextEntry(text, 0)), "Initial captures should save.");
        ClipboardEntry pinned = history.Entries[1];
        Assert(history.Lock(pinned.Id) && history.LockedEntry == pinned && history.Entries.Select(entry => entry.Text).SequenceEqual(new[] { "three", "one" }),
            "Lock moves the selected object out of normal history without duplicating it.");
        Assert(history.Add(TextEntry("four", 1)) && history.Add(TextEntry("five", 2)), "Normal capture should continue while pinned.");
        Assert(history.Entries.Select(entry => entry.Text).SequenceEqual(new[] { "five", "four", "three" }) && history.LockedEntry == pinned,
            "Three normal slots rotate without evicting the independent pin.");
        int events = 0;
        history.Changed += delegate { events++; };
        Assert(history.Promote(pinned.Id) && history.Lock(pinned.Id) && events == 0, "Taking or relocking the pin is a successful no-op.");
        Assert(!history.Remove(pinned.Id) && history.LockedEntry == pinned, "Normal deletion cannot remove a locked item.");
        ClipboardEntry replacement = history.Entries[1];
        Assert(history.Lock(replacement.Id) && history.LockedEntry == replacement && history.Entries.Count == 2 &&
            history.Entries.All(entry => entry.Id != pinned.Id), "Replacing a pin discards the old pin without returning it to history.");
        Assert(!history.Lock("missing") && history.LockedEntry == replacement, "Invalid replacement targets preserve the pin.");
        Assert(history.Add(TextEntry("six", 3)), "Fill normal history before unlocking.");
        Assert(history.Unlock() && history.LockedEntry == null && history.Entries[2] == replacement &&
            history.Entries.Select(entry => entry.Text).SequenceEqual(new[] { "six", "five", "four" }),
            "Unlock appends text after the retained recent records and still evicts the previous oldest normal slot.");
        int previousEvents = events;
        Assert(history.Unlock() && events == previousEvents, "Unlocking an empty pin is a no-op.");
        Assert(history.Lock(history.Entries[0].Id), "Relock a normal entry.");
        Assert(history.Add(new ClipboardEntry { Id = history.LockedEntry.Id, Text = "new external copy" }) && history.Entries[0].Id != history.LockedEntry.Id,
            "A new capture cannot reuse the pinned entry's identity.");
    }

    private static void UnlockAppendOrder()
    {
        for (int count = 0; count <= 3; count++)
        {
            string directory = DirectoryFor("unlock-append-" + count);
            var history = new ClipboardHistory(directory);
            Assert(history.LockDropped(TextEntry("returning pin", 100)), "Pin fixture should save.");
            ClipboardEntry pinned = history.LockedEntry;
            for (int index = 0; index < count; index++)
                Assert(history.Add(TextEntry("recent-" + index, index)), "Existing recent records should save.");
            ClipboardEntry[] before = history.Entries.ToArray();
            string[] expected = before.Take(2).Select(entry => entry.Id).Concat(new[] { pinned.Id }).ToArray();
            Assert(history.Unlock() && history.LockedEntry == null && history.Entries.Last() == pinned &&
                history.Entries.Select(entry => entry.Id).SequenceEqual(expected),
                "Unlock must append to an empty/partial/full list while retaining existing recent order: " + count);
            if (count == 3)
                Assert(history.Entries.All(entry => entry.Id != before[2].Id), "A full list still evicts its previous oldest record, not the unlocked item.");

            history = new ClipboardHistory(directory);
            Assert(history.LockedEntry == null && history.Entries.Select(entry => entry.Id).SequenceEqual(expected) &&
                history.Entries.Last().CapturedAtUtc == pinned.CapturedAtUtc,
                "Reopen preserves append order and the original capture timestamp without sorting the pin to the front: " + count);
            Assert(history.Add(TextEntry("next capture", 101)), "New capture after unlock should save.");
            string[] afterAdd = new[] { history.Entries[0].Id }.Concat(expected.Take(2)).ToArray();
            Assert(history.Entries[0].Text == "next capture" && history.Entries.Select(entry => entry.Id).SequenceEqual(afterAdd),
                "The next Add rotates the appended list using the normal three-record capacity: " + count);
            Assert(new ClipboardHistory(directory).Entries.Select(entry => entry.Id).SequenceEqual(afterAdd),
                "The next Add order survives a second reopen: " + count);
        }
    }

    private static void PinnedPersistence()
    {
        string directory = DirectoryFor("pin-persistence");
        File.WriteAllText(Path.Combine(directory, "clipboard-history.json"), "{\"Version\":1,\"Entries\":[{\"Id\":\"legacy\",\"Text\":\"legacy text\"}]}");
        var history = new ClipboardHistory(directory);
        Assert(history.ConfirmLockedReplacement && history.LockedEntry == null && history.Entries.Count == 1, "Old history defaults to confirmation enabled and an empty pin.");
        Assert(history.Lock("legacy") && history.SetConfirmLockedReplacement(false), "Pin and preference should save.");
        history = new ClipboardHistory(directory);
        Assert(!history.ConfirmLockedReplacement && history.LockedEntry.Id == "legacy" && history.Entries.Count == 0, "Both pin and disabled confirmation survive restart.");
        byte[] png = MakePng(5, 4);
        Assert(history.LockDropped(new ClipboardEntry { ImagePng = png, FileName = "固定图片.png", CapturedAtUtc = Epoch }), "Dropping an image should replace the pin.");
        string cache = Path.Combine(directory, "clipboard-history-images");
        string firstImage = Directory.GetFiles(cache, "*.png").Single();
        Assert(history.Add(TextEntry("normal", 0)), "Normal text can coexist with an image pin.");
        history = new ClipboardHistory(directory);
        Assert(history.LockedEntry.IsImage && !history.LockedEntry.IsFile && history.LockedEntry.ImagePng.SequenceEqual(png) &&
            history.LockedEntry.FileName == "固定图片.png" && !history.ConfirmLockedReplacement, "Image bytes, display name and preference survive relaunch.");
        Assert(history.Unlock() && history.Entries[0].Text == "normal" && history.Entries[1].IsImage && File.Exists(firstImage), "Unlocking an image appends its owned cache after recent history without deleting it.");
        Assert(history.Lock(history.Entries[1].Id) && history.LockDropped(TextEntry("replacement", 1)), "Replacing an image pin with text should save.");
        Assert(!File.Exists(firstImage) && history.Entries.All(entry => entry.FileName != "固定图片.png"), "Replaced pinned image cache is cleaned only after commit, without restoring history.");
        Assert(history.SetConfirmLockedReplacement(true) && new ClipboardHistory(directory).ConfirmLockedReplacement, "Re-enabling confirmation should persist.");
    }

    private static void PinnedAtomicFailure()
    {
        string directory = DirectoryFor("pin-atomic");
        var history = new ClipboardHistory(directory);
        Assert(history.LockDropped(new ClipboardEntry { ImagePng = MakePng(3, 3), FileName = "keep.png" }), "Initial pin saves.");
        Assert(history.Add(TextEntry("normal", 0)), "Initial normal history saves.");
        ClipboardEntry oldPin = history.LockedEntry, normal = history.Entries[0];
        string source = Path.Combine(directory, "source.bin");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });
        string metadata = Path.Combine(directory, "clipboard-history.json");
        byte[] before = File.ReadAllBytes(metadata);
        int events = 0;
        history.Changed += delegate { events++; };
        using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert(!history.Lock(normal.Id), "A failed move-to-pin must not remove normal history.");
            Assert(!history.LockDropped(new ClipboardEntry { ImagePng = MakePng(4, 4) }), "A failed image replacement must preserve the old pin.");
            Assert(!history.LockDropped(new ClipboardEntry { FilePath = source }), "A failed file snapshot commit must preserve the old pin.");
            Assert(!history.Unlock() && !history.SetConfirmLockedReplacement(false), "Unlock and preference changes are transactional too.");
        }
        string cache = Path.Combine(directory, "clipboard-history-images");
        Assert(history.LockedEntry == oldPin && history.Entries.Single() == normal && history.ConfirmLockedReplacement && events == 0,
            "Every failed pin operation leaves the committed objects, setting and notifications unchanged.");
        Assert(File.ReadAllBytes(metadata).SequenceEqual(before) && Directory.GetFiles(cache, "*.png").Length == 1 && Directory.GetDirectories(cache).Length == 0,
            "Failed pin changes preserve exact metadata and old cache while cleaning only their newly created snapshots.");
        Assert(File.ReadAllBytes(source).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Snapshot failure must never touch the source file.");
        Assert(history.Unlock() && history.Entries.Count == 2 && history.Entries[0] == normal && history.Entries[1] == oldPin && history.LastError == null,
            "A failed change can be retried after the lock is released, appending the preserved pin after existing recent history.");
    }

    private static void PinnedFileSnapshot()
    {
        string directory = DirectoryFor("pin-file");
        string source = Path.Combine(directory, "原始 报告.xlsx");
        byte[] bytes = Enumerable.Range(0, 4097).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(source, bytes);
        var history = new ClipboardHistory(Path.Combine(directory, "data"));
        foreach (string text in new[] { "one", "two", "three" }) Assert(history.Add(TextEntry(text, 0)), "Normal history fixture saves.");
        var drop = new ClipboardEntry { Id = "file", FilePath = source, FileName = "../do-not-use.exe", CapturedAtUtc = Epoch };
        Assert(!history.Add(drop) && history.LockDropped(drop), "Ordinary files are accepted only by the locked slot.");
        ClipboardEntry pinned = history.LockedEntry;
        string snapshot = pinned.FilePath;
        Assert(pinned.IsFile && !pinned.IsImage && pinned.Text == null && pinned.FileName == "原始 报告.xlsx" &&
            Path.GetFileName(snapshot) == pinned.FileName && snapshot != source && File.ReadAllBytes(snapshot).SequenceEqual(bytes),
            "File pins expose an independent exact snapshot with the source basename and original extension.");
        Assert(Path.GetDirectoryName(Path.GetDirectoryName(snapshot)) == Path.Combine(directory, "data", "clipboard-history-images"),
            "Ordinary-file snapshots remain inside the existing private cache root.");
        Assert(history.Entries.Count == 3 && history.Add(TextEntry("four", 4)) && history.LockedEntry == pinned && pinned.FilePath == snapshot,
            "Normal rotation does not move or replace the pinned file's stable path.");
        File.WriteAllText(source, "source changed");
        Assert(File.ReadAllBytes(snapshot).SequenceEqual(bytes), "Editing the source after pinning cannot change the snapshot.");
        File.Delete(source);
        history = new ClipboardHistory(Path.Combine(directory, "data"));
        Assert(history.LastError == null && history.LockedEntry.FilePath == snapshot && File.ReadAllBytes(snapshot).SequenceEqual(bytes),
            "Pin persistence must not depend on the original file continuing to exist.");
        string[] before = history.Entries.Select(entry => entry.Id).ToArray();
        Assert(history.Unlock() && history.LockedEntry == null && history.Entries.Select(entry => entry.Id).SequenceEqual(before) && !File.Exists(snapshot),
            "Unlocking an ordinary file clears the pin and its owned snapshot without adding it to normal history.");
    }

    private static void PinnedFileBoundaries()
    {
        string directory = DirectoryFor("pin-file-bounds");
        var history = new ClipboardHistory(Path.Combine(directory, "data"));
        Assert(history.LockDropped(TextEntry("keep", 0)), "Initial pin saves.");
        ClipboardEntry old = history.LockedEntry;
        string oversized = Path.Combine(directory, "oversized.bin");
        using (var output = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write)) output.SetLength(64L * 1024 * 1024 + 1);
        Assert(!history.LockDropped(new ClipboardEntry { FilePath = oversized }) && history.LockedEntry == old, "Oversized files fail before snapshotting and preserve the pin.");
        Assert(!history.LockDropped(new ClipboardEntry { FilePath = directory }) && !history.LockDropped(new ClipboardEntry { FilePath = "relative.bin" }) &&
            !history.LockDropped(new ClipboardEntry { FilePath = "C:relative.bin" }), "Directories and relative paths are not ordinary-file drops.");
        string empty = Path.Combine(directory, "empty.txt");
        File.WriteAllBytes(empty, new byte[0]);
        Assert(!history.LockDropped(new ClipboardEntry { FilePath = empty, Text = "ambiguous" }), "A file record cannot also contain text or PNG data.");
        Assert(history.LockDropped(new ClipboardEntry { FilePath = empty }) && new FileInfo(history.LockedEntry.FilePath).Length == 0, "Empty ordinary files are valid snapshots.");
        string own = history.LockedEntry.FilePath;
        string unrelated = Path.Combine(Path.GetDirectoryName(own), "user-file.txt");
        File.WriteAllText(unrelated, "leave alone");
        Assert(history.LockDropped(TextEntry("replace file", 1)) && !File.Exists(own) && File.ReadAllText(unrelated) == "leave alone" && File.Exists(empty),
            "Successful replacement cleans only the owned snapshot, leaving unrelated neighboring files and the original source untouched.");

        string corruptDirectory = Path.Combine(directory, "corrupt-data");
        Directory.CreateDirectory(corruptDirectory);
        string metadata = Path.Combine(corruptDirectory, "clipboard-history.json");
        string malicious = "{\"Version\":1,\"Entries\":[],\"LockedEntry\":{\"Id\":\"bad\",\"FileDirectory\":\"../..\",\"CachedFileName\":\"empty.txt\"}}";
        File.WriteAllText(metadata, malicious);
        var corrupt = new ClipboardHistory(corruptDirectory);
        Assert(corrupt.LockedEntry == null && !String.IsNullOrEmpty(corrupt.LastError) && !corrupt.LockDropped(TextEntry("blocked", 0)) &&
            File.ReadAllText(metadata) == malicious && File.Exists(empty), "A malicious pinned-file cache path is rejected without overwriting metadata or touching outside files.");
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
