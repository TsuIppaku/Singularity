using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace Orbit
{
    internal sealed class TaskPanel : Window
    {
        internal const double PreferredHeight = 336;
        private const string DragFormat = "Singularity.Orbit.TaskId";
        private readonly AppController app;
        private readonly FrameworkElement root;
        private readonly List<RowView> rows = new List<RowView>();
        private bool showCompleted;
        private bool dragging;
        private bool rebuildAfterDrag;
        private string pressedTask;
        private Point dragStart;

        internal TextBox TaskNameBox { get { return Find<TextBox>("TaskNameBox"); } }
        internal TextBox DurationBox { get { return Find<TextBox>("DurationBox"); } }

        internal TaskPanel(AppController app)
        {
            this.app = app;
            Title = "Orbit · 任务计时器";
            Width = 320;
            SizeToContent = SizeToContent.Manual;
            Height = PreferredHeight;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Orbit.Panel.xaml"))
                root = (FrameworkElement)XamlReader.Load(stream);
            Content = root;
            Find<Button>("CloseButton").Content = new VectorIcon("close", 14, "#A69EAE");
            Find<Button>("AddButton").Content = new VectorIcon("enter", 17, "#F5F0FC");
            Find<Button>("AcknowledgeButton").Content = new VectorIcon("check", 16, "#BFDBC9");
            Find<ContentControl>("SortHint").Content = new VectorIcon("grip", 14, "#81758E");
            Bind("CloseButton", Hide);
            Bind("AddButton", delegate { app.Submit(false); });
            Bind("AcknowledgeButton", app.Acknowledge);
            Bind("CompletedToggle", delegate { showCompleted = !showCompleted; Refresh(true); });
            TaskNameBox.KeyDown += OnInputKey;
            DurationBox.KeyDown += OnInputKey;
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!app.IsExiting) { e.Cancel = true; Hide(); }
            };
            SizeChanged += delegate { if (IsVisible) app.RepositionTools(); };
        }

        internal T Find<T>(string name) where T : class { return root.FindName(name) as T; }

        private void Bind(string name, Action action)
        {
            var button = Find<Button>(name);
            button.Click += delegate { action(); };
            Motion.WireHover(button);
            Motion.WirePress(button);
        }

        internal void FocusName()
        {
            FocusManager.SetFocusedElement(this, TaskNameBox);
            if (!app.IsTest && IsVisible) TaskNameBox.Focus();
        }

        private void OnInputKey(object sender, KeyEventArgs e)
        {
            // An IME candidate confirmation is Key.ImeProcessed, not a committed Enter.
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            e.Handled = true;
            if (sender == TaskNameBox)
            {
                FocusManager.SetFocusedElement(this, DurationBox);
                if (!app.IsTest) DurationBox.Focus();
            }
            else app.Submit(false);
        }

        internal void PrepareHeight(double? availableHeight = null)
        {
            var working = Placement.WorkArea(app.Dot);
            var source = PresentationSource.FromVisual(app.Dot);
            double scale = source == null || source.CompositionTarget == null ? 1 : source.CompositionTarget.TransformToDevice.M22;
            // Reserve the 68 DIP launcher and 24 DIP of spacing on this monitor.
            double cap = Math.Min(PreferredHeight, working.Height / scale - 92);
            if (availableHeight.HasValue && !Double.IsNaN(availableHeight.Value)) cap = Math.Min(cap, availableHeight.Value);
            cap = Math.Max(1, cap);
            SizeToContent = SizeToContent.Manual;
            MaxHeight = cap;
            Height = cap;
            // Keep one task row usable in a short, 200-DIP panel. The normal
            // 336-DIP panel retains its original spacing and never grows with rows.
            var border = root as Border;
            if (border != null)
            {
                Thickness padding = border.Padding;
                double vertical = cap < 240 ? 8 : 16;
                border.Padding = new Thickness(padding.Left, vertical, padding.Right, vertical);
            }
            var scroll = Find<ScrollViewer>("RowsScroll");
            scroll.MinHeight = 0;
            scroll.MaxHeight = Double.PositiveInfinity;
        }

        internal void ShowError(string text)
        {
            var label = Find<TextBlock>("ValidationText");
            label.Text = text;
            label.Visibility = Visibility.Visible;
        }

        internal void ClearError() { Find<TextBlock>("ValidationText").Visibility = Visibility.Collapsed; }

        internal void Refresh(bool rebuild)
        {
            var alert = app.Engine.AlertTask;
            Find<Border>("AlertCard").Visibility = alert == null ? Visibility.Collapsed : Visibility.Visible;
            if (alert != null)
            {
                Find<TextBlock>("AlertName").Text = "时间到了 · " + alert.Name;
                Find<TextBlock>("AlertName").ToolTip = alert.Name;
            }
            if (dragging && rebuild) { rebuildAfterDrag = true; rebuild = false; }
            if (!rebuild)
            {
                foreach (var row in rows) UpdateRow(row);
                return;
            }

            var pending = app.Engine.State.Tasks.Where(x => x.Status != OrbitTaskStatus.Completed).ToList();
            var completed = app.Engine.State.Tasks.Where(x => x.Status == OrbitTaskStatus.Completed).OrderByDescending(x => x.CompletedAtUtc).ToList();
            var desired = pending.Concat(showCompleted ? completed : Enumerable.Empty<OrbitTask>()).ToList();
            Find<TextBlock>("PendingHeading").Text = "待办 · " + pending.Count;
            Find<TextBlock>("EmptyPending").Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var toggle = Find<Button>("CompletedToggle");
            toggle.Visibility = completed.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var completedLabel = new StackPanel { Orientation = Orientation.Horizontal };
            completedLabel.Children.Add(new VectorIcon(showCompleted ? "chevronUp" : "chevronDown", 13, "#9F96AB") { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            completedLabel.Children.Add(new TextBlock { Text = "已完成 · " + completed.Count, FontSize = 11, Foreground = Display.Brush("#9F96AB"), VerticalAlignment = VerticalAlignment.Center });
            toggle.Content = completedLabel;
            var history = Find<StackPanel>("CompletedList");
            history.Visibility = showCompleted ? Visibility.Visible : Visibility.Collapsed;
            // Keep existing controls while only the play/pause state changes, so
            // their pressed/hover animation and keyboard focus can finish normally.
            if (rows.Count > 0 && rows.Count == desired.Count && rows.Select(x => x.Task.Id).SequenceEqual(desired.Select(x => x.Id)) && rows.All(x => x.CompletedGroup == (x.Task.Status == OrbitTaskStatus.Completed)))
            {
                foreach (var row in rows) UpdateRow(row);
                return;
            }
            rows.Clear();
            var list = Find<StackPanel>("PendingList");
            list.Children.Clear();
            foreach (var task in pending) list.Children.Add(CreateRow(task, true));
            history.Children.Clear();
            if (showCompleted)
                foreach (var task in completed) history.Children.Add(CreateRow(task, false));
        }

        private FrameworkElement CreateRow(OrbitTask task, bool draggable)
        {
            var row = new RowView { Task = task, CompletedGroup = !draggable };
            row.Outer = new Grid { Margin = new Thickness(0, 2, 0, 2), Tag = task.Id, AllowDrop = draggable, UseLayoutRounding = true, SnapsToDevicePixels = true };
            row.Surface = new Border { MinHeight = 52, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 7, 6, 7), Background = Brushes.Transparent };
            row.Outer.Children.Add(row.Surface);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(task.IsChecklist ? 56 : task.Status != OrbitTaskStatus.Completed ? 96 : 32) });
            row.Surface.Child = grid;

            var grip = new VectorIcon("grip", 12, "#766A85") { VerticalAlignment = VerticalAlignment.Center, Visibility = draggable ? Visibility.Visible : Visibility.Hidden, Cursor = draggable ? Cursors.SizeAll : Cursors.Arrow, ToolTip = draggable ? "拖动排序" : null };
            grid.Children.Add(grip);
            var labelHost = new Grid { Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            row.Name = new TextBlock { Text = task.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13, ToolTip = task.Name };
            row.Time = new TextBlock { FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), Foreground = Display.Brush("#99A1AF"), TextTrimming = TextTrimming.CharacterEllipsis };
            row.Labels.Children.Add(row.Name);
            row.Labels.Children.Add(row.Time);
            labelHost.Children.Add(row.Labels);
            if (!task.IsChecklist && task.Status != OrbitTaskStatus.Completed)
            {
                row.Editor = new Grid { Visibility = Visibility.Collapsed };
                row.Editor.ColumnDefinitions.Add(new ColumnDefinition());
                row.Editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                row.Editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
                row.EditName = new TextBox { Height = 28, MaxLength = 100, VerticalContentAlignment = VerticalAlignment.Center };
                row.EditDuration = new TextBox { Height = 28, MaxLength = 40, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetAutomationId(row.EditName, "task-" + task.Id + "-edit-name");
                System.Windows.Automation.AutomationProperties.SetAutomationId(row.EditDuration, "task-" + task.Id + "-edit-duration");
                row.EditName.KeyDown += delegate(object sender, KeyEventArgs e) { EditKey(row, e); };
                row.EditDuration.KeyDown += delegate(object sender, KeyEventArgs e) { EditKey(row, e); };
                row.Editor.Children.Add(row.EditName);
                Grid.SetColumn(row.EditDuration, 2);
                row.Editor.Children.Add(row.EditDuration);
                labelHost.Children.Add(row.Editor);
            }
            Grid.SetColumn(labelHost, 1);
            grid.Children.Add(labelHost);

            if (task.IsChecklist)
            {
                row.Actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                row.Check = new CheckBox { IsChecked = task.Status == OrbitTaskStatus.Completed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(row.Check, "完成 " + task.Name);
                System.Windows.Automation.AutomationProperties.SetAutomationId(row.Check, "task-" + task.Id + "-check");
                row.Check.Click += delegate { app.Act(delegate { app.Engine.ToggleChecklist(task.Id, app.Now()); }); };
                row.Delete = RowAction("删除任务", "task-" + task.Id + "-delete", new VectorIcon("trash", 15, "#E47B7E"));
                row.Delete.Click += delegate { app.Act(delegate { app.Engine.Delete(task.Id); }); };
                row.Actions.Children.Add(row.Delete);
                row.Actions.Children.Add(row.Check);
                Grid.SetColumn(row.Actions, 2);
                grid.Children.Add(row.Actions);
                Motion.WireHover(row.Delete);
                Motion.WirePress(row.Delete);
                Motion.WireHover(row.Check);
                Motion.WirePress(row.Check);
            }
            else if (task.Status != OrbitTaskStatus.Completed)
            {
                row.Actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                row.Edit = RowAction("编辑任务", "task-" + task.Id + "-edit", new VectorIcon("edit", 14, "#AAA1B4"));
                row.Edit.Click += delegate { ToggleEdit(row); };
                row.Restart = RowAction("重新开始", "task-" + task.Id + "-restart", new VectorIcon("restart", 15, "#AAA1B4"));
                row.Restart.Click += delegate { app.RestartTask(task.Id); };
                row.Delete = RowAction("删除任务", "task-" + task.Id + "-delete", new VectorIcon("trash", 15, "#E47B7E"));
                row.Delete.Click += delegate { app.Act(delegate { app.Engine.Delete(task.Id); }); };
                row.Play = RowAction("开始或暂停 " + task.Name, "task-" + task.Id + "-play", null);
                System.Windows.Automation.AutomationProperties.SetName(row.Play, "开始或暂停 " + task.Name);
                row.Play.Click += delegate { app.StartTask(task.Id); };
                row.Actions.Children.Add(row.Edit);
                row.Actions.Children.Add(row.Restart);
                row.Actions.Children.Add(row.Delete);
                row.Actions.Children.Add(row.Play);
                Grid.SetColumn(row.Actions, 2);
                grid.Children.Add(row.Actions);
                Motion.WireHover(row.Edit); Motion.WirePress(row.Edit);
                Motion.WireHover(row.Restart); Motion.WirePress(row.Restart);
                Motion.WireHover(row.Delete); Motion.WirePress(row.Delete);
                Motion.WireHover(row.Play);
                Motion.WirePress(row.Play);
            }
            else
            {
                row.Delete = RowAction("删除任务", "task-" + task.Id + "-delete", new VectorIcon("trash", 15, "#E47B7E"));
                row.Delete.Click += delegate { app.Act(delegate { app.Engine.Delete(task.Id); }); };
                Grid.SetColumn(row.Delete, 2);
                grid.Children.Add(row.Delete);
                Motion.WireHover(row.Delete);
                Motion.WirePress(row.Delete);
            }

            row.Before = new Border { Height = 2, Background = Display.Brush("#B79ADB"), VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            row.After = new Border { Height = 2, Background = Display.Brush("#B79ADB"), VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            row.Outer.Children.Add(row.Before);
            row.Outer.Children.Add(row.After);
            row.Outer.MouseEnter += delegate { row.Hovered = true; UpdateRow(row); };
            row.Outer.MouseLeave += delegate { row.Hovered = false; UpdateRow(row); };
            row.Outer.ContextMenu = RowMenu(task);
            if (draggable) WireDrag(row);
            rows.Add(row);
            UpdateRow(row);
            return row.Outer;
        }

        private ContextMenu RowMenu(OrbitTask task)
        {
            var menu = new ContextMenu();
            if (!task.IsChecklist && task.Id == app.Engine.State.ActiveTaskId)
            {
                var finish = new MenuItem { Header = "提前完成" };
                finish.Click += delegate { app.Act(delegate { app.Engine.Complete(app.Now()); }); };
                menu.Items.Add(finish);
            }
            var remove = new MenuItem { Header = "删除任务" };
            remove.Click += delegate { app.Act(delegate { app.Engine.Delete(task.Id); }); };
            menu.Items.Add(remove);
            return menu;
        }

        private static Button RowAction(string tooltip, string automationId, object content)
        {
            var button = new Button
            {
                Width = 24, Height = 30, Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip,
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                Content = content
            };
            System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
            return button;
        }

        private void ToggleEdit(RowView row)
        {
            if (row.Editing) { CommitEdit(row); return; }
            foreach (var other in rows.Where(item => item != row && item.Editing)) EndEdit(other);
            row.Editing = true;
            row.EditName.Text = row.Task.Name;
            row.EditDuration.Text = Display.Duration(row.Task.DurationSeconds);
            row.Labels.Visibility = Visibility.Collapsed;
            row.Editor.Visibility = Visibility.Visible;
            ClearError();
            UpdateRow(row);
            row.EditName.Focus();
            row.EditName.SelectAll();
        }

        private void EditKey(RowView row, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { EndEdit(row); e.Handled = true; }
            else if (e.Key == Key.Enter || e.Key == Key.Return) { CommitEdit(row); e.Handled = true; }
        }

        private void CommitEdit(RowView row)
        {
            if (!row.Editing) return;
            double seconds;
            string error;
            if (!TimerEngine.TryParseDuration(row.EditDuration.Text, out seconds, out error))
            {
                ShowError(error);
                row.EditDuration.Focus();
                return;
            }
            if (app.EditTask(row.Task.Id, row.EditName.Text, seconds)) EndEdit(row);
        }

        private void EndEdit(RowView row)
        {
            if (!row.Editing) return;
            row.Editing = false;
            row.Editor.Visibility = Visibility.Collapsed;
            row.Labels.Visibility = Visibility.Visible;
            UpdateRow(row);
        }

        private void UpdateRow(RowView row)
        {
            bool active = row.Task.Status == OrbitTaskStatus.Running;
            bool paused = row.Task.Status == OrbitTaskStatus.Paused;
            bool completed = row.Task.Status == OrbitTaskStatus.Completed;
            row.Surface.Background = Display.Brush(active ? "#203239" : row.Hovered ? "#272630" : "#1C1D24");
            row.Name.Foreground = Display.Brush(completed ? "#A298AD" : "#E7E2ED");
            row.Name.TextDecorations = completed ? TextDecorations.Strikethrough : null;
            row.Time.Foreground = Display.Brush(active ? "#8EDBD5" : completed ? "#8E939F" : "#99A1AF");
            if (row.Task.IsChecklist)
            {
                row.Time.Text = completed ? "已完成" : "勾选完成";
                if (row.Check != null)
                {
                    row.Check.IsChecked = completed;
                    row.Check.ToolTip = completed ? "取消完成" : "标记完成";
                }
            }
            else
            {
                row.Time.Text = active || paused ? (paused ? "暂停 · " : "") + Display.Time(row.Task.RemainingSeconds) + " / " + Display.Duration(row.Task.DurationSeconds) : Display.Duration(row.Task.DurationSeconds);
                if (row.Play != null)
                {
                    string iconKind = active ? "pause" : "play";
                    if (row.PlayIconKind != iconKind)
                    {
                        row.PlayIconKind = iconKind;
                        row.PlayIcon = new VectorIcon(iconKind, 16, active ? "#8EDBD5" : "#C1B2D0");
                        row.Play.Content = row.PlayIcon;
                    }
                    row.Play.ToolTip = active ? "暂停倒计时" : paused ? "继续倒计时" : "开始倒计时";
                }
                if (row.Edit != null)
                {
                    row.Edit.Visibility = row.Hovered || row.Editing ? Visibility.Visible : Visibility.Hidden;
                    string kind = row.Editing ? "check" : "edit";
                    if (row.EditIconKind != kind)
                    {
                        row.EditIconKind = kind;
                        row.Edit.Content = new VectorIcon(kind, 14, row.Editing ? "#BFE2D5" : "#AAA1B4");
                    }
                    row.Edit.ToolTip = row.Editing ? "保存修改 · Enter" : "编辑名称和时长";
                }
                if (row.Restart != null) row.Restart.Visibility = row.Hovered || row.Editing ? Visibility.Visible : Visibility.Hidden;
            }
            if (row.Delete != null) row.Delete.Opacity = row.Hovered || row.Editing ? 1 : 0.72;
        }

        private void WireDrag(RowView row)
        {
            row.Outer.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                pressedTask = null;
                if (IsInteractive(e.OriginalSource as DependencyObject, row.Outer)) return;
                pressedTask = row.Task.Id;
                dragStart = e.GetPosition(this);
            };
            row.Outer.PreviewMouseLeftButtonUp += delegate { pressedTask = null; };
            row.Outer.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (pressedTask != row.Task.Id || dragging || e.LeftButton != MouseButtonState.Pressed) return;
                Point pointer = e.GetPosition(this);
                if (Math.Abs(pointer.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pointer.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragging = true;
                try { DragDrop.DoDragDrop(row.Outer, new DataObject(DragFormat, row.Task.Id), DragDropEffects.Move); }
                finally
                {
                    dragging = false;
                    pressedTask = null;
                    ClearInsertionLines();
                    if (rebuildAfterDrag) { rebuildAfterDrag = false; Refresh(true); }
                }
            };
            row.Outer.DragOver += delegate(object sender, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent(DragFormat)) return;
                e.Handled = true;
                e.Effects = DragDropEffects.Move;
                ClearInsertionLines();
                string moved = e.Data.GetData(DragFormat) as string;
                if (moved == row.Task.Id) return;
                bool after = e.GetPosition(row.Outer).Y > row.Outer.ActualHeight / 2;
                (after ? row.After : row.Before).Visibility = Visibility.Visible;
                var scroll = Find<ScrollViewer>("RowsScroll");
                double y = e.GetPosition(scroll).Y;
                if (y < 20) scroll.LineUp();
                else if (y > scroll.ActualHeight - 20) scroll.LineDown();
            };
            row.Outer.DragLeave += delegate { row.Before.Visibility = Visibility.Collapsed; row.After.Visibility = Visibility.Collapsed; };
            row.Outer.Drop += delegate(object sender, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent(DragFormat)) return;
                e.Handled = true;
                string moved = e.Data.GetData(DragFormat) as string;
                bool after = e.GetPosition(row.Outer).Y > row.Outer.ActualHeight / 2;
                ReorderForTest(moved, row.Task.Id, after);
                ClearInsertionLines();
            };
        }

        internal void ReorderForTest(string movedId, string targetId, bool after)
        {
            app.Act(delegate { app.Engine.ReorderTask(movedId, targetId, after); });
        }

        private void ClearInsertionLines()
        {
            foreach (var row in rows) { row.Before.Visibility = Visibility.Collapsed; row.After.Visibility = Visibility.Collapsed; }
        }

        private static bool IsInteractive(DependencyObject origin, DependencyObject row)
        {
            while (origin != null && origin != row)
            {
                if (origin is ButtonBase || origin is TextBoxBase) return true;
                var visual = origin as Visual;
                origin = visual != null ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(origin);
            }
            return false;
        }

        private sealed class RowView
        {
            internal OrbitTask Task;
            internal Grid Outer, Editor;
            internal Border Surface, Before, After;
            internal StackPanel Labels, Actions;
            internal TextBlock Name, Time;
            internal TextBox EditName, EditDuration;
            internal Button Play, Edit, Restart, Delete;
            internal VectorIcon PlayIcon;
            internal string PlayIconKind, EditIconKind;
            internal CheckBox Check;
            internal bool Hovered, CompletedGroup, Editing;
        }
    }
}
