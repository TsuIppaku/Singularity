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
                var completeControl = ById<Button>(app.Panel, "task-" + checklist.Id + "-complete");
                Check(completeControl != null && ById<Button>(app.Panel, "task-" + checklist.Id + "-play") == null && ById<Button>(app.Panel, "task-" + checklist.Id + "-delete") == null, "Untimed pending task exposes completion without play or permanent-delete buttons");
                Click(completeControl);
                Check(checklist.Status == OrbitTaskStatus.Completed && app.Engine.AlertTask == null && app.ChimeRequests == 0, "Completing an untimed task moves it to history without a sound");
                Click(app.Panel.Find<Button>("CompletedToggle"));
                var checklistDelete = ById<Button>(app.Panel, "task-" + checklist.Id + "-delete");
                var checklistRestore = ById<Button>(app.Panel, "task-" + checklist.Id + "-restore");
                Check(checklistDelete != null && checklistRestore != null && ById<Button>(app.Panel, "task-" + checklist.Id + "-complete") == null, "Completed checklist replaces completion with restore and trash buttons");
                Click(checklistRestore);
                Check(checklist.Status == OrbitTaskStatus.Pending && ById<Button>(app.Panel, "task-" + checklist.Id + "-complete") != null && ById<Button>(app.Panel, "task-" + checklist.Id + "-delete") == null, "Completed checklist returns to pending through its restore button");
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
                var timedCompleteControl = ById<Button>(app.Panel, "task-" + timed.Id + "-complete");
                Check(editControl != null && restartControl != null && timedCompleteControl != null && ById<Button>(app.Panel, "task-" + timed.Id + "-delete") == null, "Timed pending task exposes edit, restart and completion instead of a trash button");
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
                CheckTaskCompletionActions(app, now, output);
                CheckOrbFileDropPreview(app, output);
                CheckOrbTextDrops(app, output);
                report.AppendLine("PASS: " + checks + " desktop integration checks. System clipboard untouched; audio suppressed.");
                Environment.ExitCode = 0;
            }
            catch (Exception ex) { report.AppendLine("FAIL: " + ex); Environment.ExitCode = 1; }
            finally { File.WriteAllText(Path.Combine(output, "ui-smoke-results.txt"), report.ToString(), Encoding.UTF8); app.Exit(); }
        }

        private static void CheckTaskCompletionActions(AppController app, DateTimeOffset now, string output)
        {
            // Run destructive task actions after reminder and layout scenarios so
            // they cannot remove another scenario's task or outstanding alert.
            var area = Placement.WorkArea(app.Dot);
            Placement.Move(app.Dot, area.Left + area.Width / 2 - 38, area.Top + 45);
            app.ShowPanel(); app.RepositionTools(); Pump();
            var panelBounds = Placement.Rectangle(app.Panel);
            var barBounds = Placement.Rectangle(app.Launcher);
            int chimes = app.ChimeRequests;
            Check(app.Engine.AlertTask == null && !app.Alerting, "Task-action checks begin with previous reminders acknowledged");

            OrbitTask checklist = null, pendingTimer = null, runningTimer = null, pausedTimer = null, removePending = null;
            app.Act(delegate
            {
                checklist = app.Engine.AddTask("完成测试 · 清单", 0, false, now);
                pendingTimer = app.Engine.AddTask("完成测试 · 尚未启动", 300, false, now);
                runningTimer = app.Engine.AddTask("完成测试 · 正在计时", 600, false, now);
                pausedTimer = app.Engine.AddTask("完成测试 · 已暂停", 420, false, now);
                removePending = app.Engine.AddTask("右键直接删除", 0, false, now);
            });
            Click(ById<Button>(app.Panel, "task-" + checklist.Id + "-complete"));
            Click(ById<Button>(app.Panel, "task-" + pendingTimer.Id + "-complete"));
            Check(checklist.Status == OrbitTaskStatus.Completed && pendingTimer.Status == OrbitTaskStatus.Completed && pendingTimer.DurationSeconds == 300 && pendingTimer.Name == "完成测试 · 尚未启动", "Checklist and unstarted timer completion preserves task identity, name and original duration");

            Click(ById<Button>(app.Panel, "task-" + runningTimer.Id + "-play"));
            Check(runningTimer.Status == OrbitTaskStatus.Running, "Manual-completion fixture starts a real running task");
            Click(ById<Button>(app.Panel, "task-" + runningTimer.Id + "-complete"));
            Check(runningTimer.Status == OrbitTaskStatus.Completed && runningTimer.DueAtUtc == null && app.Engine.ActiveTask == null, "Completing a running task stops its countdown and clears the active task");
            Click(ById<Button>(app.Panel, "task-" + pausedTimer.Id + "-play"));
            Click(ById<Button>(app.Panel, "task-" + pausedTimer.Id + "-play"));
            Check(pausedTimer.Status == OrbitTaskStatus.Paused, "Manual-completion fixture pauses its timer through the row control");
            Click(ById<Button>(app.Panel, "task-" + pausedTimer.Id + "-complete"));
            app.Pulse(); Pump();
            Check(pausedTimer.Status == OrbitTaskStatus.Completed && app.Engine.ActiveTask == null && app.Engine.AlertTask == null && !app.Alerting && !app.Reminder.IsVisible && app.ChimeRequests == chimes, "Manual completion of pending, running and paused tasks never creates a reminder or chime");

            if (app.Panel.Find<StackPanel>("CompletedList").Visibility != Visibility.Visible)
                Click(app.Panel.Find<Button>("CompletedToggle"));
            var checklistTrash = ById<Button>(app.Panel, "task-" + checklist.Id + "-delete");
            var timedTrash = ById<Button>(app.Panel, "task-" + runningTimer.Id + "-delete");
            Check(checklistTrash != null && timedTrash != null && ById<Button>(app.Panel, "task-" + checklist.Id + "-complete") == null && ById<Button>(app.Panel, "task-" + runningTimer.Id + "-play") == null, "Expanded completed checklist and timer rows expose trash instead of completion or timer controls");
            Check(ById<Button>(app.Panel, "task-" + checklist.Id + "-restore") != null && ById<Button>(app.Panel, "task-" + runningTimer.Id + "-restore") != null, "Completed checklist and timer rows both expose a restore arrow");
            app.Panel.Find<ScrollViewer>("RowsScroll").ScrollToBottom(); Pump();
            CaptureGroup(app, Path.Combine(output, "11-completed-actions.png"));
            int beforeDelete = app.Engine.State.Tasks.Count;
            Click(checklistTrash);
            Check(app.Engine.State.Tasks.Count == beforeDelete - 1 && !app.Engine.State.Tasks.Any(task => task.Id == checklist.Id) && app.Engine.State.Tasks.Contains(runningTimer), "A completed row's trash button permanently removes only that task");
            var pendingComplete = ById<Button>(app.Panel, "task-" + removePending.Id + "-complete");
            Check(pendingComplete != null && ById<Button>(app.Panel, "task-" + removePending.Id + "-delete") == null, "Pending rows reserve their visible action for completion");
            ClickMenu(TaskMenuItem(pendingComplete, removePending.Id, "删除任务"));
            Check(!app.Engine.State.Tasks.Any(task => task.Id == removePending.Id) && removePending.Status == OrbitTaskStatus.Pending, "Pending context-menu deletion removes a task directly without marking it completed");

            OrbitTask keepRunning = null, keepPaused = null;
            app.Act(delegate
            {
                keepPaused = app.Engine.AddTask("清除后保留 · 暂停", 720, false, now);
                keepRunning = app.Engine.AddTask("清除后保留 · 运行", 900, false, now);
            });
            Click(ById<Button>(app.Panel, "task-" + keepPaused.Id + "-play"));
            Click(ById<Button>(app.Panel, "task-" + keepPaused.Id + "-play"));
            Click(ById<Button>(app.Panel, "task-" + keepRunning.Id + "-play"));
            DateTimeOffset? activeDeadline = keepRunning.DueAtUtc;
            Click(ById<Button>(app.Panel, "task-" + runningTimer.Id + "-restore")); Pump();
            var lastPendingRow = app.Panel.Find<StackPanel>("PendingList").Children.OfType<Grid>().LastOrDefault();
            Check(runningTimer.Status == OrbitTaskStatus.Pending && runningTimer.RemainingSeconds == runningTimer.DurationSeconds && runningTimer.DurationSeconds == 600 && runningTimer.DueAtUtc == null && runningTimer.CompletedAtUtc == null, "Restoring a completed timer resets its original duration and leaves it waiting to start");
            Check(app.Engine.State.Tasks.Where(task => task.Status != OrbitTaskStatus.Completed).Last() == runningTimer && lastPendingRow != null && Object.Equals(lastPendingRow.Tag, runningTimer.Id), "Restored timer appears at the end of both the pending model and rendered list");
            Check(app.Engine.ActiveTask == keepRunning && keepRunning.Status == OrbitTaskStatus.Running && keepRunning.DueAtUtc == activeDeadline && ById<Button>(app.Panel, "task-" + runningTimer.Id + "-play") != null, "Restoring a timer preserves another running countdown and offers play for the restored task");
            var unfinished = app.Engine.State.Tasks.Where(task => task.Status != OrbitTaskStatus.Completed)
                .Select(task => new { Task = task, task.Name, task.DurationSeconds, task.RemainingSeconds, task.DueAtUtc, task.Status }).ToArray();
            var allBeforeClear = app.Engine.State.Tasks.ToArray();
            var completedIds = allBeforeClear.Where(task => task.Status == OrbitTaskStatus.Completed).Select(task => task.Id).ToArray();
            Check(completedIds.Length >= 3 && keepRunning.Status == OrbitTaskStatus.Running && keepPaused.Status == OrbitTaskStatus.Paused, "Bulk-clear fixture contains completed tasks alongside pending, paused and running work");
            Click(app.Panel.Find<Button>("ClearCompleted")); Pump();
            var prompt = app.Panel.Find<FrameworkElement>("ClearCompletedPrompt");
            Check(prompt != null && prompt.Visibility == Visibility.Visible && FindText(prompt, "清除") && FindText(prompt, "已完成"), "Clear completed opens an inline confirmation that describes the destructive action");
            Check(app.Engine.State.Tasks.SequenceEqual(allBeforeClear) && Placement.Rectangle(app.Panel) == panelBounds && Placement.Rectangle(app.Launcher) == barBounds, "Opening confirmation neither deletes tasks nor moves or resizes the tool windows");
            CaptureGroup(app, Path.Combine(output, "12-clear-completed-confirmation.png"));
            Click(app.Panel.Find<Button>("ClearCompletedNo")); Pump();
            Check(prompt.Visibility != Visibility.Visible && app.Engine.State.Tasks.SequenceEqual(allBeforeClear) && completedIds.All(id => app.Engine.State.Tasks.Any(task => task.Id == id && task.Status == OrbitTaskStatus.Completed)), "Cancelling bulk clear closes the confirmation and preserves every completed task");
            Click(app.Panel.Find<Button>("ClearCompleted"));
            Click(app.Panel.Find<Button>("ClearCompletedYes")); Pump();
            Check(prompt.Visibility != Visibility.Visible && !app.Engine.State.Tasks.Any(task => task.Status == OrbitTaskStatus.Completed) && app.Engine.State.Tasks.SequenceEqual(unfinished.Select(item => item.Task)), "Confirming bulk clear removes only completed tasks and preserves unfinished task order");
            Check(unfinished.All(item => item.Task.Name == item.Name && item.Task.DurationSeconds == item.DurationSeconds && item.Task.RemainingSeconds == item.RemainingSeconds && item.Task.DueAtUtc == item.DueAtUtc && item.Task.Status == item.Status) && app.Engine.ActiveTask == keepRunning, "Bulk clear preserves pending and paused values plus the running timer's deadline and active state");
            Check(app.ChimeRequests == chimes && app.Engine.AlertTask == null && !app.Alerting && Placement.Rectangle(app.Panel) == panelBounds && Placement.Rectangle(app.Launcher) == barBounds, "Completion and removal remain silent and keep the fixed panel and toolbar in place");
            var saved = new StateStore(Path.Combine(output, "state")).Load();
            Check(saved.Tasks.Select(task => task.Id).SequenceEqual(unfinished.Select(item => item.Task.Id)) && saved.ActiveTaskId == keepRunning.Id, "Confirmed bulk removal persists while retaining the active unfinished task");
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
            CheckLockedClipboard(app, now, output, png);
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

        private static void CheckLockedClipboard(AppController app, DateTimeOffset now, string output, byte[] imagePng)
        {
            var panel = Placement.Rectangle(app.Utilities);
            var bar = Placement.Rectangle(app.Launcher);
            var fourth = ById<Border>(app.Utilities, "ClipboardCard4");
            var confirm = ById<CheckBox>(app.Utilities, "ConfirmLockedReplacement");
            Check(app.ClipboardHistory.LockedEntry == null && (fourth == null || fourth.Visibility != Visibility.Visible), "The extra locked card stays hidden until an item is pinned");
            Check(confirm != null && confirm.IsChecked == true && app.ClipboardHistory.ConfirmLockedReplacement, "Replacing a locked item asks for confirmation by default");
            Check(Enumerable.Range(1, 3).All(slot => ById<Button>(app.Utilities, "LockSlot" + slot) != null), "Each occupied recent slot exposes a lock action");

            int copies = 0, drags = 0;
            ClipboardEntry copied = null;
            IDataObject payload = null;
            app.ClipboardWriterForTest = delegate(ClipboardEntry entry) { copies++; copied = entry; return true; };
            app.ClipboardDragRunnerForTest = delegate(DependencyObject source, IDataObject data, DragDropEffects effects)
            {
                drags++; payload = data;
                return DragDropEffects.Copy;
            };
            try
            {
                var initial = app.ClipboardHistory.Entries[0];
                var lockButton = ById<Button>(app.Utilities, "LockSlot1");
                var start = new Point(30, 35);
                var dragMove = new Point(start.X + SystemParameters.MinimumHorizontalDragDistance + 20, start.Y + 20);
                lockButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                app.Utilities.MoveClipboardGesture(dragMove, true); app.Utilities.EndClipboardGesture(false);
                Check(copies == 0 && drags == 0 && !app.Utilities.ClipboardGesturePending, "Pressing a lock button does not start a card drag or copy its content");
                Click(lockButton); Pump();
                fourth = ById<Border>(app.Utilities, "ClipboardCard4");
                var third = ById<Border>(app.Utilities, "ClipboardCard3");
                Check(app.ClipboardHistory.LockedEntry != null && app.ClipboardHistory.LockedEntry.Id == initial.Id && fourth != null && fourth.Visibility == Visibility.Visible && !LockedPromptVisible(app), "Locking the first item immediately reveals its separate fourth card");
                var lockedBorder = fourth.BorderBrush as SolidColorBrush;
                Check(lockedBorder != null && lockedBorder.Color.R > lockedBorder.Color.B && lockedBorder.Color.G > lockedBorder.Color.B && ById<Button>(app.Utilities, "UnlockSlot4") != null && ById<Button>(app.Utilities, "DeleteSlot4") == null, "Locked card uses a yellow border and unlock action without a delete button");
                Check(third != null && fourth.TranslatePoint(new Point(), app.Utilities).Y >= third.TranslatePoint(new Point(0, third.ActualHeight), app.Utilities).Y && Placement.Rectangle(app.Utilities) == panel && Placement.Rectangle(app.Launcher) == bar, "The fourth card follows recent slots without growing the panel or moving the launcher");

                for (int i = 0; i < 4; i++) app.CaptureClipboardEntry(new ClipboardEntry { Text = "锁定后的新复制 " + i, CapturedAtUtc = now.AddSeconds(30 + i) });
                Pump();
                Check(app.ClipboardHistory.Entries.Count == 3 && app.ClipboardHistory.Entries[0].Text == "锁定后的新复制 3" && app.ClipboardHistory.LockedEntry.Id == initial.Id && app.ClipboardHistory.LockedEntry.Text == initial.Text, "Recent copies rotate through three slots while the locked content remains unchanged");
                CheckClipboardRowActions(app.Utilities);
                var recentOrder = app.ClipboardHistory.Entries.Select(entry => entry.Id).ToArray();
                Click(ById<Button>(app.Utilities, "CopySlot4")); Pump();
                Check(copies == 1 && copied != null && copied.Id == initial.Id && copied.Text == initial.Text && app.ClipboardHistory.LockedEntry.Id == initial.Id && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentOrder), "Copying the locked text preserves the pin and recent-slot order");
                var scroll = FindFirst<ScrollViewer>(app.Utilities);
                if (scroll != null) scroll.ScrollToBottom();
                CaptureGroup(app, Path.Combine(output, "13-locked-clipboard.png"));

                var replacement = app.ClipboardHistory.Entries[1];
                Click(ById<Button>(app.Utilities, "LockSlot2")); Pump();
                Check(LockedPromptVisible(app) && app.ClipboardHistory.LockedEntry.Id == initial.Id && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentOrder), "A replacement lock stages confirmation without changing either clipboard collection");
                CaptureGroup(app, Path.Combine(output, "14-locked-replacement.png"));
                Click(ById<Button>(app.Utilities, "ConfirmLockedReplaceNo")); Pump();
                Check(!LockedPromptVisible(app) && app.ClipboardHistory.LockedEntry.Id == initial.Id && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentOrder), "Cancelling locked replacement keeps both the old pin and incoming recent entry");
                Click(ById<Button>(app.Utilities, "LockSlot2"));
                Click(ById<Button>(app.Utilities, "ConfirmLockedReplaceYes")); Pump();
                Check(!LockedPromptVisible(app) && app.ClipboardHistory.LockedEntry.Id == replacement.Id && !app.ClipboardHistory.Entries.Any(entry => entry.Id == initial.Id) && app.ClipboardHistory.Entries.Count <= 3, "Confirming replacement installs the selected entry and discards the former pin");

                SetLockedConfirmation(app, false);
                var reopened = new ClipboardHistory(Path.Combine(output, "state"));
                Check(!app.ClipboardHistory.ConfirmLockedReplacement && !reopened.ConfirmLockedReplacement && reopened.LockedEntry != null && reopened.LockedEntry.Id == replacement.Id, "The replacement-confirmation preference and locked entry persist together");
                var immediate = app.ClipboardHistory.Entries[0];
                Click(ById<Button>(app.Utilities, "LockSlot1")); Pump();
                Check(!LockedPromptVisible(app) && app.ClipboardHistory.LockedEntry.Id == immediate.Id && !app.ClipboardHistory.Entries.Any(entry => entry.Id == replacement.Id), "Disabling confirmation makes subsequent lock replacement immediate");
                var textUnlockOrder = app.ClipboardHistory.Entries.Take(2).Select(entry => entry.Id).Concat(new[] { immediate.Id }).ToArray();
                int copiesBeforeTextUnlock = copies;
                Click(ById<Button>(app.Utilities, "UnlockSlot4")); Pump();
                Check(app.ClipboardHistory.LockedEntry == null && ById<Border>(app.Utilities, "ClipboardCard4").Visibility != Visibility.Visible && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(textUnlockOrder) && app.ClipboardHistory.Entries.Last().Id == immediate.Id && app.ClipboardHistory.Entries.Last().Text == immediate.Text && copies == copiesBeforeTextUnlock, "Unlocking text appends its unchanged content after the existing recent entries without copying to the system clipboard");

                string fixtures = Path.Combine(output, "locked-clipboard-fixtures");
                Directory.CreateDirectory(fixtures);
                string filePath = Path.Combine(fixtures, "归档资料.bin");
                byte[] fileBytes = { 0, 1, 2, 3, 5, 8, 13, 21, 34, 55 };
                File.WriteAllBytes(filePath, fileBytes);
                string imagePath = Path.Combine(fixtures, "锁定图像.png");
                File.WriteAllBytes(imagePath, imagePng);
                Check(app.Utilities.TryLockDroppedFile(filePath), "A single ordinary file is accepted by the clipboard drop command");
                Pump();
                var lockedFile = app.ClipboardHistory.LockedEntry;
                Check(lockedFile != null && !lockedFile.IsImage && lockedFile.FileName == Path.GetFileName(filePath) && FindText(ById<Border>(app.Utilities, "ClipboardCard4"), Path.GetFileName(filePath)), "Dropped ordinary files display their original filename in the locked card");
                int copiesBeforeDrag = copies;
                Click(ById<Button>(app.Utilities, "CopySlot4")); Pump();
                Check(copies == copiesBeforeDrag + 1 && copied.Id == lockedFile.Id && app.ClipboardHistory.LockedEntry.Id == lockedFile.Id, "The locked file copy button uses the clipboard writer without unlocking it");
                string copiedFilePath = copied.FilePath;
                Check(copiedFilePath != lockedFile.FilePath && File.ReadAllBytes(copiedFilePath).SequenceEqual(fileBytes), "Copying a locked file prepares an independent file for a later paste");
                recentOrder = app.ClipboardHistory.Entries.Select(entry => entry.Id).ToArray();
                Check(app.Utilities.BeginClipboardGesture(3, start, false), "The locked fourth card accepts the shared external-drag gesture");
                app.Utilities.MoveClipboardGesture(dragMove, true); app.Utilities.EndClipboardGesture(true); Pump();
                var filePaths = payload == null ? null : payload.GetData(DataFormats.FileDrop) as string[];
                Check(drags == 1 && copies == copiesBeforeDrag + 1 && filePaths != null && filePaths.Length == 1 && Path.GetFileName(filePaths[0]) == Path.GetFileName(filePath) && File.ReadAllBytes(filePaths[0]).SequenceEqual(fileBytes), "Dragging a locked file offers its exact bytes and filename without an extra clipboard copy");
                Check(app.ClipboardHistory.LockedEntry.Id == lockedFile.Id && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentOrder), "Dragging a locked file preserves both the pin and recent-history order");
                Check(!app.Utilities.TryLockDroppedFile(Path.Combine(fixtures, "missing.bin")) && !app.Utilities.TryLockDroppedFile(fixtures) && app.ClipboardHistory.LockedEntry.Id == lockedFile.Id, "Invalid file paths and directory drops leave the existing pin intact");

                SetLockedConfirmation(app, true);
                Check(app.Utilities.TryLockDroppedFile(imagePath), "An image drop is accepted while replacement confirmation is enabled");
                Pump();
                Check(LockedPromptVisible(app) && app.ClipboardHistory.LockedEntry.Id == lockedFile.Id, "Dropping an image over an occupied pin waits for confirmation");
                Click(ById<Button>(app.Utilities, "ConfirmLockedReplaceNo")); Pump();
                Check(app.ClipboardHistory.LockedEntry.Id == lockedFile.Id && !LockedPromptVisible(app), "Cancelling a dropped replacement preserves the previously locked file");
                Check(app.Utilities.TryLockDroppedFile(imagePath), "The same image can be staged again after cancellation");
                Click(ById<Button>(app.Utilities, "ConfirmLockedReplaceYes")); Pump();
                var lockedImage = app.ClipboardHistory.LockedEntry;
                var imageView = FindFirst<Image>(ById<Border>(app.Utilities, "ClipboardCard4"));
                Check(lockedImage != null && lockedImage.IsImage && lockedImage.FileName == Path.GetFileName(imagePath) && lockedImage.PixelWidth == 240 && lockedImage.PixelHeight == 140 && imageView != null && imageView.Source is BitmapSource && imageView.Stretch == Stretch.Uniform, "Confirmed image drops replace the pin and render a proportionate thumbnail with original dimensions");
                Check(!app.ClipboardHistory.Entries.Any(entry => entry.Id == lockedFile.Id) && app.ClipboardHistory.Entries.Count <= 3, "Replacing a pinned file discards it without adding it to the three recent slots");
                Check(!File.Exists(lockedFile.FilePath) && File.ReadAllBytes(copiedFilePath).SequenceEqual(fileBytes), "Replacing the pin leaves the previously copied file available to paste");
                app.Utilities.BeginClipboardGesture(3, start, false);
                app.Utilities.MoveClipboardGesture(dragMove, true); app.Utilities.EndClipboardGesture(true); Pump();
                var imagePaths = payload.GetData(DataFormats.FileDrop) as string[];
                Check(drags == 2 && imagePaths != null && imagePaths.Length == 1 && File.ReadAllBytes(imagePaths[0]).SequenceEqual(lockedImage.ImagePng) && app.ClipboardHistory.LockedEntry.Id == lockedImage.Id, "Locked image dragging exports its stored PNG while keeping the image pinned");
                app.CaptureClipboardEntry(new ClipboardEntry { Text = "解锁前的新复制", CapturedAtUtc = now.AddSeconds(40) }); Pump();
                Check(app.ClipboardHistory.Entries.Count == 3, "Image-unlock fixture has a full recent history before restoring the pin");
                var imageUnlockOrder = app.ClipboardHistory.Entries.Take(2).Select(entry => entry.Id).Concat(new[] { lockedImage.Id }).ToArray();
                int copiesBeforeImageUnlock = copies;
                Click(ById<Button>(app.Utilities, "UnlockSlot4")); Pump();
                var restoredImage = app.ClipboardHistory.Entries.Last();
                Check(app.ClipboardHistory.LockedEntry == null && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(imageUnlockOrder) && restoredImage.Id == lockedImage.Id && restoredImage.IsImage && restoredImage.FileName == lockedImage.FileName && restoredImage.ImagePng.SequenceEqual(lockedImage.ImagePng) && copies == copiesBeforeImageUnlock, "Unlocking an image preserves the two newest entries and appends the original image last without a clipboard write");

                recentOrder = app.ClipboardHistory.Entries.Select(entry => entry.Id).ToArray();
                Check(app.Utilities.TryLockDroppedFile(filePath), "A file can be pinned again after image unlock");
                Click(ById<Button>(app.Utilities, "UnlockSlot4")); Pump();
                Check(app.ClipboardHistory.LockedEntry == null && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentOrder), "Unlocking an ordinary file simply clears the pin and leaves recent text and images untouched");
                Check(Placement.Rectangle(app.Utilities) == panel && Placement.Rectangle(app.Launcher) == bar && !LockedPromptVisible(app), "Pinning, replacement, dragging and unlocking preserve the fixed clipboard panel and toolbar geometry");
                reopened = new ClipboardHistory(Path.Combine(output, "state"));
                Check(reopened.LockedEntry == null && reopened.ConfirmLockedReplacement && reopened.Entries.Select(entry => entry.Id).SequenceEqual(recentOrder), "Unlocking and the restored confirmation preference persist without changing recent order");
            }
            finally
            {
                app.ClipboardWriterForTest = null;
                app.ClipboardDragRunnerForTest = null;
            }
        }

        private static void CheckClipboardRowActions(UtilityPanel panel)
        {
            panel.UpdateLayout();
            Rect firstCopy = Rect.Empty, firstLock = Rect.Empty;
            bool recentLayout = true, recentGlyphs = true;
            for (int slot = 1; slot <= 3; slot++)
            {
                var card = ById<Border>(panel, "ClipboardCard" + slot);
                var copy = ById<Button>(panel, "CopySlot" + slot);
                var pin = ById<Button>(panel, "LockSlot" + slot);
                var remove = ById<Button>(panel, "DeleteSlot" + slot);
                if (card == null || copy == null || pin == null || remove == null)
                    throw new InvalidOperationException("Expected the recent clipboard row's copy, lock and delete controls.");
                Rect c = ClipboardActionBounds(copy, card), p = ClipboardActionBounds(pin, card), d = ClipboardActionBounds(remove, card);
                double primaryGap = p.Left - c.Right, deleteGap = d.Left - p.Right;
                recentLayout &= new[] { c, p, d }.All(bounds => Math.Abs(bounds.Width - 24) < 1 && Math.Abs(bounds.Height - 24) < 1)
                    && Math.Abs(c.Top + c.Height / 2 - card.ActualHeight / 2) < 1
                    && Math.Abs(c.Top - p.Top) < 1 && Math.Abs(c.Top - d.Top) < 1
                    && primaryGap >= 0 && primaryGap <= 5 && deleteGap >= 8 && deleteGap <= 10
                    && c.Left >= 0 && d.Right <= card.ActualWidth;
                if (slot == 1) { firstCopy = c; firstLock = p; }
                else recentLayout &= Math.Abs(c.Left - firstCopy.Left) < 1 && Math.Abs(p.Left - firstLock.Left) < 1;
                var icon = pin.Content as VectorIcon;
                recentGlyphs &= icon != null && icon.Kind == "unlock";
            }
            Check(recentLayout, "Recent clipboard actions share a centered 24-DIP row in Copy-Lock-Delete order, with a separate 8-10-DIP delete gap");

            var lockedCard = ById<Border>(panel, "ClipboardCard4");
            var lockedCopy = ById<Button>(panel, "CopySlot4");
            var unlock = ById<Button>(panel, "UnlockSlot4");
            if (lockedCard == null || lockedCopy == null || unlock == null)
                throw new InvalidOperationException("Expected the locked clipboard row's copy and unlock controls.");
            Rect lc = ClipboardActionBounds(lockedCopy, lockedCard), lu = ClipboardActionBounds(unlock, lockedCard);
            Check(new[] { lc, lu }.All(bounds => Math.Abs(bounds.Width - 24) < 1 && Math.Abs(bounds.Height - 24) < 1)
                && Math.Abs(lc.Top + lc.Height / 2 - lockedCard.ActualHeight / 2) < 1 && Math.Abs(lc.Top - lu.Top) < 1
                && lc.Right <= lu.Left && Math.Abs(lc.Left - firstCopy.Left) < 1 && Math.Abs(lu.Left - firstLock.Left) < 1
                && ById<Button>(panel, "DeleteSlot4") == null,
                "The locked row keeps Copy-Unlock centered in the same columns without shifting into the absent delete column");
            var lockedIcon = unlock.Content as VectorIcon;
            Check(recentGlyphs && lockedIcon != null && lockedIcon.Kind == "lock", "Recent rows use the open-lock glyph and the pinned row uses the closed-lock glyph");
        }

        private static Rect ClipboardActionBounds(Button button, FrameworkElement card)
        {
            // Layout positions exclude the button's transient press/hover scale,
            // so animation progress cannot change spacing regression results.
            var parent = VisualTreeHelper.GetParent(button) as Visual;
            Vector offset = VisualTreeHelper.GetOffset(button);
            return parent.TransformToAncestor(card).TransformBounds(new Rect(new Point(offset.X, offset.Y), button.RenderSize));
        }

        private static void CheckOrbFileDropPreview(AppController app, string output)
        {
            app.HideTools(); Pump();
            var bounds = Placement.Rectangle(app.Dot);
            var active = app.Engine.ActiveTask;
            DateTimeOffset? deadline = active == null ? null : active.DueAtUtc;
            var networkData = new DataObject();
            networkData.SetData(DataFormats.FileDrop, new[] { "\\\\orbit-smoke.invalid\\share\\missing.bin" });
            Check(!app.Dot.PreviewFileDrop(networkData, DragDropEffects.Copy), "Orb hover rejects UNC file paths before probing external storage");
            app.Dot.CancelFileDropPreview();
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { Path.Combine(output, "locked-clipboard-fixtures", "归档资料.bin") });
            bool accepted = app.Dot.PreviewFileDrop(data, DragDropEffects.Copy);
            app.PreviewLauncher();
            // Let the native target's small morph finish before capturing it.
            var frame = new DispatcherFrame();
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            settle.Tick += delegate { settle.Stop(); frame.Continue = false; };
            settle.Start(); Dispatcher.PushFrame(frame);
            Check(accepted && app.Dot.IsReceivingFileDrop && !app.Launcher.IsVisible && !app.Utilities.IsVisible && Placement.Rectangle(app.Dot) == bounds && FindText(app.Dot, "松开放入"), "File drag previews on the closed orb without opening tools or changing its native bounds");
            Capture(app.Dot, Path.Combine(output, "15-orb-file-drop.png"));
            app.Dot.CancelFileDropPreview(); Pump();
            Check(!app.Dot.IsReceivingFileDrop && active != null && app.Engine.ActiveTask == active && active.Status == OrbitTaskStatus.Running && active.DueAtUtc == deadline && FindFirst<TextBlock>(app.Dot).Visibility == Visibility.Visible && Placement.Rectangle(app.Dot) == bounds, "Cancelling the orb drop preview restores the countdown without changing its active timer or deadline");
            Check(app.Dot.ReceiveFileDrop(data, DragDropEffects.Copy) && app.ClipboardHistory.LockedEntry.IsFile && !app.Utilities.IsVisible,
                "Dropping into an empty pin locks the file directly from the closed orb");
            string firstPinId = app.ClipboardHistory.LockedEntry.Id;
            var imageDrop = new DataObject();
            imageDrop.SetData(DataFormats.FileDrop, new[] { Path.Combine(output, "locked-clipboard-fixtures", "锁定图像.png") });
            Check(app.Dot.ReceiveFileDrop(imageDrop, DragDropEffects.Copy), "The closed orb accepts a replacement candidate");
            Pump();
            Check(app.Utilities.IsVisible && LockedPromptVisible(app) && app.ClipboardHistory.LockedEntry.Id == firstPinId,
                "An occupied pin opens confirmation from the orb before changing the locked entry");
            Click(ById<Button>(app.Utilities, "ConfirmLockedReplaceYes")); Pump();
            Check(app.ClipboardHistory.LockedEntry.IsImage && !LockedPromptVisible(app),
                "Confirming the orb drop replaces the locked file with the dropped image");
        }

        private static void CheckOrbTextDrops(AppController app, string output)
        {
            var active = app.Engine.ActiveTask;
            Check(active != null && active.Status == OrbitTaskStatus.Running, "Text-drop mode checks run with a live countdown");
            DateTimeOffset? deadline = active.DueAtUtc;
            var dotBounds = Placement.Rectangle(app.Dot);
            var recentIds = app.ClipboardHistory.Entries.Select(entry => entry.Id).ToArray();
            int taskCount = app.Engine.State.Tasks.Count;
            string notes = app.Utilities.NotesEditor.Text;
            var previousWriter = app.ClipboardWriterForTest;
            int clipboardWrites = 0;
            app.ClipboardWriterForTest = delegate { clipboardWrites++; return true; };
            try
            {
                SetLockedConfirmation(app, false);
                foreach (string mode in new[] { "bare", "launcher", "timer", "notes", "clipboard" })
                {
                    foreach (string format in new[] { DataFormats.UnicodeText, DataFormats.Text })
                    {
                        ShowOrbDropTestMode(app, mode);
                        bool launcherVisible = app.Launcher.IsVisible, timerVisible = app.Panel.IsVisible, utilityVisible = app.Utilities.IsVisible;
                        string utilityMode = app.Utilities.Mode;
                        string originalPin = app.ClipboardHistory.LockedEntry.Id;
                        string text = format == DataFormats.UnicodeText ? "奇点文本拖入 · " + mode + "\r\n保留换行与符号：✓" : "Orbit plain text / " + mode + "\r\nSecond line stays intact.";
                        var data = new DataObject();
                        // Test each advertised format itself, not WPF's automatic
                        // conversion from one text format into the other.
                        data.SetData(format, text, false);
                        bool previewAccepted = app.Dot.PreviewFileDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
                        app.PreviewLauncher(); Pump();
                        Check(previewAccepted && app.Dot.IsReceivingFileDrop && app.ClipboardHistory.LockedEntry.Id == originalPin && app.Launcher.IsVisible == launcherVisible && app.Panel.IsVisible == timerVisible && app.Utilities.IsVisible == utilityVisible && app.Utilities.Mode == utilityMode,
                            mode + " / " + format + ": orb preview accepts text while preserving the current tool mode and pin");
                        bool accepted = app.Dot.ReceiveFileDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
                        Pump();
                        var locked = app.ClipboardHistory.LockedEntry;
                        Check(accepted && !app.Dot.IsReceivingFileDrop && locked != null && !locked.IsImage && !locked.IsFile && locked.Text == text && locked.Id != originalPin && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentIds) && app.Launcher.IsVisible == launcherVisible && app.Panel.IsVisible == timerVisible && app.Utilities.IsVisible == utilityVisible && app.Utilities.Mode == utilityMode && Placement.Rectangle(app.Dot) == dotBounds,
                            mode + " / " + format + ": orb drop locks the full text without opening another tool or changing recent rows");
                    }
                }
                Check(Enumerable.Range(1, 4).All(slot => ById<Border>(app.Utilities, "ClipboardCard" + slot) != null && ById<Border>(app.Utilities, "ClipboardCard" + slot).Visibility == Visibility.Visible) && ById<Border>(app.Utilities, "ClipboardCard5") == null,
                    "Repeated text drops retain three recent cards and exactly one separate locked card");

                var bitmap = new BitmapImage();
                using (var stream = File.OpenRead(Path.Combine(output, "locked-clipboard-fixtures", "锁定图像.png")))
                {
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                }
                var bitmapData = new DataObject(); bitmapData.SetData(DataFormats.Bitmap, bitmap, false);
                Check(app.Dot.PreviewFileDrop(bitmapData, DragDropEffects.Copy) && app.Dot.ReceiveFileDrop(bitmapData, DragDropEffects.Copy) && app.ClipboardHistory.LockedEntry.IsImage && app.ClipboardHistory.LockedEntry.PixelWidth == 240 && app.ClipboardHistory.LockedEntry.PixelHeight == 140 && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentIds),
                    "The same orb route accepts an in-memory bitmap without requiring a file-drop format");

                SetLockedConfirmation(app, true);
                var original = app.ClipboardHistory.LockedEntry;
                ShowOrbDropTestMode(app, "notes");
                var unicodeReplacement = new DataObject(); unicodeReplacement.SetData(DataFormats.UnicodeText, "待确认的中文文本\r\n取消后应保留旧锁定", false);
                Check(app.Dot.PreviewFileDrop(unicodeReplacement, DragDropEffects.Copy) && app.Dot.ReceiveFileDrop(unicodeReplacement, DragDropEffects.Copy), "Notes mode accepts a Unicode replacement candidate through the orb");
                Pump();
                Check(app.Utilities.IsVisible && app.Utilities.Mode == "clipboard" && app.Utilities.ClipboardLockConfirmationPending && LockedPromptVisible(app) && app.ClipboardHistory.LockedEntry.Id == original.Id && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentIds),
                    "Text replacement opens confirmation only when required and retains the previous pin until a decision");
                Click(ById<Button>(app.Utilities, "ConfirmLockedReplaceNo")); Pump();
                Check(!app.Utilities.ClipboardLockConfirmationPending && !LockedPromptVisible(app) && app.ClipboardHistory.LockedEntry.Id == original.Id && app.ClipboardHistory.LockedEntry.ImagePng.SequenceEqual(original.ImagePng) && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentIds),
                    "Cancelling a Unicode text drop preserves the locked image bytes and all recent rows");

                ShowOrbDropTestMode(app, "timer");
                const string finalText = "Plain text replacement\r\nConfirmed through the orb.";
                var plainReplacement = new DataObject(); plainReplacement.SetData(DataFormats.Text, finalText, false);
                Check(app.Dot.PreviewFileDrop(plainReplacement, DragDropEffects.Copy) && app.Dot.ReceiveFileDrop(plainReplacement, DragDropEffects.Copy), "Timer mode accepts a plain-Text replacement candidate through the orb");
                Pump();
                Check(LockedPromptVisible(app) && app.ClipboardHistory.LockedEntry.Id == original.Id, "Plain text replacement also waits for confirmation instead of overwriting the old pin");
                Click(ById<Button>(app.Utilities, "ConfirmLockedReplaceYes")); Pump();
                Check(!LockedPromptVisible(app) && !app.Utilities.ClipboardLockConfirmationPending && app.ClipboardHistory.LockedEntry.Text == finalText && app.ClipboardHistory.LockedEntry.Id != original.Id && !app.ClipboardHistory.Entries.Any(entry => entry.Id == original.Id) && app.ClipboardHistory.Entries.Select(entry => entry.Id).SequenceEqual(recentIds),
                    "Confirming plain text replacement discards only the prior pin and leaves recent-history rows intact");
                app.Dot.CancelFileDropPreview(); app.Pulse(); Pump();
                Check(clipboardWrites == 0 && app.Engine.State.Tasks.Count == taskCount && app.Engine.ActiveTask == active && active.Status == OrbitTaskStatus.Running && active.DueAtUtc == deadline && app.Utilities.NotesEditor.Text == notes && Placement.Rectangle(app.Dot) == dotBounds,
                    "Text and bitmap drops across all modes leave system clipboard writes, task count, notes and the running countdown untouched");
            }
            finally { app.ClipboardWriterForTest = previousWriter; }
        }

        private static void ShowOrbDropTestMode(AppController app, string mode)
        {
            app.HideTools();
            if (mode == "launcher") app.ShowLauncher();
            else if (mode != "bare") app.ShowTool(mode, false);
            Pump();
        }

        private static bool LockedPromptVisible(AppController app)
        {
            var prompt = ById<FrameworkElement>(app.Utilities, "LockedReplacementPrompt");
            return prompt != null && prompt.Visibility == Visibility.Visible;
        }

        private static void SetLockedConfirmation(AppController app, bool enabled)
        {
            var box = ById<CheckBox>(app.Utilities, "ConfirmLockedReplacement");
            if (box == null) throw new InvalidOperationException("Expected locked-replacement preference was not rendered.");
            box.IsChecked = enabled;
            box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
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
        private static void ClickMenu(MenuItem item) { if (item == null) throw new InvalidOperationException("Expected task context-menu action was not present."); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        private static MenuItem TaskMenuItem(Button button, string taskId, string header)
        {
            if (button == null) throw new InvalidOperationException("Expected task row action was not rendered.");
            DependencyObject node = button;
            while (node != null)
            {
                var row = node as Grid;
                if (row != null && Object.Equals(row.Tag, taskId))
                    return row.ContextMenu == null ? null : row.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => Object.Equals(item.Header, header));
                node = VisualTreeHelper.GetParent(node);
            }
            throw new InvalidOperationException("Expected task row was not found for " + taskId);
        }
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
