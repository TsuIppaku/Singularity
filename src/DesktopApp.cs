using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Orbit
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool smoke = args.Length == 2 && args[0] == "--smoke-test";
            bool ownsMutex;
            using (var mutex = new Mutex(true, smoke ? "Local\\Orbit.Smoke." + Guid.NewGuid() : "Local\\Singularity.Orbit.v1", out ownsMutex))
            {
                if (!ownsMutex)
                {
                    try { using (var signal = EventWaitHandle.OpenExisting("Local\\Singularity.Orbit.Show")) signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    string data = smoke ? Path.Combine(Path.GetFullPath(args[1]), "state") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Singularity", "Orbit");
                    var controller = new AppController(data, smoke);
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        WriteError(data, e.Exception);
                        if (smoke) { Environment.ExitCode = 1; app.Shutdown(1); }
                        else MessageBox.Show("Orbit 遇到了一个问题，详情已保存在本机 error.log。\n" + e.Exception.Message, "Orbit");
                        e.Handled = true;
                    };
                    controller.Start();
                    if (smoke) app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { SmokeTests.Run(controller, args[1]); }));
                    return app.Run();
                }
                catch (Exception ex)
                {
                    string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Singularity", "Orbit");
                    WriteError(data, ex);
                    if (!smoke) MessageBox.Show("Orbit 暂时无法启动：\n" + ex.Message, "Orbit");
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static void WriteError(string directory, Exception error)
        {
            try { Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "error.log"), DateTimeOffset.Now + "\r\n" + error + "\r\n"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class AppController
    {
        private const int PreviewGracePixels = 14;
        internal readonly TimerEngine Engine;
        internal readonly ClipboardHistory ClipboardHistory;
        internal readonly DotWindow Dot;
        internal readonly TaskPanel Panel;
        internal readonly LauncherWindow Launcher;
        internal readonly UtilityPanel Utilities;
        internal readonly ReminderWindow Reminder;
        internal Func<DateTimeOffset> Now = delegate { return DateTimeOffset.UtcNow; };
        internal Func<ClipboardEntry, bool> ClipboardWriterForTest;
        internal Func<DependencyObject, IDataObject, DragDropEffects, DragDropEffects> ClipboardDragRunnerForTest;
        internal bool Alerting { get; private set; }
        internal int ChimeRequests { get; private set; }
        internal bool IsExiting { get; private set; }
        internal bool IsTest { get; private set; }
        private readonly StateStore store;
        private readonly AlertSound sound;
        private readonly StartupRegistration startup;
        private ClipboardMonitor clipboardMonitor;
        private readonly ClipboardDragExport clipboardDragExport;
        private readonly DispatcherTimer ticker;
        private readonly DispatcherTimer chimeTimer;
        private readonly DispatcherTimer savePositionTimer;
        private readonly DispatcherTimer previewDismissTimer;
        private GlobalMouseClickMonitor mouseMonitor;
        private Forms.NotifyIcon tray;
        private Drawing.Icon trayIcon;
        private EventWaitHandle showSignal;
        private RegisteredWaitHandle signalRegistration;
        private string lastAlertId;
        private bool muted;
        private bool positioning;
        private bool placementDirty;
        private bool switchingTools;
        private bool launcherPreview;
        private bool toolsPinned;
        private bool previewHiding;
        private int previewFadeGeneration;
        private DispatcherOperation pendingPlacement;
        private PopupSide? panelSide;
        private Drawing.Rectangle? lastPanelAnchor;
        private Drawing.Rectangle? lastPanelWorkArea;
        private readonly Dictionary<Window, int> showVersions = new Dictionary<Window, int>();

        internal AppController(string directory, bool test)
        {
            IsTest = test;
            store = new StateStore(directory);
            Engine = new TimerEngine(store.Load());
            ClipboardHistory = new ClipboardHistory(directory);
            clipboardDragExport = new ClipboardDragExport(directory);
            sound = new AlertSound();
            startup = new StartupRegistration();
            Dot = new DotWindow(this);
            Panel = new TaskPanel(this);
            Launcher = new LauncherWindow(this);
            Utilities = new UtilityPanel(this, directory);
            Panel.IsVisibleChanged += delegate { ToolVisibilityChanged(); };
            Utilities.IsVisibleChanged += delegate { ToolVisibilityChanged(); };
            Reminder = new ReminderWindow(this);
            ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            ticker.Tick += delegate { Pulse(); };
            chimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            chimeTimer.Tick += delegate { if (Engine.AlertTask != null) Chime(); };
            savePositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            savePositionTimer.Tick += delegate { savePositionTimer.Stop(); SavePosition(); };
            previewDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            previewDismissTimer.Tick += delegate { previewDismissTimer.Stop(); DismissPreviewIfAway(IsPointerInPreviewRegion()); };
            Dot.MouseEnter += delegate
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed) PreviewLauncher();
            };
            Dot.MouseLeave += delegate { SchedulePreviewDismissal(); };
            Launcher.MouseEnter += delegate { KeepPreviewOpen(); };
            Launcher.MouseLeave += delegate { SchedulePreviewDismissal(); };
            Panel.Deactivated += delegate { RequestOutsideDismissal(); };
            Utilities.Deactivated += delegate { RequestOutsideDismissal(); };
        }

        internal void Start()
        {
            Dot.Show();
            Placement.RestoreDot(Dot, Engine.State.DotLeft, Engine.State.DotTop);
            Engine.Tick(Now());
            Refresh(true);
            if (!String.IsNullOrEmpty(store.LastError)) Panel.ShowError(store.LastError);
            else if (Engine.State.WasRepaired) Panel.ShowError("已恢复保存数据中的异常字段，相关计时已暂停，请检查后继续。");
            if (!IsTest)
            {
                ConfigureStartup();
                clipboardMonitor = new ClipboardMonitor(Dot, CaptureClipboardEntry,
                    delegate(string message) { Utilities.SetClipboardStatus(message, !String.IsNullOrEmpty(message)); });
                clipboardMonitor.Start();
                ticker.Start();
                CreateTray();
                string mouseError;
                mouseMonitor = new GlobalMouseClickMonitor();
                mouseMonitor.Pressed += OnGlobalMousePressed;
                if (!mouseMonitor.TryStart(out mouseError))
                {
                    mouseMonitor.Dispose();
                    mouseMonitor = null;
                    Panel.ShowError("无法监听窗口外点击：" + mouseError);
                }
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Singularity.Orbit.Show");
                signalRegistration = ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { Dot.Dispatcher.BeginInvoke(new Action(ShowPanel)); }, null, Timeout.Infinite, false);
            }
        }

        internal void CaptureClipboardEntry(ClipboardEntry entry)
        {
            if (IsExiting || entry == null) return;
            bool saved = ClipboardHistory.Add(entry);
            bool warning = !saved || !String.IsNullOrEmpty(ClipboardHistory.LastError);
            Utilities.SetClipboardStatus(warning ? ClipboardHistory.LastError : entry.IsImage ? "已自动记录图片" : "已自动记录文字", warning);
        }

        internal bool CopyClipboardEntry(string id)
        {
            var entry = ClipboardHistory.Entries.FirstOrDefault(item => item.Id == id);
            bool isLocked = false;
            if (entry == null)
            {
                entry = ClipboardHistory.LockedEntry;
                if (entry == null || entry.Id != id) return false;
                isLocked = true;
            }
            bool copied;
            try
            {
                var output = entry;
                if (entry.IsFile)
                {
                    // Explorer can paste this file even after the pin is replaced.
                    output = new ClipboardEntry
                    {
                        Id = entry.Id, FileName = entry.FileName, CapturedAtUtc = entry.CapturedAtUtc,
                        FilePath = clipboardDragExport.Prepare(entry).FilePath
                    };
                }
                copied = IsTest ? ClipboardWriterForTest != null && ClipboardWriterForTest(output) :
                    clipboardMonitor != null && clipboardMonitor.Copy(output);
            }
            catch (Exception error)
            {
                if (!IsClipboardTransferError(error)) throw;
                Utilities.SetClipboardStatus("复制未完成：" + error.Message, true);
                return false;
            }
            if (!copied) return false;
            bool saved = isLocked || ClipboardHistory.Promote(id);
            string success = entry.IsFile ? "已复制 · 到文件夹按 Ctrl+V 粘贴" : "已复制 · 切回应用按 Ctrl+V 粘贴";
            Utilities.SetClipboardStatus(saved ? success : "已复制，但历史排序保存失败。", !saved);
            return true;
        }

        internal DragDropEffects DragClipboardEntry(DependencyObject source, ClipboardEntry snapshot)
        {
            if (IsExiting || source == null || snapshot == null) return DragDropEffects.None;
            try
            {
                var payload = clipboardDragExport.Prepare(snapshot);
                Utilities.SetClipboardStatus("拖到文件夹或支持拖放的目标，松开鼠标", false);
                DragDropEffects result = IsTest
                    ? ClipboardDragRunnerForTest == null ? DragDropEffects.None : ClipboardDragRunnerForTest(source, payload.Data, DragDropEffects.Copy)
                    : DragDrop.DoDragDrop(source, payload.Data, DragDropEffects.Copy);
                // The export outlives the OLE call so a browser can read it later.
                // Dragging neither rewrites the clipboard nor reorders its history.
                Utilities.SetClipboardStatus((result & DragDropEffects.Copy) != 0 ? "已拖出" : "已取消或目标未接收", false);
                return result;
            }
            catch (Exception error)
            {
                if (!IsClipboardTransferError(error)) throw;
                Utilities.SetClipboardStatus("拖出未完成：" + error.Message, true);
                return DragDropEffects.None;
            }
        }

        private static bool IsClipboardTransferError(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException ||
                error is InvalidOperationException || error is ExternalException || error is NotSupportedException ||
                error is FormatException || error is System.Runtime.Serialization.SerializationException ||
                error is System.Security.SecurityException;
        }

        internal void Pulse()
        {
            bool changed = Engine.Tick(Now());
            if (changed) Save();
            Refresh(changed);
        }

        internal void Refresh(bool rebuild)
        {
            Dot.Refresh();
            Panel.Refresh(rebuild);
            var alert = Engine.AlertTask;
            string alertId = alert == null ? null : alert.Id;
            if (lastAlertId != alertId)
            {
                lastAlertId = alertId;
                Alerting = alert != null;
                if (alert != null)
                {
                    Reminder.SetTask(alert.Name);
                    Chime();
                    if (!IsTest) chimeTimer.Start();
                }
                else { chimeTimer.Stop(); sound.Stop(); Reminder.Hide(); }
            }
            SyncReminder();
            if (tray != null)
            {
                var task = Engine.ActiveTask;
                string caption = alert != null ? "Orbit · 时间到了" : task == null ? "Orbit · 点击开始一件小事" : "Orbit · " + task.Name + " · " + Display.Time(task.RemainingSeconds);
                tray.Text = caption.Length > 63 ? caption.Substring(0, 63) : caption;
            }
        }

        private void Chime()
        {
            ChimeRequests++;
            if (!muted && !IsTest) sound.Play();
        }

        internal void ClickDot()
        {
            if (Engine.AlertTask != null) { Acknowledge(); return; }
            if (!Launcher.IsVisible) { ShowLauncher(); return; }
            // A hover reveal becomes intentional only when it is clicked. The
            // first click pins it; later clicks use the familiar collapse action.
            if (launcherPreview && !Panel.IsVisible && !Utilities.IsVisible) { PinTools(); return; }
            HideTools();
        }

        internal void ShowPanel()
        {
            ShowTool("timer", false);
        }

        internal void ShowLauncher()
        {
            if (IsExiting) return;
            PinTools();
            if (Launcher.IsVisible) { RepositionTools(); return; }
            PresentTool(Launcher, null);
        }

        internal void PreviewLauncher()
        {
            if (IsExiting || Dot.IsReceivingFileDrop || Panel.IsVisible || Utilities.IsVisible) return;
            if (Launcher.IsVisible) { KeepPreviewOpen(); return; }
            toolsPinned = false;
            launcherPreview = true;
            previewDismissTimer.Stop();
            PresentTool(Launcher, null);
        }

        private void PinTools()
        {
            launcherPreview = false;
            toolsPinned = true;
            previewDismissTimer.Stop();
            CancelPreviewFade();
        }

        private void KeepPreviewOpen()
        {
            if (!launcherPreview) return;
            previewDismissTimer.Stop();
            CancelPreviewFade();
        }

        private void SchedulePreviewDismissal()
        {
            if (!launcherPreview || toolsPinned || Panel.IsVisible || Utilities.IsVisible) return;
            previewDismissTimer.Stop();
            previewDismissTimer.Start();
        }

        private bool IsPointerInPreviewRegion()
        {
            if (!Dot.IsVisible || !Launcher.IsVisible) return false;
            Point pointer = NativeDesktop.CursorPosition;
            // Keep two compact halos rather than a large rectangular bridge.
            return PointerIsWithin(Dot, pointer, PreviewGracePixels) ||
                PointerIsWithin(Launcher, pointer, PreviewGracePixels);
        }

        private static bool PointerIsWithin(Window window, Point pointer, int padding)
        {
            if (window == null || !window.IsVisible) return false;
            var bounds = Placement.Bounds(window);
            return pointer.X >= bounds.Left - padding && pointer.X < bounds.Right + padding &&
                pointer.Y >= bounds.Top - padding && pointer.Y < bounds.Bottom + padding;
        }

        private void DismissPreviewIfAway(bool pointerInPreviewRegion)
        {
            if (launcherPreview && !toolsPinned && !Panel.IsVisible && !Utilities.IsVisible && !pointerInPreviewRegion) BeginPreviewFade();
        }

        private void BeginPreviewFade()
        {
            if (previewHiding || !Launcher.IsVisible) return;
            previewHiding = true;
            int generation = ++previewFadeGeneration;
            var fade = new DoubleAnimation(Launcher.Opacity, 0, TimeSpan.FromMilliseconds(125))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fade.Completed += delegate
            {
                if (previewFadeGeneration != generation || !previewHiding) return;
                previewHiding = false;
                if (launcherPreview && !toolsPinned && !IsPointerInPreviewRegion()) HideTools();
            };
            Launcher.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }

        private void CancelPreviewFade()
        {
            previewHiding = false;
            previewFadeGeneration++;
            if (!Launcher.IsVisible) return;
            Launcher.BeginAnimation(UIElement.OpacityProperty, null);
            Launcher.Opacity = 1;
        }

        private void RequestOutsideDismissal()
        {
            if (IsTest || IsExiting || !toolsPinned || switchingTools) return;
            Dot.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
            {
                if (IsExiting || !toolsPinned || switchingTools || !Launcher.IsVisible) return;
                Point pointer = NativeDesktop.CursorPosition;
                HideToolsIfOutside(pointer);
            }));
        }

        private void OnGlobalMousePressed(Point pointer)
        {
            if (IsTest || IsExiting || !Launcher.IsVisible || Panel.IsOverTaskMenu(pointer)) return;
            Dot.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { HideToolsIfOutside(pointer); }));
        }

        private void HideToolsIfOutside(Point pointer)
        {
            if (IsExiting || !Launcher.IsVisible || Panel.IsOverTaskMenu(pointer)) return;
            if (!PointerIsWithin(Dot, pointer, 0) && !PointerIsWithin(Launcher, pointer, 0) &&
                !PointerIsWithin(Panel, pointer, 0) && !PointerIsWithin(Utilities, pointer, 0) && !PointerIsWithin(Reminder, pointer, 0))
                HideTools();
        }

        // Keeps the interaction state directly testable without reading the real cursor.
        internal void DismissPreviewForTest(bool pointerInPreviewRegion)
        {
            if (launcherPreview && !toolsPinned && !pointerInPreviewRegion) HideTools();
        }
        internal void ClickOutsideForTest(Point pointer) { HideToolsIfOutside(pointer); }
        internal bool LauncherIsPreview { get { return launcherPreview; } }
        internal bool ToolsArePinned { get { return toolsPinned; } }

        internal void HideTools()
        {
            if (!Utilities.Flush()) { ShowUnsavedNotes(); return; }
            previewDismissTimer.Stop();
            launcherPreview = false;
            toolsPinned = false;
            CancelPreviewFade();
            Panel.Hide(); Utilities.Hide(); Launcher.Hide();
            panelSide = null; lastPanelAnchor = null;
        }

        internal void ShowTool(string mode, bool toggle)
        {
            if (IsExiting) return;
            if (!Utilities.Flush()) { ShowUnsavedNotes(); return; }
            PinTools();
            bool alreadyOpen = (mode == "timer" && Panel.IsVisible) || (Utilities.IsVisible && Utilities.Mode == mode);
            if (alreadyOpen)
            {
                if (toggle)
                {
                    if (mode == "timer") Panel.Hide(); else Utilities.Hide();
                }
                else FocusTool(mode);
                return;
            }
            if (!Launcher.IsVisible) ShowLauncher();
            switchingTools = true;
            try
            {
                Window target = mode == "timer" ? (Window)Panel : Utilities;
                // Configure and size the next panel while it is transparent. Its
                // previous position and provisional first-show bounds never flash.
                target.Opacity = 0;
                if (mode == "timer")
                {
                    Utilities.Hide(); Panel.PrepareHeight();
                }
                else
                {
                    Panel.Hide(); Utilities.OpenMode(mode);
                }
                Launcher.SetSelected(mode);
                Refresh(true);
                PresentTool(target, mode);
            }
            finally { switchingTools = false; }
        }

        private void PresentTool(Window target, string mode)
        {
            int previous;
            showVersions.TryGetValue(target, out previous);
            int version = previous + 1;
            showVersions[target] = version;
            target.Opacity = 0;
            target.ShowActivated = false;
            target.Show();
            target.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                if (IsExiting || !target.IsVisible || showVersions[target] != version) return;
                if (mode != null && Launcher.Selected != mode) return;
                target.UpdateLayout();
                PlaceToolsNow();
                // A move to another DPI can enqueue a fresh WPF layout. Keep the
                // window transparent until that render/layout turn has settled,
                // then reveal the finished window in one step. Per-level fades
                // made the launcher and child panels look like they were flashing.
                target.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                {
                    if (IsExiting || !target.IsVisible || showVersions[target] != version) return;
                    if (mode != null && Launcher.Selected != mode) return;
                    PlaceToolsNow();
                    target.Opacity = 1;
                    if (mode != null) FocusTool(mode);
                }));
            }));
        }

        private void FocusTool(string mode)
        {
            if (mode == "timer" && Panel.IsVisible)
            {
                if (!IsTest) Panel.Activate();
                Panel.FocusName();
            }
            else if (Utilities.IsVisible && Utilities.Mode == mode && !IsTest)
            {
                Utilities.Activate();
                if (mode == "notes") Utilities.NotesEditor.Focus();
            }
        }

        private void ToolVisibilityChanged()
        {
            if (Launcher == null || Utilities == null || Panel == null) return;
            Launcher.SetSelected(Panel.IsVisible ? "timer" : Utilities.IsVisible ? Utilities.Mode : null);
            if (!switchingTools && !Panel.IsVisible && !Utilities.IsVisible) panelSide = null;
            RepositionTools();
            SyncReminder();
        }

        internal void RepositionTools()
        {
            if (IsExiting || Launcher == null || !Launcher.IsVisible) return;
            if (positioning) { placementDirty = true; return; }
            if (pendingPlacement != null && pendingPlacement.Status == DispatcherOperationStatus.Pending) return;
            pendingPlacement = Dot.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(delegate
            {
                pendingPlacement = null;
                PlaceToolsNow();
            }));
        }

        private void PlaceToolsNow()
        {
            if (positioning || IsExiting || Launcher == null || !Launcher.IsVisible) return;
            // Native placement must wait for the first WPF layout to complete.
            if (!Dot.IsLoaded || !Launcher.IsLoaded) return;
            Window activePanel = Panel.IsVisible ? (Window)Panel : Utilities != null && Utilities.IsVisible ? Utilities : null;
            positioning = true;
            try
            {
                placementDirty = false;
                Drawing.Rectangle dot = Placement.Rectangle(Dot);
                Drawing.Rectangle work = Placement.WorkArea(Dot);
                Drawing.Rectangle bar = PopupLayout.Toolbar(dot, work, Placement.Rectangle(Launcher).Size);
                Placement.Move(Launcher, bar.Left, bar.Top);
                // Moving across monitors can change the native size immediately.
                bar = Placement.Rectangle(Launcher);
                // The launcher is positioned without any knowledge of its child.
                if (activePanel == null || !activePanel.IsLoaded) return;
                double scale = VisualTreeHelper.GetDpi(Dot).DpiScaleY;
                bool moved = !lastPanelAnchor.HasValue || lastPanelAnchor.Value != dot || !lastPanelWorkArea.HasValue || lastPanelWorkArea.Value != work;
                int minimum = (int)Math.Ceiling((activePanel == Panel ? 200 : 160) * scale);
                var region = PopupLayout.ChooseRegion(dot, bar, work, minimum, panelSide, moved, (int)Math.Ceiling(24 * scale));
                panelSide = region.Side;
                double cap = Math.Max(1, region.AvailableHeight / scale);
                if (activePanel == Panel) Panel.PrepareHeight(cap); else Utilities.PrepareHeight(cap);
                // Read the final size only after the constraint has been applied.
                activePanel.UpdateLayout();
                Drawing.Rectangle bounds = Placement.Rectangle(activePanel);
                Drawing.Rectangle card = PopupLayout.Panel(bar, work, region, bounds.Size);
                Placement.Move(activePanel, card.Left, card.Top);
                lastPanelAnchor = dot; lastPanelWorkArea = work;
            }
            finally
            {
                positioning = false;
                // Do not lose a size/DPI change raised during positioning. One
                // coalesced pass lets WPF process it without recursive layout.
                if (placementDirty) RepositionTools();
            }
        }

        private void SyncReminder()
        {
            if (Reminder == null || IsExiting) return;
            if (Engine.AlertTask != null && !Panel.IsVisible)
            {
                Reminder.SetTask(Engine.AlertTask.Name);
                if (!Reminder.IsVisible) Reminder.Show();
                Placement.Beside(Dot, Reminder);
            }
            else if (Reminder.IsVisible) Reminder.Hide();
        }

        private void ShowUnsavedNotes()
        {
            if (!Launcher.IsVisible) ShowLauncher();
            switchingTools = true;
            try
            {
                Panel.Hide(); Utilities.Opacity = 0; Utilities.OpenMode("notes");
                Launcher.SetSelected("notes"); PresentTool(Utilities, "notes");
            }
            finally { switchingTools = false; }
        }

        internal void PanelHidden()
        {
            if (!IsExiting && Engine.AlertTask != null) { Reminder.Show(); Placement.Beside(Dot, Reminder); }
            ToolVisibilityChanged();
        }

        internal void Submit(bool start)
        {
            string name = Panel.TaskNameBox.Text.Trim();
            double seconds = 0;
            string error;
            if (!String.IsNullOrWhiteSpace(Panel.DurationBox.Text) && !TimerEngine.TryParseDuration(Panel.DurationBox.Text, out seconds, out error)) { Panel.ShowError(error); Panel.DurationBox.Focus(); return; }
            bool success = Act(delegate { Engine.AddTask(name, seconds, false, Now()); Panel.TaskNameBox.Clear(); Panel.DurationBox.Clear(); Panel.ClearError(); });
            if (!success) return;
            Panel.FocusName();
        }

        internal void StartTask(string id)
        {
            Act(delegate
            {
                var current = Engine.ActiveTask;
                if (current != null && current.Id == id && current.Status == OrbitTaskStatus.Running) Engine.Pause(Now());
                else Engine.Start(id, Now());
            });
        }

        internal void RestartTask(string id)
        {
            Act(delegate { Engine.Restart(id, Now()); });
        }

        internal bool EditTask(string id, string name, double durationSeconds)
        {
            return Act(delegate { Engine.EditTask(id, name, durationSeconds, Now()); });
        }

        internal bool Act(Action action)
        {
            string error = null;
            try { action(); }
            catch (ArgumentException ex) { error = ex.Message; }
            catch (InvalidOperationException ex) { error = ex.Message; }
            // An operation can observe an expired deadline before refusing a new task.
            // Persist and render that completion even when the requested operation fails.
            bool saved = Save();
            Refresh(true);
            if (error != null) Panel.ShowError(error);
            return error == null && saved;
        }

        internal void TogglePause()
        {
            Act(delegate
            {
                var task = Engine.ActiveTask;
                if (task == null) return;
                if (task.Status == OrbitTaskStatus.Running) Engine.Pause(Now()); else Engine.Resume(Now());
            });
        }

        internal void Acknowledge() { Act(delegate { Engine.Acknowledge(); }); }

        internal void Moved()
        {
            RepositionTools();
            if (Reminder.IsVisible) Placement.Beside(Dot, Reminder);
            savePositionTimer.Stop();
            savePositionTimer.Start();
        }

        private void SavePosition()
        {
            var rect = Placement.Bounds(Dot);
            Engine.State.DotLeft = rect.Left;
            Engine.State.DotTop = rect.Top;
            Save();
        }

        private bool Save()
        {
            if (store.Save(Engine.State)) return true;
            Panel.ShowError("保存失败：" + store.LastError);
            return false;
        }

        internal void ResetPosition() { Placement.RestoreDot(Dot, null, null); Moved(); }

        private void ConfigureStartup()
        {
            if (!Engine.State.StartWithWindows.HasValue)
            {
                MessageBoxResult answer = MessageBox.Show(
                    "Orbit 会在你登录 Windows 后自动启动，并安静显示桌面小球。\n\n你可以随时从 Orbit 的右键菜单或 Windows 的“启动应用”中关闭它。",
                    "允许 Orbit 开机启动？", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (!SetStartup(answer == MessageBoxResult.Yes, false)) return;
            }
            else if (Engine.State.StartWithWindows.Value)
            {
                string error;
                if (!startup.TrySet(true, ApplicationPath(), out error))
                    Panel.ShowError("开机启动未能恢复：" + error);
            }
        }

        private string ApplicationPath()
        {
            string path = Assembly.GetEntryAssembly() == null ? null : Assembly.GetEntryAssembly().Location;
            if (String.IsNullOrWhiteSpace(path)) path = Assembly.GetExecutingAssembly().Location;
            return path;
        }

        private bool SetStartup(bool enabled, bool reportError)
        {
            string error;
            if (!startup.TrySet(enabled, ApplicationPath(), out error))
            {
                if (reportError) Panel.ShowError("无法更改开机启动：" + error);
                return false;
            }
            bool? previous = Engine.State.StartWithWindows;
            Engine.State.StartWithWindows = enabled;
            if (Save()) return true;
            Engine.State.StartWithWindows = previous;
            // A failed preference save must not leave an unrecorded startup entry.
            string ignored;
            startup.TrySet(previous == true, ApplicationPath(), out ignored);
            return false;
        }

        internal ContextMenu DotMenu()
        {
            var menu = new ContextMenu();
            AddMenu(menu, "打开任务清单", ShowPanel);
            if (Engine.AlertTask != null) AddMenu(menu, "停止提醒", Acknowledge);
            if (Engine.ActiveTask != null)
            {
                AddMenu(menu, Engine.ActiveTask.Status == OrbitTaskStatus.Running ? "暂停计时" : "继续计时", TogglePause);
                AddMenu(menu, "重新计时", delegate { Act(delegate { Engine.Restart(Now()); }); });
                AddMenu(menu, "提前完成", delegate { Act(delegate { Engine.Complete(Now()); }); });
            }
            menu.Items.Add(new Separator());
            var mute = new MenuItem { Header = "静音", IsCheckable = true, IsChecked = muted };
            mute.Click += delegate { muted = mute.IsChecked; if (muted) sound.Stop(); };
            menu.Items.Add(mute);
            var startupItem = new MenuItem { Header = "登录 Windows 时启动 Orbit", IsCheckable = true, IsChecked = Engine.State.StartWithWindows == true };
            startupItem.Click += delegate
            {
                if (!SetStartup(startupItem.IsChecked, true)) startupItem.IsChecked = Engine.State.StartWithWindows == true;
            };
            menu.Items.Add(startupItem);
            AddMenu(menu, "小点回到屏幕右侧", ResetPosition);
            menu.Items.Add(new Separator());
            AddMenu(menu, "退出 Orbit", Exit);
            return menu;
        }

        private static void AddMenu(ContextMenu menu, string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += delegate { action(); };
            menu.Items.Add(item);
        }

        private void CreateTray()
        {
            trayIcon = CreateIcon();
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Orbit · 极简工作助手", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开任务清单", null, delegate { ShowPanel(); });
            menu.Items.Add("小点回到屏幕右侧", null, delegate { ResetPosition(); });
            menu.Items.Add("停止提醒", null, delegate { Acknowledge(); });
            var startupItem = menu.Items.Add("登录 Windows 时启动 Orbit", null, delegate(object sender, EventArgs e)
            {
                var item = sender as Forms.ToolStripMenuItem;
                SetStartup(item != null && item.Checked, true);
            }) as Forms.ToolStripMenuItem;
            startupItem.CheckOnClick = true;
            startupItem.Checked = Engine.State.StartWithWindows == true;
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出 Orbit", null, delegate { Exit(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) ShowPanel(); };
        }

        private static Drawing.Icon CreateIcon()
        {
            using (var bitmap = new Drawing.Bitmap(32, 32))
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            using (var ink = new Drawing.SolidBrush(Drawing.Color.FromArgb(37, 38, 43)))
            using (var red = new Drawing.SolidBrush(Drawing.Color.FromArgb(217, 108, 98)))
            using (var ring = new Drawing.Pen(Drawing.Color.FromArgb(240, 237, 231), 1.5f))
            {
                graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillEllipse(ink, 1, 1, 30, 30); graphics.DrawEllipse(ring, 7, 7, 18, 18); graphics.FillEllipse(red, 12, 12, 8, 8);
                IntPtr native = bitmap.GetHicon();
                try { return (Drawing.Icon)Drawing.Icon.FromHandle(native).Clone(); }
                finally { Placement.DestroyIcon(native); }
            }
        }

        internal void Exit()
        {
            if (IsExiting) return;
            if (!Utilities.Flush()) { ShowUnsavedNotes(); return; }
            IsExiting = true;
            ticker.Stop(); chimeTimer.Stop(); savePositionTimer.Stop();
            if (clipboardMonitor != null) clipboardMonitor.Dispose();
            if (mouseMonitor != null) { mouseMonitor.Dispose(); mouseMonitor = null; }
            Engine.Tick(Now()); SavePosition();
            Utilities.Flush();
            sound.Dispose();
            if (signalRegistration != null) signalRegistration.Unregister(null);
            if (showSignal != null) showSignal.Dispose();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (trayIcon != null) trayIcon.Dispose();
            Panel.Close(); Utilities.Close(); Launcher.Close(); Reminder.Close(); Dot.Close();
            Application.Current.Shutdown(Environment.ExitCode);
        }
    }

    internal sealed class DotWindow : Window
    {
        private readonly AppController app;
        private readonly DotVisual visual;
        private readonly ToolTip tip;
        private readonly DispatcherTimer fileDropFeedbackTimer;
        private Point pointerStart;
        private Placement.NativeRect dragBounds;
        private bool pressed;
        private bool dragged;

        internal bool IsReceivingFileDrop { get; private set; }

        internal DotWindow(AppController app)
        {
            this.app = app;
            Title = "Orbit · 悬浮小点"; Width = 88; Height = 88;
            Display.ConfigureWindow(this);
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            // A layered window passes input through pixels with zero alpha.
            // Keep its small fixed area hittable for native drags in every mode.
            Background = Display.Brush("#02000000"); ShowInTaskbar = false; ShowActivated = false; Topmost = true;
            visual = new DotVisual(app) { AllowDrop = true }; Content = visual; Cursor = Cursors.Hand;
            AllowDrop = true;
            Motion.WireHover(visual); Motion.WirePress(visual);
            tip = new ToolTip { Content = "Orbit · 点击开始，拖动移动，右键菜单" }; ToolTip = tip;
            ToolTipService.SetInitialShowDelay(this, 450);
            fileDropFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            fileDropFeedbackTimer.Tick += delegate { fileDropFeedbackTimer.Stop(); visual.SetFileDropState(false, null, false); };
            PreviewDragEnter += OnFileDragOver;
            PreviewDragOver += OnFileDragOver;
            PreviewDragLeave += delegate(object sender, DragEventArgs e) { if (IsReceivingFileDrop) CancelFileDropPreview(); e.Handled = true; };
            PreviewDrop += OnFileDrop;
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape && IsReceivingFileDrop) { CancelFileDropPreview(); e.Handled = true; }
            };
            IsVisibleChanged += delegate { if (!IsVisible) CancelFileDropPreview(); };
            SourceInitialized += delegate { NativeDesktop.PreventActivation(this); };
            MouseLeftButtonDown += OnDown; MouseMove += OnMove; MouseLeftButtonUp += OnUp;
            LostMouseCapture += delegate { pressed = false; };
            MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) { ContextMenu = app.DotMenu(); ContextMenu.IsOpen = true; e.Handled = true; };
            LocationChanged += delegate { if (IsLoaded) app.Moved(); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!app.IsExiting) { e.Cancel = true; Dispatcher.BeginInvoke(new Action(app.Exit)); } };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Closed += delegate { CancelFileDropPreview(); Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; };
        }

        private void OnFileDragOver(object sender, DragEventArgs e)
        {
            e.Effects = PreviewFileDrop(e.Data, e.AllowedEffects) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        internal bool PreviewFileDrop(IDataObject data, DragDropEffects allowedEffects)
        {
            // The guard also covers rejected and Orbit-originated OLE drags, so
            // none of them can trigger the normal mouse-hover launcher preview.
            IsReceivingFileDrop = true;
            pressed = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            tip.IsOpen = false;
            ToolTipService.SetIsEnabled(this, false);
            fileDropFeedbackTimer.Stop();
            bool ownDrag = app.Utilities != null && app.Utilities.ClipboardGestureDragging;
            bool accepted = !ownDrag && (allowedEffects & DragDropEffects.Copy) != 0 && ClipboardMonitor.CanReadDroppedContent(data);
            visual.SetFileDropState(accepted, null, false);
            return accepted;
        }

        internal void CancelFileDropPreview()
        {
            IsReceivingFileDrop = false;
            fileDropFeedbackTimer.Stop();
            ToolTipService.SetIsEnabled(this, true);
            visual.SetFileDropState(false, null, false);
        }

        private void OnFileDrop(object sender, DragEventArgs e)
        {
            e.Effects = ReceiveFileDrop(e.Data, e.AllowedEffects) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        internal bool ReceiveFileDrop(IDataObject data, DragDropEffects allowedEffects)
        {
            bool ownDrag = app.Utilities != null && app.Utilities.ClipboardGestureDragging;
            CancelFileDropPreview();
            if (ownDrag) return false;
            bool accepted = (allowedEffects & DragDropEffects.Copy) != 0 && app.Utilities.TryLockDroppedContent(data);
            string message = accepted ? (app.Utilities.ClipboardLockConfirmationPending ? "待确认" : "已锁定") : "未能锁定";
            visual.SetFileDropState(false, message, !accepted);
            fileDropFeedbackTimer.Start();
            return accepted;
        }

        private void OnDisplayChanged(object sender, EventArgs args)
        {
            Dispatcher.BeginInvoke(new Action(delegate { if (!app.IsExiting) { Placement.Clamp(this); app.Moved(); } }));
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (IsReceivingFileDrop) { e.Handled = true; return; }
            tip.IsOpen = false;
            pressed = true; dragged = false; pointerStart = NativeDesktop.CursorPosition; dragBounds = Placement.Bounds(this);
            CaptureMouse(); e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            Point pointer = NativeDesktop.CursorPosition;
            double dx = pointer.X - pointerStart.X, dy = pointer.Y - pointerStart.Y;
            if (Math.Abs(dx) > 5 || Math.Abs(dy) > 5) dragged = true;
            if (dragged) Placement.Move(this, dragBounds.Left + (int)dx, dragBounds.Top + (int)dy);
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            bool click = !dragged; pressed = false; ReleaseMouseCapture();
            Placement.Clamp(this);
            if (click) app.ClickDot(); else app.Moved();
            e.Handled = true;
        }

        internal void Refresh()
        {
            visual.Refresh();
            var task = app.Engine.ActiveTask;
            var alert = app.Engine.AlertTask;
            tip.Content = alert != null ? "时间到了 · " + alert.Name + "\n点击小点停止提醒" : task != null ? task.Name + "\n" + (task.Status == OrbitTaskStatus.Paused ? "已暂停 · " : "剩余 ") + Display.Time(task.RemainingSeconds) : "Orbit · 点击打开工具\n拖动移动 · 右键菜单";
        }
    }

    internal sealed class DotVisual : FrameworkElement
    {
        private static readonly DependencyProperty FileDropMorphProperty = DependencyProperty.Register(
            "FileDropMorph", typeof(double), typeof(DotVisual), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        private readonly AppController app;
        private readonly VisualCollection children;
        private readonly TextBlock countdown;
        private readonly TextBlock fileDropLabel;
        private bool fileDropReady;
        private string fileDropFeedback;
        private bool fileDropError;
        internal DotVisual(AppController app)
        {
            this.app = app; Width = 88; Height = 88; UseLayoutRounding = true; SnapsToDevicePixels = true;
            countdown = new TextBlock { FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Medium, FontSize = 14, Foreground = Display.Brush("#DFF8F4"), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, SnapsToDevicePixels = true, UseLayoutRounding = true };
            TextOptions.SetTextFormattingMode(countdown, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(countdown, TextRenderingMode.Grayscale);
            System.Windows.Documents.Typography.SetNumeralAlignment(countdown, FontNumeralAlignment.Tabular);
            fileDropLabel = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontWeight = FontWeights.Medium, FontSize = 11,
                Foreground = Display.Brush("#F0D49A"), TextAlignment = TextAlignment.Center, LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Visibility = Visibility.Collapsed,
                IsHitTestVisible = false, SnapsToDevicePixels = true, UseLayoutRounding = true
            };
            TextOptions.SetTextFormattingMode(fileDropLabel, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(fileDropLabel, TextRenderingMode.Grayscale);
            children = new VisualCollection(this); children.Add(countdown); children.Add(fileDropLabel);
        }
        protected override int VisualChildrenCount { get { return children.Count; } }
        protected override Visual GetVisualChild(int index) { return children[index]; }
        protected override Size MeasureOverride(Size available) { countdown.Measure(new Size(68, 28)); fileDropLabel.Measure(new Size(66, 34)); return new Size(88, 88); }
        protected override Size ArrangeOverride(Size finalSize) { countdown.Arrange(new Rect(10, 30, 68, 28)); fileDropLabel.Arrange(new Rect(11, 28, 66, 34)); return finalSize; }
        internal void SetFileDropState(bool ready, string feedback, bool error)
        {
            if (fileDropReady == ready && fileDropFeedback == feedback && fileDropError == error) return;
            fileDropReady = ready;
            fileDropFeedback = feedback;
            fileDropError = error;
            bool showing = ready || feedback != null;
            fileDropLabel.Text = ready ? "松开放入\n锁定槽" : feedback == null ? String.Empty : feedback + (error ? "\n请重试" : "\n锁定槽");
            fileDropLabel.Foreground = Display.Brush(error ? "#E9B4A8" : "#F0D49A");
            fileDropLabel.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
            countdown.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            BeginAnimation(FileDropMorphProperty, new DoubleAnimation(showing ? 1 : 0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            InvalidateVisual();
        }
        internal void Refresh()
        {
            var task = app.Engine.ActiveTask;
            countdown.Text = task == null ? "" : Display.Time(task.RemainingSeconds);
            countdown.FontSize = task != null && task.RemainingSeconds >= 3600 ? 12 : 14;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var center = new Point(44, 44);
            double morph = Math.Max(0, Math.Min(1, (double)GetValue(FileDropMorphProperty)));
            dc.DrawRectangle(Display.Brush("#02000000"), null, new Rect(0, 0, ActualWidth, ActualHeight));
            dc.DrawRoundedRectangle(Display.Brush("#13141A"), null, new Rect(9, 11, 70, 70), 35 - 18 * morph, 35 - 18 * morph);
            var shell = new LinearGradientBrush(Blend("#34353F", "#3A3328", morph), Blend("#202128", "#24211D", morph), new Point(0, 0), new Point(1, 1));
            var edge = new SolidColorBrush(Blend("#51505F", fileDropError ? "#BC897A" : "#CDA65D", morph));
            dc.DrawRoundedRectangle(shell, new Pen(edge, 1 + .35 * morph), new Rect(10, 10, 68, 68), 34 - 18 * morph, 34 - 18 * morph);
            if (fileDropReady || fileDropFeedback != null) return;
            var task = app.Engine.ActiveTask;
            if (app.Engine.AlertTask != null)
            {
                dc.DrawEllipse(null, new Pen(Display.Brush("#9DC9AE"), 2), center, 29, 29);
                var check = new Pen(Display.Brush("#BDE2CA"), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                var mark = new StreamGeometry();
                using (var g = mark.Open()) { g.BeginFigure(new Point(36, 44), false, false); g.LineTo(new Point(42, 50), true, false); g.LineTo(new Point(53, 38), true, false); }
                dc.DrawGeometry(null, check, mark);
            }
            else if (task != null)
            {
                dc.DrawEllipse(null, new Pen(Display.Brush("#42404D"), 2), center, 29, 29);
                double ratio = Math.Max(0, Math.Min(1, task.RemainingSeconds / task.DurationSeconds));
                DrawArc(dc, center, 29, ratio, task.Status == OrbitTaskStatus.Paused ? "#87838F" : "#8EDBD5");
            }
            else
            {
                dc.DrawEllipse(null, new Pen(Display.Brush("#DDDAE3"), 1.5), center, 15, 15);
                dc.DrawEllipse(Display.Brush("#E78A7A"), null, center, 5, 5);
                dc.DrawEllipse(Display.Brush("#DDDAE3"), null, new Point(56, 35), 2.4, 2.4);
            }
        }

        private static Color Blend(string from, string to, double ratio)
        {
            Color a = (Color)ColorConverter.ConvertFromString(from), b = (Color)ColorConverter.ConvertFromString(to);
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * ratio), (byte)(a.G + (b.G - a.G) * ratio), (byte)(a.B + (b.B - a.B) * ratio));
        }

        private static void DrawArc(DrawingContext dc, Point c, double radius, double ratio, string color)
        {
            if (ratio <= 0) return;
            var pen = new Pen(Display.Brush(color), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (ratio >= .9999) { dc.DrawEllipse(null, pen, c, radius, radius); return; }
            double angle = ratio * Math.PI * 2 - Math.PI / 2;
            var end = new Point(c.X + radius * Math.Cos(angle), c.Y + radius * Math.Sin(angle));
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open()) { ctx.BeginFigure(new Point(c.X, c.Y - radius), false, false); ctx.ArcTo(end, new Size(radius, radius), 0, ratio > .5, SweepDirection.Clockwise, true, false); }
            geometry.Freeze(); dc.DrawGeometry(null, pen, geometry);
        }

    }

    internal sealed class ReminderWindow : Window
    {
        private readonly TextBlock name;
        internal ReminderWindow(AppController app)
        {
            Title = "Orbit · 时间到了"; Width = 256; SizeToContent = SizeToContent.Height;
            Display.ConfigureWindow(this);
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
            var border = new Border { Background = Display.Brush("#1B1C24"), BorderBrush = Display.Brush("#526B5C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(6) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "时间到了", FontSize = 11, Foreground = Display.Brush("#91BC9F") });
            name = new TextBlock { FontSize = 13, Foreground = Display.Brush("#E3E9E4"), TextWrapping = TextWrapping.Wrap, MaxHeight = 80, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 7, 0, 12) }; stack.Children.Add(name);
            var confirm = new Button { Content = "知道了 · 停止提醒", Padding = new Thickness(9, 6, 9, 6), Background = Display.Brush("#35483A"), BorderThickness = new Thickness(0), Foreground = Display.Brush("#C9E7D3"), Cursor = Cursors.Hand };
            Motion.WireHover(confirm); Motion.WirePress(confirm);
            confirm.Click += delegate { app.Acknowledge(); }; stack.Children.Add(confirm);
            border.Child = stack; Content = border;
            SourceInitialized += delegate { NativeDesktop.PreventActivation(this); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!app.IsExiting) e.Cancel = true; };
        }
        internal void SetTask(string task) { name.Text = task; }
    }

    internal static class Display
    {
        internal static void ConfigureWindow(Window window)
        {
            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(window, TextRenderingMode.Grayscale);
        }
        internal static SolidColorBrush Brush(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
        internal static string Time(double seconds)
        {
            long whole = (long)Math.Ceiling(Math.Max(0, seconds));
            return whole >= 3600 ? String.Format("{0}:{1:00}:{2:00}", whole / 3600, whole / 60 % 60, whole % 60) : String.Format("{0:00}:{1:00}", whole / 60, whole % 60);
        }
        internal static string Duration(double seconds) { return seconds >= 60 ? (seconds / 60).ToString("0.#", CultureInfo.InvariantCulture) + " 分钟" : seconds.ToString("0.#", CultureInfo.InvariantCulture) + " 秒"; }
        internal static string ShortName(string name) { var info = new StringInfo(name); return info.LengthInTextElements > 3 ? info.SubstringByTextElements(0, 3) : name; }
    }

    internal static class Placement
    {
        [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { internal int Left, Top, Right, Bottom; internal int Width { get { return Right - Left; } } internal int Height { get { return Bottom - Top; } } }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr handle);
        internal static NativeRect Bounds(Window window) { NativeRect result; GetWindowRect(new WindowInteropHelper(window).Handle, out result); return result; }
        internal static Drawing.Rectangle WorkArea(Window window) { var b = Bounds(window); return Forms.Screen.FromRectangle(new Drawing.Rectangle(b.Left, b.Top, b.Width, b.Height)).WorkingArea; }
        internal static Drawing.Rectangle Rectangle(Window window) { var r = Bounds(window); return new Drawing.Rectangle(r.Left, r.Top, r.Width, r.Height); }
        internal static void Move(Window window, int left, int top)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var current = Bounds(window);
            if (current.Left == left && current.Top == top) return;
            SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, 0x0001 | 0x0004 | 0x0010);
        }
        internal static void RestoreDot(Window dot, double? left, double? top)
        {
            var area = Forms.Screen.PrimaryScreen.WorkingArea; var size = Bounds(dot);
            if (left.HasValue && top.HasValue && !Double.IsNaN(left.Value) && !Double.IsNaN(top.Value) && Math.Abs(left.Value) < 100000 && Math.Abs(top.Value) < 100000) Move(dot, (int)left.Value, (int)top.Value);
            else Move(dot, area.Right - size.Width - 28, area.Top + Math.Max(20, (area.Height - size.Height) / 3));
            Clamp(dot);
        }
        internal static void Clamp(Window window)
        {
            var b = Bounds(window); var area = WorkArea(window);
            Move(window, Math.Max(area.Left, Math.Min(b.Left, area.Right - b.Width)), Math.Max(area.Top, Math.Min(b.Top, area.Bottom - b.Height)));
        }
        internal static void Beside(Window dot, Window panel)
        {
            if (!panel.IsVisible) return;
            var orb = Bounds(dot); var b = Bounds(panel); var area = WorkArea(dot);
            int x = orb.Left - b.Width - 2;
            if (x < area.Left) x = orb.Right + 2;
            x = Math.Max(area.Left, Math.Min(x, area.Right - b.Width));
            int y = Math.Max(area.Top, Math.Min(orb.Top - 8, area.Bottom - b.Height));
            Move(panel, x, y);
        }

    }
}
