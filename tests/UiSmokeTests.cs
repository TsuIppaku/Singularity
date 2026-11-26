using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Orbit
{
    internal static class SmokeTests
    {
        private static int checks;
        private static readonly StringBuilder report = new StringBuilder();

        internal static void Run(AppController app, string output)
        {
            output = Path.GetFullPath(output); Directory.CreateDirectory(output);
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow; app.Now = delegate { return now; };
                var working = Placement.WorkArea(app.Dot);
                Placement.Move(app.Dot, working.Left + working.Width / 2 - 38, working.Top + 45);
                Check(app.Dot.IsVisible && app.Dot.Topmost && !app.Dot.ShowInTaskbar, "Native dot remains an independent topmost window");
                app.PreviewLauncher(); Pump();
                Check(app.Launcher.IsVisible && app.LauncherIsPreview && !app.ToolsArePinned && !app.Panel.IsVisible && !app.Utilities.IsVisible, "Hover preview reveals only the three-tool launcher");
                app.DismissPreviewForTest(false); Pump();
                Check(!app.Launcher.IsVisible && !app.LauncherIsPreview, "Leaving the expanded hover region collapses the preview");
                app.PreviewLauncher(); app.ClickDot(); app.DismissPreviewForTest(false); Pump();
                Check(app.Launcher.IsVisible && app.ToolsArePinned && !app.LauncherIsPreview, "Clicking a hover preview pins it instead of allowing automatic dismissal");
                app.ClickOutsideForTest(new Point(working.Left, working.Bottom)); Pump();
                Check(!app.Launcher.IsVisible && !app.Panel.IsVisible && !app.Utilities.IsVisible, "An outside click closes a pinned toolbar even when no child panel is open");
                app.HideTools();
                app.ClickDot(); Pump();
                Check(app.Launcher.IsVisible && !app.Panel.IsVisible && !app.Utilities.IsVisible, "Clicking dot reveals only the three-tool launcher");
                Check(app.Launcher.ToolButton("timer").ToolTip != null && app.Launcher.ToolButton("notes").ToolTip != null && app.Launcher.ToolButton("clipboard").ToolTip != null, "All three tools expose hover descriptions");
                var launcherOnly = Placement.Rectangle(app.Launcher);
                CaptureGroup(app, Path.Combine(output, "01-launcher.png"));
                Click(app.Launcher.ToolButton("timer")); Pump();
                Check(Placement.Rectangle(app.Launcher) == launcherOnly, "Opening the timer never moves the toolbar");
                Check(app.Panel.IsVisible && app.Launcher.Selected == "timer", "Timer button opens compact panel and marks selection");
                Check(Math.Abs(app.Panel.ActualWidth - 320) < 1, "First opening preserves the panel width after native placement");
                Check(Math.Abs(app.Panel.ActualHeight - TaskPanel.PreferredHeight) < 1, "Empty task panel starts at its fixed height");
                Check(app.Panel.Find<ScrollViewer>("RowsScroll").ComputedVerticalScrollBarVisibility != Visibility.Visible, "Empty task list has no scrollbar");
                var firstBar = Placement.Bounds(app.Launcher); var firstCard = Placement.Bounds(app.Panel);
                Check(Math.Abs((firstBar.Left + firstBar.Right) - (firstCard.Left + firstCard.Right)) <= 2 && firstCard.Top >= firstBar.Bottom - 2, "Timer panel opens directly below and centered on the toolbar");
                Check(FocusManager.GetFocusedElement(app.Panel) == app.Panel.TaskNameBox || app.Panel.TaskNameBox.IsKeyboardFocused, "Opening timer puts input focus in task name");
                CaptureGroup(app, Path.Combine(output, "02-empty-timer.png"));
                Capture(app.Panel, Path.Combine(output, "02-panel-only.png"));
                app.Panel.TaskNameBox.Text = "整理奇点的计划";
                PressEnter(app.Panel.TaskNameBox);
                Check(FocusManager.GetFocusedElement(app.Panel) == app.Panel.DurationBox || app.Panel.DurationBox.IsKeyboardFocused, "First Enter moves from task name to duration");
                Check(app.Engine.State.Tasks.Count == 0, "First Enter never submits the task");
                app.Panel.DurationBox.Text = "25"; PressEnter(app.Panel.DurationBox);
                var timed = app.Engine.State.Tasks.Single();
                Check(timed.Name == "整理奇点的计划" && timed.DurationSeconds == 1500 && timed.Status == OrbitTaskStatus.Pending && app.Engine.ActiveTask == null, "Second Enter creates a pending task without starting it");
                Check(app.Panel.IsVisible && app.Panel.TaskNameBox.Text == "" && app.Panel.DurationBox.Text == "", "Task submission keeps panel open and clears both inputs");
                app.Panel.TaskNameBox.Text = ""; PressEnter(app.Panel.TaskNameBox); PressEnter(app.Panel.DurationBox);
                var checklist = app.Engine.State.Tasks.Last();
                Check(checklist.IsChecklist && checklist.Name.StartsWith(now.ToLocalTime().ToString("yyyy-MM-dd")) && checklist.Name.EndsWith("任务 1"), "Blank fields create a dated, numbered check-only task");
                var checkControl = ById<CheckBox>(app.Panel, "task-" + checklist.Id + "-check");
                Check(checkControl != null && ById<Button>(app.Panel, "task-" + checklist.Id + "-play") == null, "Untimed task has a checkbox and no play control");
                Toggle(checkControl);
                Check(checklist.Status == OrbitTaskStatus.Completed && app.Engine.AlertTask == null && app.ChimeRequests == 0, "Checking an untimed task completes it without a sound");
                Click(app.Panel.Find<Button>("CompletedToggle"));
                Toggle(ById<CheckBox>(app.Panel, "task-" + checklist.Id + "-check"));
                Check(checklist.Status == OrbitTaskStatus.Pending, "Untimed task can be unchecked again");
                app.Panel.TaskNameBox.Text = "喝杯水"; app.Panel.DurationBox.Text = "10s"; PressEnter(app.Panel.DurationBox);
                var shortTask = app.Engine.State.Tasks.Last();
                int count = app.Engine.State.Tasks.Count;
                app.Panel.TaskNameBox.Text = "无效时长"; app.Panel.DurationBox.Text = "-2"; PressEnter(app.Panel.DurationBox);
                Check(app.Engine.State.Tasks.Count == count && app.Panel.Find<TextBlock>("ValidationText").Visibility == Visibility.Visible, "Invalid nonempty duration is rejected with visible feedback");
                app.Panel.TaskNameBox.Clear(); app.Panel.DurationBox.Clear(); app.Panel.ClearError();
                double originalDuration = timed.DurationSeconds;
                var playControl = ById<Button>(app.Panel, "task-" + timed.Id + "-play");
                Click(playControl);
                Check(app.Engine.ActiveTask == timed && timed.Status == OrbitTaskStatus.Running, "Row play button starts its timer");
                Check(Object.ReferenceEquals(playControl, ById<Button>(app.Panel, "task-" + timed.Id + "-play")), "Starting a timer preserves its button so the click animation can finish");
                var editControl = ById<Button>(app.Panel, "task-" + timed.Id + "-edit");
                var restartControl = ById<Button>(app.Panel, "task-" + timed.Id + "-restart");
                var deleteControl = ById<Button>(app.Panel, "task-" + timed.Id + "-delete");
                Check(editControl != null && restartControl != null && deleteControl != null, "Timed task exposes compact edit, restart and red delete actions");
                Click(editControl);
                var editName = ById<TextBox>(app.Panel, "task-" + timed.Id + "-edit-name");
                var editDuration = ById<TextBox>(app.Panel, "task-" + timed.Id + "-edit-duration");
                Check(editName != null && editDuration != null && editName.Visibility == Visibility.Visible, "Edit action changes the row into compact in-place inputs");
                editName.Text = "整理奇点的计划 · 修订"; editDuration.Text = "25m";
                Click(editControl);
                Check(timed.Name == "整理奇点的计划 · 修订" && timed.Status == OrbitTaskStatus.Running, "Saving an edit keeps the timer active and updates its task name");
                Click(restartControl);
                Check(timed.Status == OrbitTaskStatus.Running && Math.Abs(timed.RemainingSeconds - timed.DurationSeconds) < .01, "Restart action restores a running timer without opening another panel");
                DateTimeOffset? deadline = timed.DueAtUtc;
                now = now.AddSeconds(123); app.Pulse();
                Check(Math.Abs(timed.RemainingSeconds - 1377) < .01, "Running task uses its real deadline");
                app.Panel.ReorderForTest(timed.Id, shortTask.Id, true);
                Check(app.Engine.State.Tasks.Last().Id == timed.Id && timed.Name == "整理奇点的计划 · 修订" && timed.DurationSeconds == originalDuration && timed.DueAtUtc == deadline && timed.Status == OrbitTaskStatus.Running, "Dragging a running row changes only task order, never edited values or deadline");
                CaptureGroup(app, Path.Combine(output, "03-running.png")); CaptureGroup(app, Path.Combine(output, "03-running-2x.png"), 2); Capture(app.Dot, Path.Combine(output, "03-countdown-dot.png"));
                Click(ById<Button>(app.Panel, "task-" + timed.Id + "-play"));
                double remaining = timed.RemainingSeconds; now = now.AddMinutes(2); app.Pulse();
                Check(timed.Status == OrbitTaskStatus.Paused && timed.RemainingSeconds == remaining, "Row pause control preserves the remaining duration");
                Click(ById<Button>(app.Panel, "task-" + timed.Id + "-play"));
                Check(timed.Status == OrbitTaskStatus.Running, "The same row button resumes the timer");
                app.ClickDot();
                Check(!app.Launcher.IsVisible && !app.Panel.IsVisible, "Clicking dot collapses both layers");
                now = now.AddHours(1); app.Pulse();
                Check(app.Engine.AlertTask == timed && app.Alerting && app.Reminder.IsVisible && app.ChimeRequests == 1, "Task completes and reminds while all tools are collapsed");
                app.ClickDot(); app.Pulse(); app.Pulse();
                Check(!app.Alerting && app.Engine.AlertTask == null && app.ChimeRequests == 1, "Clicking dot acknowledges completion without re-triggering sound");
                app.ShowLauncher(); Click(app.Launcher.ToolButton("notes")); Pump();
                Check(app.Utilities.IsVisible && app.Utilities.Mode == "notes" && !app.Panel.IsVisible, "Notepad opens in the same tool position");
                Check(Math.Abs(app.Utilities.ActualWidth - 320) < 1, "Utility panel preserves its full width on first opening");
                app.Utilities.NotesEditor.Text = "让一个人的小工具，帮到更多人。\n今天先做好这一件事。"; app.Utilities.Flush();
                Check(File.ReadAllText(Path.Combine(output, "state", "notes.txt")).Contains("帮到更多人"), "Notepad persists text to its isolated local file");
                CaptureGroup(app, Path.Combine(output, "04-notes.png"));
                Click(app.Launcher.ToolButton("clipboard")); Pump();
                Check(app.Utilities.Mode == "clipboard" && app.Launcher.Selected == "clipboard", "Clipboard button switches the utility panel");
                Check(app.ClipboardHistory.Entries.Count == 0, "Desktop tests use an isolated clipboard history without reading the system clipboard");
                CaptureGroup(app, Path.Combine(output, "05-clipboard.png"));
                CheckClipboard(app, now, output);
                Click(app.Launcher.ToolButton("timer")); Pump();
                Check(app.Panel.IsVisible && !app.Utilities.IsVisible && app.Launcher.Selected == "timer", "Switching tools leaves exactly one content panel visible");
                Motion.Bounce(app.Launcher.ToolButton("timer"));
                Check(app.Launcher.ToolButton("timer").RenderTransform.HasAnimatedProperties, "Click bounce attaches an actual scale animation");
                app.Panel.ReorderForTest(shortTask.Id, checklist.Id, false);
                Check(app.Engine.State.Tasks[0].Id == shortTask.Id && shortTask.Name == "喝杯水" && shortTask.DurationSeconds == 10, "Row reordering also preserves pending task values");
                Click(ById<Button>(app.Panel, "task-" + shortTask.Id + "-play"));
                app.Act(delegate { app.Engine.AddTask("下一项", 60, false, now); });
                var next = app.Engine.State.Tasks.Last(); now = now.AddSeconds(11);
                Click(ById<Button>(app.Panel, "task-" + next.Id + "-play"));
                Check(app.Engine.AlertTask == shortTask && app.Engine.ActiveTask == null && app.Panel.IsVisible, "Starting at expiry preserves the previous alert and refuses silent overwrite");
                Click(app.Panel.Find<Button>("CloseButton"));
                Check(app.Reminder.IsVisible, "Closing the timer restores the outstanding completion reminder");
                Click(app.Launcher.ToolButton("notes")); Pump();
                Check(app.Reminder.IsVisible, "Switching to notepad preserves an unacknowledged reminder");
                app.Acknowledge();
                app.ShowPanel(); Pump();
                CheckStableLayout(app, now, output);
                var area = Placement.WorkArea(app.Dot); var dot = Placement.Bounds(app.Dot);
                Placement.Move(app.Dot, area.Right - dot.Width, area.Bottom - dot.Height); app.RepositionTools(); Pump();
                Check(Inside(Placement.Bounds(app.Launcher), area) && Inside(Placement.Bounds(app.Panel), area), "Both tool layers stay on screen at the bottom-right corner");
                var bar = Placement.Bounds(app.Launcher); var card = Placement.Bounds(app.Panel);
                Check(card.Bottom <= bar.Top + 2, "Near the bottom edge the panel flips above the toolbar without overlap");
                Placement.Move(app.Dot, -20000, -20000); Placement.Clamp(app.Dot); app.RepositionTools(); Pump();
                Check(Inside(Placement.Bounds(app.Dot), Placement.WorkArea(app.Dot)), "Off-screen dot is recovered safely");
                report.AppendLine("PASS: " + checks + " desktop integration checks. System clipboard untouched; audio suppressed.");
                Environment.ExitCode = 0;
            }
            catch (Exception ex) { report.AppendLine("FAIL: " + ex); Environment.ExitCode = 1; }
            finally { File.WriteAllText(Path.Combine(output, "ui-smoke-results.txt"), report.ToString(), Encoding.UTF8); app.Exit(); }
        }

        private static void CheckClipboard(AppController app, DateTimeOffset now, string output)
        {
            var originalBar = Placement.Rectangle(app.Launcher);
            var originalCard = Placement.Rectangle(app.Utilities);
            Check(ById<Button>(app.Utilities, "CaptureSlot1") == null, "Clipboard no longer requires a manual capture button");
            app.HideTools(); Pump();
            app.CaptureClipboardEntry(new ClipboardEntry { Text = "第一条文字", CapturedAtUtc = now });
            app.CaptureClipboardEntry(new ClipboardEntry { Text = "第二条文字", CapturedAtUtc = now.AddSeconds(1) });
            app.CaptureClipboardEntry(new ClipboardEntry { Text = "让知识与成果被更多人使用。", CapturedAtUtc = now.AddSeconds(2) });
            Check(!app.Utilities.IsVisible && app.ClipboardHistory.Entries.Count == 3 && app.ClipboardHistory.Entries[0].Text == "让知识与成果被更多人使用。", "Automatic captures fill three slots while the utility panel is hidden");
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Display.Brush("#223548"), null, new Rect(0, 0, 240, 140));
                drawing.DrawEllipse(Display.Brush("#DCA285"), null, new Point(180, 42), 19, 19);
                drawing.DrawGeometry(Display.Brush("#507C88"), null, Geometry.Parse("M0,140 L80,45 L160,140 Z"));
                drawing.DrawGeometry(Display.Brush("#305866"), null, Geometry.Parse("M95,140 L172,68 L240,140 Z"));
            }
            var bitmap = new RenderTargetBitmap(240, 140, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            byte[] png;
            using (var buffer = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(buffer); png = buffer.ToArray();
            }
            app.CaptureClipboardEntry(new ClipboardEntry { ImagePng = png, FileName = "山间日落.png", PixelWidth = 240, PixelHeight = 140, CapturedAtUtc = now.AddSeconds(3) });
            Check(app.ClipboardHistory.Entries.Count == 3 && app.ClipboardHistory.Entries[0].IsImage && app.ClipboardHistory.Entries[2].Text == "第二条文字", "A fourth copy enters slot one and evicts only the oldest entry");
            app.ShowTool("clipboard", false); Pump();
            Check(Placement.Rectangle(app.Launcher) == originalBar && Placement.Rectangle(app.Utilities) == originalCard, "Text and image captures preserve the toolbar anchor and clipboard panel size");
            var preview = FindFirst<Image>(app.Utilities);
            Check(preview != null && preview.Source is BitmapSource && ((BitmapSource)preview.Source).PixelWidth > 0 && preview.Stretch == Stretch.Uniform, "Image history renders a real thumbnail with its proportions preserved");
            Check(FindText(app.Utilities, "山间日落.png") && FindText(app.Utilities, "240 × 140"), "Image slot displays the filename and pixel dimensions");
            CaptureGroup(app, Path.Combine(output, "09-clipboard-image.png"));
            var third = app.ClipboardHistory.Entries[2];
            ClipboardEntry copied = null;
            app.ClipboardWriterForTest = delegate(ClipboardEntry entry) { copied = entry; return true; };
            Click(ById<Button>(app.Utilities, "CopySlot3")); Pump();
            Check(copied != null && copied.Id == third.Id && copied.Text == "第二条文字" && app.ClipboardHistory.Entries[0].Id == third.Id && app.ClipboardHistory.Entries.Count == 3, "Taking a history entry copies its full text and promotes it without a duplicate");
            var imageEntry = app.ClipboardHistory.Entries.Single(item => item.IsImage);
            Check(app.CopyClipboardEntry(imageEntry.Id) && copied.ImagePng.SequenceEqual(png), "Taking an image passes its original PNG bytes to the clipboard writer");
            Pump();
            var reopened = new ClipboardHistory(Path.Combine(output, "state"));
            Check(reopened.Entries.Count == 3 && reopened.Entries[0].IsImage && reopened.Entries[0].FileName == "山间日落.png" && reopened.Entries[0].ImagePng.SequenceEqual(png), "Image bytes, filename and recent ordering survive reopening storage");
            Check(Placement.Rectangle(app.Utilities) == originalCard, "Copying history entries cannot resize the utility panel");
            var beforeFailure = app.ClipboardHistory.Entries.Select(item => item.Id).ToArray();
            app.ClipboardWriterForTest = delegate { return false; };
            Check(!app.CopyClipboardEntry(beforeFailure[2]) && app.ClipboardHistory.Entries.Select(item => item.Id).SequenceEqual(beforeFailure), "A failed clipboard write leaves the history order intact");
            Click(ById<Button>(app.Utilities, "DeleteSlot3")); Pump();
            Check(app.ClipboardHistory.Entries.Count == 2 && Placement.Rectangle(app.Utilities) == originalCard && !app.CopyClipboardEntry(beforeFailure[2]), "Deleting a history entry leaves the third slot empty without moving the panel");
            app.CaptureClipboardEntry(new ClipboardEntry { Text = "新的复制自动排在最前面", CapturedAtUtc = now.AddSeconds(4) }); Pump();
            Check(app.ClipboardHistory.Entries.Count == 3 && app.ClipboardHistory.Entries[0].Text == "新的复制自动排在最前面", "The next automatic copy refills the three-slot history");
            app.ClipboardWriterForTest = null;
            CheckClipboardDrag(app, now, output);
        }

        private static void CheckClipboardDrag(AppController app, DateTimeOffset now, string output)
        {
            var bar = Placement.Rectangle(app.Launcher);
            var panel = Placement.Rectangle(app.Utilities);
            int copies = 0, drags = 0;
            IDataObject payload = null;
            app.ClipboardWriterForTest = delegate { copies++; return true; };
            app.ClipboardDragRunnerForTest = delegate(DependencyObject source, IDataObject data, DragDropEffects effects)
            {
                drags++; payload = data;
                Check(effects == DragDropEffects.Copy && source != null, "External clipboard drags offer copying, never moving the history source");
                return DragDropEffects.Copy;
            };
            var start = new Point(35, 35);
            var smallMove = new Point(start.X + SystemParameters.MinimumHorizontalDragDistance / 2, start.Y);
            var dragMove = new Point(start.X + SystemParameters.MinimumHorizontalDragDistance + 20, start.Y + 20);
            app.Utilities.BeginClipboardGesture(0, start, false);
            app.Utilities.MoveClipboardGesture(smallMove, true);
            Check(drags == 0 && copies == 0, "A small pointer movement does not start dragging or copy early");
            app.Utilities.EndClipboardGesture(true); Pump();
            Check(copies == 1 && drags == 0, "Releasing a short click preserves single-click copying");

            string text = app.ClipboardHistory.Entries[0].Text;
            var order = app.ClipboardHistory.Entries.Select(entry => entry.Id).ToArray();
            app.Utilities.BeginClipboardGesture(0, start, false);
            app.Utilities.MoveClipboardGesture(dragMove, true);
            app.Utilities.EndClipboardGesture(true); Pump();
            Check(drags == 1 && copies == 1 && payload.GetDataPresent(DataFormats.UnicodeText) && (string)payload.GetData(DataFormats.UnicodeText) == text, "Dragging sends complete text without an extra clipboard write on release");
            var textPaths = (string[])payload.GetData(DataFormats.FileDrop);
            Check(textPaths.Length == 1 && Path.GetExtension(textPaths[0]) == ".txt" && File.ReadAllText(textPaths[0]) == text, "Text dragging provides a readable file for folder and upload drop targets");
            Check(app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(order), "Dragging leaves clipboard history order intact");

            int imageIndex = app.ClipboardHistory.Entries.ToList().FindIndex(entry => entry.IsImage);
            var selectedImage = app.ClipboardHistory.Entries[imageIndex];
            app.Utilities.BeginClipboardGesture(imageIndex, start, false);
            // Simulate other apps copying while the mouse is down. The original
            // image is evicted from history before the drag crosses its threshold.
            for (int i = 0; i < 3; i++) app.CaptureClipboardEntry(new ClipboardEntry { Text = "拖动时的新复制 " + i, CapturedAtUtc = now.AddSeconds(10 + i) });
            app.Utilities.MoveClipboardGesture(dragMove, true);
            app.Utilities.EndClipboardGesture(true); Pump();
            var imagePaths = (string[])payload.GetData(DataFormats.FileDrop);
            Check(drags == 2 && copies == 1 && imagePaths.Length == 1 && Path.GetFileName(imagePaths[0]) == "山间日落.png" && File.ReadAllBytes(imagePaths[0]).SequenceEqual(selectedImage.ImagePng), "Drag uses the pressed image snapshot even after all three live slots change");
            Check(!app.ClipboardHistory.Entries.Any(entry => entry.Id == selectedImage.Id) && File.Exists(imagePaths[0]), "Export remains readable after the source image is evicted from history");

            string[] beforeCancel = app.ClipboardHistory.Entries.Select(entry => entry.Id).ToArray();
            app.ClipboardDragRunnerForTest = delegate { drags++; return DragDropEffects.None; };
            app.Utilities.BeginClipboardGesture(0, start, false);
            app.Utilities.MoveClipboardGesture(dragMove, true);
            app.Utilities.EndClipboardGesture(true); Pump();
            Check(copies == 1 && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(beforeCancel), "Cancelled external drags never fall through to click-copy or promotion");

            int beforeEscape = drags;
            app.Utilities.BeginClipboardGesture(0, start, false);
            app.Utilities.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(app.Utilities), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            app.Utilities.MoveClipboardGesture(dragMove, true); app.Utilities.EndClipboardGesture(true); Pump();
            Check(drags == beforeEscape && copies == 1, "Escape cancels a pending drag gesture without copying");
            app.ShowTool("clipboard", false); Pump();

            var copyButton = ById<Button>(app.Utilities, "CopySlot1");
            copyButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            app.Utilities.MoveClipboardGesture(dragMove, true); app.Utilities.EndClipboardGesture(false);
            Click(copyButton); Pump();
            Check(drags == beforeEscape && copies == 2, "Copy buttons remain normal controls rather than external drag handles");

            app.Utilities.BeginClipboardGesture(0, start, false);
            app.Utilities.Hide();
            app.Utilities.MoveClipboardGesture(dragMove, true); app.Utilities.EndClipboardGesture(true);
            app.ShowTool("clipboard", false); Pump();
            Check(drags == beforeEscape && copies == 2, "Hiding the panel cancels any pending gesture");
            Check(Placement.Rectangle(app.Utilities) == panel && Placement.Rectangle(app.Launcher) == bar, "Clipboard dragging and cancellation never move the toolbar or resize its panel");
            CaptureGroup(app, Path.Combine(output, "10-clipboard-drag.png"));
            app.ClipboardDragRunnerForTest = null;
            app.ClipboardWriterForTest = null;
        }

        private static bool FindText(DependencyObject node, string value)
        {
            var label = node as TextBlock;
            if (label != null && label.Text.Contains(value)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (FindText(VisualTreeHelper.GetChild(node, i), value)) return true;
            return false;
        }

        private static void CheckStableLayout(AppController app, DateTimeOffset now, string output)
        {
            app.Panel.ClearError(); Pump();
            var bar = Placement.Rectangle(app.Launcher);
            var card = Placement.Rectangle(app.Panel);
            int toolbarMoves = 0;
            EventHandler moved = delegate { toolbarMoves++; };
            app.Launcher.LocationChanged += moved;
            try
            {
                app.Act(delegate { for (int i = 1; i <= 40; i++) app.Engine.AddTask("待办事项 " + i, i % 2 == 0 ? 25 * 60 : 0, false, now); });
                Pump();
                Check(Placement.Rectangle(app.Panel) == card, "Forty additional tasks do not grow or move the task panel");
                var scroll = app.Panel.Find<ScrollViewer>("RowsScroll");
                Check(scroll.ScrollableHeight > 0 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, "Long task lists scroll inside the fixed viewport");
                var scrollbar = scroll.Template.FindName("PART_VerticalScrollBar", scroll) as ScrollBar;
                var track = scrollbar == null ? null : scrollbar.Template.FindName("PART_Track", scrollbar) as Track;
                Check(track != null && Math.Abs(scrollbar.ActualWidth - 12) < 1 && track.Thumb.ActualHeight >= 19.9, "Thin scrollbar keeps a usable drag target and minimum thumb length");
                var thumbLine = FindFirst<Border>(track.Thumb);
                var thumbInk = thumbLine == null ? null : thumbLine.Background as SolidColorBrush;
                Check(thumbLine != null && thumbLine.Width == 3 && thumbLine.CornerRadius.TopLeft == 1.5 && thumbInk != null && thumbInk.Color == Color.FromArgb(0x33, 0xE6, 0xE3, 0xEC), "Scrollbar is a rounded 3-DIP line in text color at 80 percent transparency");
                scroll.ScrollToBottom(); Pump();
                Check(scroll.VerticalOffset > 0 && Placement.Rectangle(app.Panel) == card, "Scrolling reaches later tasks without moving or resizing the panel");
                scroll.ScrollToTop(); Pump();
                CaptureGroup(app, Path.Combine(output, "06-fixed-list.png"));
                app.Panel.ShowError("请输入有效时长"); Pump();
                Check(Placement.Rectangle(app.Panel) == card, "Validation feedback uses the fixed panel instead of increasing its height");
                app.Panel.ClearError();
                Click(app.Panel.Find<Button>("CompletedToggle")); Pump();
                Check(Placement.Rectangle(app.Panel) == card, "Expanding completed tasks leaves panel bounds unchanged");
                app.ShowPanel(); Pump();
                Check(app.Panel.Opacity == 1 && Placement.Rectangle(app.Panel) == card, "Reopening the visible timer does not hide or reposition it");
                app.ShowTool("notes", false); Pump();
                app.ShowTool("clipboard", false); Pump();
                app.ShowTool("timer", false); Pump();
                Check(Placement.Rectangle(app.Panel) == card && Placement.Rectangle(app.Launcher) == bar && toolbarMoves == 0, "Switching all three tools never moves the toolbar or changes the timer anchor");
                app.ShowTool("notes", false); app.ShowTool("timer", false); app.ShowTool("clipboard", false); Pump();
                Check(app.Utilities.IsVisible && app.Utilities.Mode == "clipboard" && app.Utilities.Opacity == 1 && !app.Panel.IsVisible && app.Launcher.Selected == "clipboard", "Rapid switches display only the last requested tool without a transparent stuck window");
                app.ShowTool("timer", false); Pump();
                Check(toolbarMoves == 0, "Rapid switches produce no transient toolbar movement");
            }
            finally { app.Launcher.LocationChanged -= moved; }

            var area = Placement.WorkArea(app.Dot);
            double scale = VisualTreeHelper.GetDpi(app.Dot).DpiScaleY;
            var dot = Placement.Rectangle(app.Dot);
            // Leave a useful 260-DIP region below. The panel should use it, rather
            // than flipping merely because its normal height is 336 DIP.
            int lowerTop = area.Bottom - dot.Height - bar.Height - 4 - (int)Math.Ceiling(260 * scale);
            Placement.Move(app.Dot, dot.Left, lowerTop); app.RepositionTools(); Pump();
            var lowerBar = Placement.Rectangle(app.Launcher); var lowerCard = Placement.Rectangle(app.Panel);
            Check(lowerBar.Top >= Placement.Rectangle(app.Dot).Bottom && lowerCard.Top >= lowerBar.Bottom && Inside(Placement.Bounds(app.Panel), area), "Useful space below keeps the child below and scrolls within the available height");
            Check(app.Panel.ActualHeight >= 259 && app.Panel.ActualHeight <= 261, "Screen-edge height cap uses actual available space");
            CaptureGroup(app, Path.Combine(output, "07-lower-space.png"));

            // The toolbar still fits below the dot; only the child should flip.
            app.HideTools(); Pump();
            Placement.Move(app.Dot, dot.Left, area.Bottom - dot.Height - bar.Height - 30);
            app.ShowLauncher(); Pump();
            var beforeChild = Placement.Rectangle(app.Launcher);
            app.ShowPanel(); Pump();
            var aboveCard = Placement.Rectangle(app.Panel);
            Check(Placement.Rectangle(app.Launcher) == beforeChild && beforeChild.Top >= Placement.Rectangle(app.Dot).Bottom, "Near the bottom, opening a child leaves the toolbar below the dot");
            Check(aboveCard.Bottom <= Placement.Rectangle(app.Dot).Top && Inside(Placement.Bounds(app.Panel), area), "Only the child flips above the dot when lower space is insufficient");
            CaptureGroup(app, Path.Combine(output, "08-child-above.png"));
            app.Act(delegate { app.Engine.AddTask("边缘新增事项", 0, false, now); }); Pump();
            Check(Placement.Rectangle(app.Panel) == aboveCard && Placement.Rectangle(app.Launcher) == beforeChild, "Adding tasks cannot flip the child or nudge the toolbar at screen edges");
            Placement.Move(app.Dot, dot.Left, area.Top + 45); app.RepositionTools(); Pump();
            Check(Placement.Rectangle(app.Panel).Top >= Placement.Rectangle(app.Launcher).Bottom && Math.Abs(app.Panel.ActualHeight - TaskPanel.PreferredHeight) < 1, "Dragging back to free space restores the normal panel height and downward position");
        }

        private static bool Inside(Placement.NativeRect b, System.Drawing.Rectangle a) { return b.Left >= a.Left && b.Top >= a.Top && b.Right <= a.Right && b.Bottom <= a.Bottom; }
        private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; report.AppendLine("PASS " + label); }
        private static void Click(Button button) { if (button == null) throw new InvalidOperationException("Expected button was not rendered."); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static void Toggle(CheckBox box) { if (box == null) throw new InvalidOperationException("Expected checkbox was not rendered."); box.IsChecked = box.IsChecked != true; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
        private static void PressEnter(TextBox box) { box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), Environment.TickCount, Key.Return) { RoutedEvent = Keyboard.KeyDownEvent }); }
        private static T ById<T>(DependencyObject node, string id) where T : DependencyObject
        {
            var element = node as FrameworkElement; if (element != null) element.UpdateLayout();
            if (node is T && AutomationProperties.GetAutomationId(node) == id) return node as T;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var match = ById<T>(VisualTreeHelper.GetChild(node, i), id); if (match != null) return match; }
            return null;
        }
        private static T FindFirst<T>(DependencyObject node) where T : DependencyObject
        {
            var element = node as FrameworkElement; if (element != null) element.UpdateLayout();
            if (node is T) return node as T;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var match = FindFirst<T>(VisualTreeHelper.GetChild(node, i)); if (match != null) return match; }
            return null;
        }
        private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; })); Dispatcher.PushFrame(frame); }
        private static void Capture(FrameworkElement element, string path)
        {
            element.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(element);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(element); Save(bitmap, path);
        }
        private static void CaptureGroup(AppController app, string path, int factor = 1)
        {
            var windows = new List<Window> { app.Dot };
            if (app.Launcher.IsVisible) windows.Add(app.Launcher);
            if (app.Panel.IsVisible) windows.Add(app.Panel);
            if (app.Utilities.IsVisible) windows.Add(app.Utilities);
            foreach (var window in windows) window.UpdateLayout(); app.RepositionTools(); Pump();
            var loadedBefore = windows.Select(w => w.IsLoaded).ToArray();
            foreach (var window in windows)
            {
                var bounds = Placement.Bounds(window);
                report.AppendLine(String.Format("RENDER {0}: actual {1}x{2}, native {3}x{4} at {6},{7}, WPF at {8},{9}, requested width {5}, loaded {10}", window.Title, window.ActualWidth, window.ActualHeight, bounds.Width, bounds.Height, window.Width, bounds.Left, bounds.Top, window.Left, window.Top, window.IsLoaded));
            }
            int left = windows.Min(w => Placement.Bounds(w).Left) - 24, top = windows.Min(w => Placement.Bounds(w).Top) - 24;
            int width = windows.Max(w => Placement.Bounds(w).Right) - left + 24, height = windows.Max(w => Placement.Bounds(w).Bottom) - top + 24;
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(Display.Brush("#181818"), null, new Rect(0, 0, width, height));
                foreach (var window in windows)
                {
                    var b = Placement.Bounds(window);
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var snapshot = new RenderTargetBitmap(b.Width * factor, b.Height * factor, dpi.PixelsPerInchX * factor, dpi.PixelsPerInchY * factor, PixelFormats.Pbgra32);
                    snapshot.Render(window);
                    context.DrawImage(snapshot, new Rect(b.Left - left, b.Top - top, b.Width, b.Height));
                }
            }
            var bitmap = new RenderTargetBitmap(width * factor, height * factor, 96 * factor, 96 * factor, PixelFormats.Pbgra32); bitmap.Render(drawing); Save(bitmap, path);
            if (!loadedBefore.SequenceEqual(windows.Select(w => w.IsLoaded))) throw new InvalidOperationException("Rendering must not detach live windows.");
        }
        private static void Save(BitmapSource bitmap, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream); }
    }
}
