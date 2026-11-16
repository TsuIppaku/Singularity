using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Orbit
{
    /// <summary>Local notes and a compact view of the shared automatic clipboard history.</summary>
    internal sealed class UtilityPanel : Window
    {
        private readonly AppController app;
        private readonly string dataDirectory;
        private readonly TextBlock title;
        private readonly ScrollViewer body;
        private readonly StackPanel notesView;
        private readonly StackPanel clipboardView;
        private readonly TextBox notesEditor;
        private readonly TextBlock notesStatus;
        private readonly TextBlock clipboardStatus;
        private readonly ClipboardSlotView[] clipboardCards = new ClipboardSlotView[3];
        private readonly DispatcherTimer saveTimer;
        private bool notesLoaded;
        private bool changingNotes;
        private bool notesDirty;
        private volatile bool closed;
        private int clipboardRefreshQueued;
        private ClipboardGesture clipboardGesture;
        private bool clipboardDragging;
        private bool clipboardDragCancelled;
        private bool releasingClipboardCapture;
        private bool suppressClipboardMouseUp;
        private bool clipboardRefreshDeferred;

        internal string Mode { get; private set; }
        internal TextBox NotesEditor { get { return notesEditor; } }
        internal bool ClipboardGesturePending { get { return clipboardGesture != null && !clipboardDragging; } }
        internal bool ClipboardGestureDragging { get { return clipboardDragging; } }

        internal UtilityPanel(AppController app, string dataDirectory)
        {
            if (app == null) throw new ArgumentNullException("app");
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("A data directory is required.", "dataDirectory");
            this.app = app;
            this.dataDirectory = Path.GetFullPath(dataDirectory);
            Mode = "notes";
            Title = "Orbit · 记事本";
            Width = 320;
            Display.ConfigureWindow(this);
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = !app.IsTest;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 12;
            Foreground = ColorBrush("#DAD6E2");
            UseLayoutRounding = true;
            Resources.Add(typeof(Button), MakeButtonStyle());

            Border card = new Border
            {
                Background = ColorBrush("#1C1D24"), BorderBrush = ColorBrush("#3B3946"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(6)
            };
            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.Child = layout;
            Content = card;

            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title = Label("记事本", 13, "#E3DFEB");
            title.FontWeight = FontWeights.SemiBold;
            title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title);
            Button close = SmallButton("×", "收起工具面板", delegate { if (Flush()) Hide(); });
            close.Content = new VectorIcon("close", 16, "#A8A4B5");
            close.FontSize = 19;
            close.Padding = new Thickness(5, 0, 5, 1);
            close.Background = Brushes.Transparent;
            AutomationProperties.SetAutomationId(close, "UtilityClose");
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            layout.Children.Add(header);

            body = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            ApplyCompactScrollbar(body);
            Grid.SetRow(body, 1);
            layout.Children.Add(body);
            Grid modes = new Grid();
            body.Content = modes;

            notesView = new StackPanel();
            notesEditor = new TextBox
            {
                AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Height = 172, MinHeight = 70, FontSize = 13,
                Background = ColorBrush("#17181F"), Foreground = ColorBrush("#E3DFEB"),
                BorderBrush = ColorBrush("#393342"), BorderThickness = new Thickness(1),
                CaretBrush = ColorBrush("#DCC5FE"), SelectionBrush = ColorBrush("#625079"),
                Padding = new Thickness(9), Template = MakeEditorTemplate(),
                ToolTip = "随手记下想法，自动保存到本机"
            };
            AutomationProperties.SetName(notesEditor, "记事内容");
            AutomationProperties.SetAutomationId(notesEditor, "NotesEditor");
            notesView.Children.Add(notesEditor);
            notesStatus = StatusLabel();
            notesView.Children.Add(notesStatus);
            modes.Children.Add(notesView);

            clipboardView = new StackPanel { Visibility = Visibility.Collapsed };
            TextBlock hint = Label("最近 3 项 · 单击复制 · 拖出使用", 12, "#ABA6B8");
            hint.Height = 18;
            hint.Margin = new Thickness(0, 0, 0, 8);
            clipboardView.Children.Add(hint);
            for (int i = 0; i < clipboardCards.Length; i++) clipboardView.Children.Add(MakeClipboardCard(i));
            clipboardStatus = StatusLabel();
            clipboardStatus.Height = 16;
            clipboardStatus.TextWrapping = TextWrapping.NoWrap;
            clipboardStatus.TextTrimming = TextTrimming.CharacterEllipsis;
            clipboardStatus.Margin = new Thickness(1, 6, 1, 0);
            AutomationProperties.SetAutomationId(clipboardStatus, "ClipboardStatus");
            clipboardView.Children.Add(clipboardStatus);
            modes.Children.Add(clipboardView);

            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveNotes(); };
            notesEditor.TextChanged += delegate
            {
                if (changingNotes || !notesLoaded) return;
                notesDirty = true;
                SetStatus(notesStatus, "正在保存…", false);
                saveTimer.Stop();
                saveTimer.Start();
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    if (clipboardGesture != null || clipboardDragging) EndClipboardGesture(false);
                    else if (Flush()) Hide();
                    e.Handled = true;
                }
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                EndClipboardGesture(false);
                bool saved = Flush();
                if (!saved) { e.Cancel = true; return; }
                if (!app.IsExiting) { e.Cancel = true; Hide(); }
            };
            IsVisibleChanged += delegate
            {
                if (!IsVisible) { EndClipboardGesture(false); Flush(); }
                else FocusNotesWhenReady();
            };
            SizeChanged += delegate { if (IsVisible) app.RepositionTools(); };
            app.ClipboardHistory.Changed += OnClipboardChanged;
            Closed += delegate
            {
                closed = true;
                EndClipboardGesture(false);
                saveTimer.Stop();
                app.ClipboardHistory.Changed -= OnClipboardChanged;
            };
            RefreshClipboardCards();
            SetClipboardStatus("拖到文件夹或网站，松开即可使用", false);
        }

        internal void OpenMode(string mode)
        {
            if (mode != "notes" && mode != "clipboard") throw new ArgumentException("Unknown utility mode.", "mode");
            if (mode != Mode) EndClipboardGesture(false);
            Flush();
            Mode = mode;
            bool showNotes = mode == "notes";
            title.Text = showNotes ? "记事本" : "剪贴板";
            Title = "Orbit · " + title.Text;
            notesView.Visibility = showNotes ? Visibility.Visible : Visibility.Collapsed;
            clipboardView.Visibility = showNotes ? Visibility.Collapsed : Visibility.Visible;
            if (showNotes && !notesLoaded) LoadNotes();
            if (!showNotes)
            {
                RefreshClipboardCards();
                if (!String.IsNullOrWhiteSpace(app.ClipboardHistory.LastError))
                    SetClipboardStatus(app.ClipboardHistory.LastError, true);
            }
            PrepareHeight();
            FocusNotesWhenReady();
        }

        internal bool Flush()
        {
            saveTimer.Stop();
            return SaveNotes();
        }

        internal void PrepareHeight(double? availableHeight = null)
        {
            double scale = 1;
            PresentationSource source = PresentationSource.FromVisual(app.Dot);
            if (source != null && source.CompositionTarget != null)
                scale = source.CompositionTarget.TransformToDevice.M22;
            double available = Placement.WorkArea(app.Dot).Height / scale;
            // Reserve the launcher height and outer gaps on the dot's current monitor.
            double cap = available - 68 - 24;
            if (availableHeight.HasValue && !Double.IsNaN(availableHeight.Value)) cap = Math.Min(cap, availableHeight.Value);
            MaxHeight = Math.Max(1, cap);

            // Measure only the fixed header, not the scrollable body. Measuring
            // this child does not resize the HWND or trigger another placement.
            double chrome = 66;
            var border = Content as Border;
            var layout = border == null ? null : border.Child as Grid;
            if (layout != null && layout.Children.Count > 0)
            {
                Thickness margin = border.Margin, padding = border.Padding, edge = border.BorderThickness;
                double innerWidth = Math.Max(1, Width - margin.Left - margin.Right - padding.Left - padding.Right - edge.Left - edge.Right);
                var header = layout.Children[0] as FrameworkElement;
                if (header != null)
                {
                    header.Measure(new Size(innerWidth, Double.PositiveInfinity));
                    chrome = margin.Top + margin.Bottom + padding.Top + padding.Bottom + edge.Top + edge.Bottom + header.DesiredSize.Height;
                }
            }
            // A conservative physical-pixel allowance absorbs layout rounding.
            double bodyHeight = Math.Max(1, Math.Min(320, MaxHeight - chrome - 1 / scale));
            body.MinHeight = 0;
            body.MaxHeight = bodyHeight;
        }

        private void FocusNotesWhenReady()
        {
            if (app.IsTest || Mode != "notes") return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
            {
                if (IsVisible && Mode == "notes" && !app.IsExiting && notesLoaded) notesEditor.Focus();
            }));
        }

        private void LoadNotes()
        {
            try
            {
                string path = Path.Combine(dataDirectory, "notes.txt");
                string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : String.Empty;
                changingNotes = true;
                try { notesEditor.Text = text; }
                finally { changingNotes = false; }
                notesEditor.IsReadOnly = false;
                notesLoaded = true;
                notesDirty = false;
                SetStatus(notesStatus, "自动保存 · 仅本机", false);
            }
            catch (Exception error)
            {
                RethrowFatal(error);
                notesEditor.IsReadOnly = true;
                SetStatus(notesStatus, "读取失败，原文件已保留。请稍后重新打开。", true);
            }
        }

        private bool SaveNotes()
        {
            if (!notesLoaded || !notesDirty) return true;
            try
            {
                WriteAtomically("notes.txt", new UTF8Encoding(false).GetBytes(notesEditor.Text));
                notesDirty = false;
                SetStatus(notesStatus, "已保存 · 仅本机", false);
                return true;
            }
            catch (Exception error)
            {
                RethrowFatal(error);
                SetStatus(notesStatus, "保存失败，内容仍留在面板中。", true);
                return false;
            }
        }

        internal void SetClipboardStatus(string message, bool error)
        {
            if (closed || Dispatcher.HasShutdownStarted) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { SetClipboardStatus(message, error); }));
                return;
            }
            SetStatus(clipboardStatus, message ?? String.Empty, error);
            clipboardStatus.ToolTip = error ? message : null;
        }

        private void OnClipboardChanged(object sender, EventArgs args)
        {
            if (closed || Dispatcher.HasShutdownStarted) return;
            if (!Dispatcher.CheckAccess())
            {
                if (Interlocked.Exchange(ref clipboardRefreshQueued, 1) != 0) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    Interlocked.Exchange(ref clipboardRefreshQueued, 0);
                    if (!closed) RefreshClipboardCards();
                }));
                return;
            }
            RefreshClipboardCards();
        }

        private Border MakeClipboardCard(int index)
        {
            var view = new ClipboardSlotView();
            clipboardCards[index] = view;
            var card = new Border
            {
                Height = 84, Background = ColorBrush("#22232D"), BorderBrush = ColorBrush("#383440"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, index < 2 ? 6 : 0),
                Focusable = true, Cursor = Cursors.Hand, UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            view.Card = card;
            AutomationProperties.SetAutomationId(card, "ClipboardCard" + (index + 1));
            var grid = new Grid();
            view.ThumbnailColumn = new ColumnDefinition { Width = new GridLength(0) };
            grid.ColumnDefinitions.Add(view.ThumbnailColumn);
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            card.Child = grid;

            view.Image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(view.Image, "ClipboardImage" + (index + 1));
            view.Thumbnail = new Border
            {
                Width = 60, Height = 60, Padding = new Thickness(4), CornerRadius = new CornerRadius(6),
                Background = ColorBrush("#16171D"), Child = view.Image, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed
            };
            grid.Children.Add(view.Thumbnail);

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            view.Title = Label("等待复制", 12, "#DAD5E3");
            view.Title.TextTrimming = TextTrimming.CharacterEllipsis;
            view.Title.Height = 18;
            view.Detail = Label(String.Empty, 11, "#9F96AC");
            view.Detail.TextTrimming = TextTrimming.CharacterEllipsis;
            view.Detail.Height = 16;
            view.Detail.Margin = new Thickness(0, 4, 0, 0);
            view.Preview = Label(String.Empty, 13, "#DAD5E3");
            view.Preview.TextWrapping = TextWrapping.Wrap;
            view.Preview.TextTrimming = TextTrimming.CharacterEllipsis;
            view.Preview.LineHeight = 17;
            view.Preview.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            view.Preview.Height = 34;
            view.Preview.Margin = new Thickness(0, 3, 0, 0);
            AutomationProperties.SetAutomationId(view.Title, "ClipboardName" + (index + 1));
            AutomationProperties.SetAutomationId(view.Detail, "ClipboardDetail" + (index + 1));
            AutomationProperties.SetAutomationId(view.Preview, "SlotPreview" + (index + 1));
            labels.Children.Add(view.Title);
            labels.Children.Add(view.Detail);
            labels.Children.Add(view.Preview);
            Grid.SetColumn(labels, 1);
            grid.Children.Add(labels);

            var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            view.Copy = SmallButton(String.Empty, "复制此项", delegate { CopyCard(view); });
            view.Copy.Content = new VectorIcon("clipboard", 16, "#CFC2DF");
            view.Copy.Width = 26;
            view.Copy.Height = 27;
            view.Copy.Padding = new Thickness(0);
            view.Copy.Margin = new Thickness(0, 0, 0, 4);
            AutomationProperties.SetAutomationId(view.Copy, "CopySlot" + (index + 1));
            actions.Children.Add(view.Copy);
            view.Delete = SmallButton(String.Empty, "删除此项", delegate { DeleteCard(view); });
            view.Delete.Content = new VectorIcon("close", 13, "#9D91AB");
            view.Delete.Width = 26;
            view.Delete.Height = 24;
            view.Delete.Padding = new Thickness(0);
            view.Delete.Margin = new Thickness(0);
            view.Delete.Background = Brushes.Transparent;
            AutomationProperties.SetAutomationId(view.Delete, "DeleteSlot" + (index + 1));
            actions.Children.Add(view.Delete);
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            card.MouseEnter += delegate { PaintClipboardCard(view); };
            card.MouseLeave += delegate { PaintClipboardCard(view); };
            card.GotKeyboardFocus += delegate { PaintClipboardCard(view); };
            card.LostKeyboardFocus += delegate { PaintClipboardCard(view); };
            card.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                suppressClipboardMouseUp = false;
                if (IsInteractiveChild(e.OriginalSource as DependencyObject, card)) return;
                if (BeginClipboardGesture(index, e.GetPosition(this), true)) e.Handled = true;
            };
            card.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (clipboardGesture == null || clipboardGesture.Source != card) return;
                if (MoveClipboardGesture(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed)) e.Handled = true;
            };
            card.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if (clipboardGesture == null && IsInteractiveChild(e.OriginalSource as DependencyObject, card)) return;
                if (suppressClipboardMouseUp)
                {
                    suppressClipboardMouseUp = false;
                    e.Handled = true;
                    return;
                }
                if (clipboardGesture == null || clipboardGesture.Source != card) return;
                EndClipboardGesture(true);
                e.Handled = true;
            };
            card.LostMouseCapture += delegate
            {
                if (releasingClipboardCapture || clipboardDragging || clipboardGesture == null || clipboardGesture.Source != card) return;
                EndClipboardGesture(false);
            };
            card.QueryContinueDrag += delegate(object sender, QueryContinueDragEventArgs e)
            {
                if (!clipboardDragging) return;
                if (e.EscapePressed || clipboardDragCancelled || closed || !IsVisible || Mode != "clipboard")
                {
                    clipboardDragCancelled = true;
                    e.Action = DragAction.Cancel;
                    e.Handled = true;
                }
            };
            card.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Enter && e.Key != Key.Space) return;
                if (IsInteractiveChild(e.OriginalSource as DependencyObject, card)) return;
                CopyCard(view);
                e.Handled = true;
            };
            Motion.WireHover(card);
            Motion.WirePress(card);
            return card;
        }

        private void CopyCard(ClipboardSlotView view)
        {
            // Promote may synchronously rebuild the slot order. Capture identity first.
            string id = view.EntryId;
            if (!String.IsNullOrEmpty(id)) app.CopyClipboardEntry(id);
        }

        internal bool BeginClipboardGesture(int slotIndex, Point panelPoint, bool captureMouse)
        {
            if (closed || clipboardDragging || Mode != "clipboard" || slotIndex < 0 || slotIndex >= clipboardCards.Length) return false;
            ClipboardSlotView view = clipboardCards[slotIndex];
            if (view.Entry == null) return false;
            ClipboardEntry snapshot = SnapshotEntry(view.Entry);
            EndClipboardGesture(false);
            clipboardDragCancelled = false;
            suppressClipboardMouseUp = false;
            clipboardGesture = new ClipboardGesture { Source = view.Card, Snapshot = snapshot, Start = panelPoint };
            if (captureMouse && !view.Card.CaptureMouse())
            {
                EndClipboardGesture(false);
                return false;
            }
            return true;
        }

        internal bool MoveClipboardGesture(Point panelPoint, bool leftButtonPressed)
        {
            ClipboardGesture gesture = clipboardGesture;
            if (gesture == null || clipboardDragging) return false;
            if (!leftButtonPressed) { EndClipboardGesture(false); return false; }
            if (Math.Abs(panelPoint.X - gesture.Start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(panelPoint.Y - gesture.Start.Y) < SystemParameters.MinimumVerticalDragDistance) return false;

            clipboardDragging = true;
            suppressClipboardMouseUp = true;
            // OLE owns capture during DoDragDrop. Releasing our pending capture is
            // intentional and must not run the cancellation path recursively.
            ReleaseClipboardCapture(gesture.Source);
            try { app.DragClipboardEntry(gesture.Source, gesture.Snapshot); }
            catch (Exception error)
            {
                RethrowFatal(error);
                SetClipboardStatus("拖出未完成，请稍后重试。", true);
            }
            finally
            {
                clipboardGesture = null;
                clipboardDragging = false;
                clipboardDragCancelled = false;
                suppressClipboardMouseUp = true;
                ReleaseClipboardCapture(gesture.Source);
                FlushClipboardRefresh();
            }
            // Copy and None are both completed drag gestures, never a click.
            return true;
        }

        internal void EndClipboardGesture(bool allowClick)
        {
            if (clipboardDragging)
            {
                if (!allowClick) clipboardDragCancelled = true;
                suppressClipboardMouseUp = true;
                return;
            }
            ClipboardGesture gesture = clipboardGesture;
            if (gesture == null) return;
            clipboardGesture = null;
            if (!allowClick) suppressClipboardMouseUp = true;
            ReleaseClipboardCapture(gesture.Source);
            FlushClipboardRefresh();
            // The entry may have been evicted meanwhile. The controller then
            // rejects this original id instead of copying a newly shifted slot.
            if (allowClick && !closed) app.CopyClipboardEntry(gesture.Snapshot.Id);
        }

        private void ReleaseClipboardCapture(Border source)
        {
            if (source == null || Mouse.Captured != source) return;
            releasingClipboardCapture = true;
            try { source.ReleaseMouseCapture(); }
            finally { releasingClipboardCapture = false; }
        }

        private void FlushClipboardRefresh()
        {
            if (!clipboardRefreshDeferred) return;
            clipboardRefreshDeferred = false;
            if (!closed) RefreshClipboardCards();
        }

        private static ClipboardEntry SnapshotEntry(ClipboardEntry entry)
        {
            return new ClipboardEntry
            {
                Id = entry.Id, Text = entry.Text, FileName = entry.FileName,
                ImagePng = entry.ImagePng == null ? null : (byte[])entry.ImagePng.Clone(),
                PixelWidth = entry.PixelWidth, PixelHeight = entry.PixelHeight,
                CapturedAtUtc = entry.CapturedAtUtc
            };
        }

        private void DeleteCard(ClipboardSlotView view)
        {
            string id = view.EntryId;
            if (String.IsNullOrEmpty(id)) return;
            if (app.ClipboardHistory.Remove(id))
                SetClipboardStatus("已删除这条记录", false);
            else
                SetClipboardStatus(app.ClipboardHistory.LastError ?? "删除未完成，请稍后重试。", true);
        }

        private void RefreshClipboardCards()
        {
            if (closed) return;
            if (clipboardGesture != null || clipboardDragging)
            {
                clipboardRefreshDeferred = true;
                return;
            }
            var entries = app.ClipboardHistory.Entries;
            for (int i = 0; i < clipboardCards.Length; i++)
            {
                var view = clipboardCards[i];
                ClipboardEntry entry = i < entries.Count ? entries[i] : null;
                bool empty = entry == null;
                bool image = !empty && entry.IsImage;
                string id = empty ? null : entry.Id;
                bool changedImage = view.ImageId != id;
                view.Entry = entry;
                view.EntryId = id;
                view.Copy.IsEnabled = !empty;
                view.Delete.IsEnabled = !empty;
                view.Card.Focusable = !empty;
                view.Card.Cursor = empty ? Cursors.Arrow : Cursors.Hand;
                view.Card.ToolTip = empty ? null : "单击复制 · 拖出使用";
                view.ThumbnailColumn.Width = new GridLength(image ? 68 : 0);
                view.Thumbnail.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
                view.Title.ToolTip = null;
                view.Preview.ToolTip = null;
                view.Detail.ToolTip = null;

                if (empty)
                {
                    view.Image.Source = null;
                    view.ImageId = null;
                    view.Title.Text = "等待复制";
                    view.Title.FontSize = 12;
                    view.Title.Foreground = ColorBrush("#ADA3BB");
                    view.Detail.Visibility = Visibility.Collapsed;
                    view.Preview.Visibility = Visibility.Visible;
                    view.Preview.Text = "文字和图片会自动出现在这里";
                    view.Preview.FontSize = 11;
                    view.Preview.Foreground = ColorBrush("#8F849F");
                    AutomationProperties.SetName(view.Card, "剪贴板空槽位 " + (i + 1));
                }
                else if (image)
                {
                    string name = String.IsNullOrWhiteSpace(entry.FileName) ? "剪贴板图片.png" : entry.FileName;
                    view.Title.Text = name;
                    view.Title.ToolTip = name;
                    view.Title.FontSize = 12;
                    view.Title.Foreground = ColorBrush("#E0DAE8");
                    view.Detail.Text = entry.PixelWidth + " × " + entry.PixelHeight + " · " + entry.CapturedAtUtc.ToLocalTime().ToString("HH:mm");
                    view.Detail.Visibility = Visibility.Visible;
                    view.Preview.Visibility = Visibility.Collapsed;
                    if (changedImage || view.Image.Source == null)
                    {
                        view.Image.Source = LoadThumbnail(entry);
                        view.ImageId = id;
                    }
                    if (view.Image.Source == null) view.Detail.Text = "预览暂不可用 · 可复制原图";
                    AutomationProperties.SetName(view.Card, "剪贴板图片 " + (i + 1) + "：" + name);
                }
                else
                {
                    view.Image.Source = null;
                    view.ImageId = null;
                    view.Title.Text = "文字 · " + entry.CapturedAtUtc.ToLocalTime().ToString("HH:mm");
                    view.Title.FontSize = 11;
                    view.Title.Foreground = ColorBrush("#A89CB7");
                    view.Detail.Visibility = Visibility.Collapsed;
                    view.Preview.Visibility = Visibility.Visible;
                    view.Preview.Text = TextPreview(entry.Text);
                    view.Preview.FontSize = 13;
                    view.Preview.Foreground = ColorBrush("#DDD7E6");
                    AutomationProperties.SetName(view.Card, "剪贴板文字 " + (i + 1));
                }
                PaintClipboardCard(view);
            }
            if (!String.IsNullOrWhiteSpace(app.ClipboardHistory.LastError))
                SetClipboardStatus(app.ClipboardHistory.LastError, true);
        }

        private static BitmapImage LoadThumbnail(ClipboardEntry entry)
        {
            if (entry.ImagePng == null || entry.ImagePng.Length == 0) return null;
            try
            {
                using (var stream = new MemoryStream(entry.ImagePng, false))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    if (entry.PixelHeight > entry.PixelWidth && entry.PixelHeight > 0)
                        bitmap.DecodePixelHeight = Math.Min(192, entry.PixelHeight);
                    else if (entry.PixelWidth > 0)
                        bitmap.DecodePixelWidth = Math.Min(192, entry.PixelWidth);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception error) { RethrowFatal(error); return null; }
        }

        private static string TextPreview(string text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            var result = new StringBuilder();
            bool previousSpace = false;
            foreach (char character in text)
            {
                bool space = Char.IsWhiteSpace(character);
                if (space && (previousSpace || result.Length == 0)) continue;
                result.Append(space ? ' ' : character);
                previousSpace = space;
                if (result.Length >= 240)
                {
                    if (Char.IsHighSurrogate(result[result.Length - 1])) result.Length--;
                    result.Append('…');
                    break;
                }
            }
            return result.ToString().Trim();
        }

        private static void PaintClipboardCard(ClipboardSlotView view)
        {
            bool active = !String.IsNullOrEmpty(view.EntryId);
            bool hover = active && (view.Card.IsMouseOver || view.Card.IsKeyboardFocusWithin);
            view.Card.Background = ColorBrush(hover ? "#2B2936" : "#22232D");
            view.Card.BorderBrush = ColorBrush(hover ? "#716082" : "#383440");
        }

        private static bool IsInteractiveChild(DependencyObject origin, DependencyObject card)
        {
            while (origin != null && origin != card)
            {
                if (origin is ButtonBase || origin is TextBoxBase) return true;
                var visual = origin as Visual;
                origin = visual != null ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(origin);
            }
            return false;
        }

        private static void ApplyCompactScrollbar(ScrollViewer target)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Orbit.Panel.xaml"))
            {
                if (stream == null) return;
                var timerView = XamlReader.Load(stream) as FrameworkElement;
                var timerScroll = timerView == null ? null : timerView.FindName("RowsScroll") as ScrollViewer;
                if (timerScroll == null) return;
                foreach (DictionaryEntry resource in timerScroll.Resources)
                    target.Resources[resource.Key] = resource.Value;
            }
        }

        private sealed class ClipboardSlotView
        {
            internal ClipboardEntry Entry;
            internal string EntryId, ImageId;
            internal Border Card, Thumbnail;
            internal ColumnDefinition ThumbnailColumn;
            internal Image Image;
            internal TextBlock Title, Detail, Preview;
            internal Button Copy, Delete;
        }

        private sealed class ClipboardGesture
        {
            internal Border Source;
            internal ClipboardEntry Snapshot;
            internal Point Start;
        }

        private void WriteAtomically(string name, byte[] bytes)
        {
            Directory.CreateDirectory(dataDirectory);
            string destination = Path.Combine(dataDirectory, name);
            string temporary = Path.Combine(dataDirectory, ".orbit-util-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(true);
                }
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static TextBlock Label(string text, double size, string color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = ColorBrush(color) };
        }

        private static TextBlock StatusLabel()
        {
            return new TextBlock
            {
                FontSize = 11, Foreground = ColorBrush("#A8A0B7"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 7, 1, 0)
            };
        }

        private static void SetStatus(TextBlock label, string text, bool error)
        {
            label.Text = text;
            label.Foreground = ColorBrush(error ? "#DDA7B3" : "#8D859A");
        }

        private static Button SmallButton(string text, string tooltip, Action action)
        {
            Button button = new Button
            {
                Content = text, ToolTip = tooltip, FontSize = 12,
                Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(3, 0, 0, 0),
                Background = ColorBrush("#302C3B"), Foreground = ColorBrush("#D6CEE2"),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand
            };
            AutomationProperties.SetName(button, tooltip);
            button.Click += delegate { action(); };
            Motion.WireHover(button);
            Motion.WirePress(button);
            return button;
        }

        private static Style MakeButtonStyle()
        {
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border), "Surface");
            surface.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            surface.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            surface.AppendChild(content);
            ControlTemplate template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.78, "Surface"));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "Surface"));
            template.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "Surface"));
            template.Triggers.Add(disabled);
            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private static ControlTemplate MakeEditorTemplate()
        {
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border), "EditorSurface");
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            foreach (DependencyProperty property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty, Border.PaddingProperty })
                surface.SetBinding(property, new Binding(property.Name) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            surface.AppendChild(content);
            ControlTemplate template = new ControlTemplate(typeof(TextBox)) { VisualTree = surface };
            Trigger focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, ColorBrush("#79628E"), "EditorSurface"));
            template.Triggers.Add(focused);
            return template;
        }

        private static SolidColorBrush ColorBrush(string color)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static void RethrowFatal(Exception error)
        {
            if (error is OutOfMemoryException || error is StackOverflowException || error is AccessViolationException) throw error;
        }
    }
}
