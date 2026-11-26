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
using ShapePath = System.Windows.Shapes.Path;

namespace Orbit
{
    /// <summary>Local notes and a compact view of the shared automatic clipboard history.</summary>
    internal sealed class UtilityPanel : Window
    {
        private const int HistorySlotCount = 3;
        private const int ClipboardSlotsHeight = 240;
        private const double LockConfirmationSize = 11;
        private readonly AppController app;
        private readonly string dataDirectory;
        private readonly TextBlock title;
        private readonly ScrollViewer body;
        private readonly StackPanel notesView;
        private readonly StackPanel clipboardView;
        private readonly Grid clipboardSlots;
        private readonly TextBox notesEditor;
        private readonly TextBlock notesStatus;
        private readonly TextBlock clipboardStatus;
        private readonly TextBlock clipboardHint;
        private readonly CheckBox confirmLockedReplacement;
        private readonly Grid lockedReplacementPrompt;
        private readonly Button cancelLockedReplacement;
        private readonly ClipboardSlotView[] clipboardCards = new ClipboardSlotView[HistorySlotCount + 1];
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
        private Func<bool> pendingLockedReplacement;

        internal string Mode { get; private set; }
        internal TextBox NotesEditor { get { return notesEditor; } }
        internal bool ClipboardGesturePending { get { return clipboardGesture != null && !clipboardDragging; } }
        internal bool ClipboardGestureDragging { get { return clipboardDragging; } }
        internal bool ClipboardLockConfirmationPending { get { return pendingLockedReplacement != null; } }

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
            clipboardHint = Label("最近 3 项 · 单击复制 · 拖出使用", 12, "#ABA6B8");
            clipboardHint.Height = 18;
            clipboardHint.Margin = new Thickness(0, 0, 0, 8);
            AutomationProperties.SetAutomationId(clipboardHint, "ClipboardDropHint");
            clipboardView.Children.Add(clipboardHint);
            // A fixed grid gives the rounded screen pixels to the cards and
            // gaps without resizing the window when the extra row appears.
            clipboardSlots = new Grid { Height = ClipboardSlotsHeight };
            for (int i = 0; i < clipboardCards.Length; i++)
            {
                if (i != 0) clipboardSlots.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                clipboardSlots.RowDefinitions.Add(new RowDefinition());
                var slot = MakeClipboardCard(i);
                Grid.SetRow(slot, i * 2);
                clipboardSlots.Children.Add(slot);
            }
            clipboardView.Children.Add(clipboardSlots);
            clipboardStatus = StatusLabel();
            clipboardStatus.Height = 16;
            clipboardStatus.TextWrapping = TextWrapping.NoWrap;
            clipboardStatus.TextTrimming = TextTrimming.CharacterEllipsis;
            clipboardStatus.Margin = new Thickness(1, 6, 1, 0);
            AutomationProperties.SetAutomationId(clipboardStatus, "ClipboardStatus");
            clipboardView.Children.Add(clipboardStatus);
            confirmLockedReplacement = new CheckBox
            {
                Content = "替换锁定时确认",
                FontSize = LockConfirmationSize,
                Foreground = ColorBrush("#CFC2DF"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Height = 20,
                Margin = new Thickness(0, 4, 0, 0),
                Cursor = Cursors.Hand,
                SnapsToDevicePixels = true,
                Template = MakeLockConfirmationTemplate()
            };
            AutomationProperties.SetAutomationId(confirmLockedReplacement, "ConfirmLockedReplacement");
            AutomationProperties.SetName(confirmLockedReplacement, (string)confirmLockedReplacement.Content);
            confirmLockedReplacement.Click += delegate
            {
                if (app.ClipboardHistory.SetConfirmLockedReplacement(confirmLockedReplacement.IsChecked == true)) return;
                confirmLockedReplacement.IsChecked = app.ClipboardHistory.ConfirmLockedReplacement;
                SetClipboardStatus(app.ClipboardHistory.LastError ?? "确认设置未能保存。", true);
            };
            clipboardView.Children.Add(confirmLockedReplacement);
            modes.Children.Add(clipboardView);

            lockedReplacementPrompt = new Grid { Visibility = Visibility.Collapsed, Background = ColorBrush("#C01C1D24") };
            KeyboardNavigation.SetTabNavigation(lockedReplacementPrompt, KeyboardNavigationMode.Cycle);
            AutomationProperties.SetAutomationId(lockedReplacementPrompt, "LockedReplacementPrompt");
            var replacementCard = new Border
            {
                Background = ColorBrush("#262730"), BorderBrush = ColorBrush("#625441"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 268
            };
            var replacementContent = new StackPanel();
            var replacementTitle = Label("替换已锁定内容？", 13, "#F0EDF3");
            replacementTitle.FontWeight = FontWeights.SemiBold;
            replacementContent.Children.Add(replacementTitle);
            var replacementDetail = Label("替换后，原锁定内容会从剪贴板移除。", 11, "#C4BACB");
            replacementDetail.TextWrapping = TextWrapping.Wrap;
            replacementDetail.Margin = new Thickness(0, 5, 0, 12);
            replacementContent.Children.Add(replacementDetail);
            var replacementActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            cancelLockedReplacement = SmallButton("取消", "保留原锁定内容", delegate { CompleteLockedReplacement(false); });
            cancelLockedReplacement.MinWidth = 48;
            cancelLockedReplacement.Height = 27;
            AutomationProperties.SetAutomationId(cancelLockedReplacement, "ConfirmLockedReplaceNo");
            replacementActions.Children.Add(cancelLockedReplacement);
            var replace = SmallButton("替换", "替换已锁定内容", delegate { CompleteLockedReplacement(true); });
            replace.MinWidth = 48;
            replace.Height = 27;
            replace.Margin = new Thickness(7, 0, 0, 0);
            replace.Background = ColorBrush("#624C32");
            replace.Foreground = ColorBrush("#FFF2D7");
            AutomationProperties.SetAutomationId(replace, "ConfirmLockedReplaceYes");
            replacementActions.Children.Add(replace);
            replacementContent.Children.Add(replacementActions);
            replacementCard.Child = replacementContent;
            lockedReplacementPrompt.Children.Add(replacementCard);
            Grid.SetRowSpan(lockedReplacementPrompt, 2);
            layout.Children.Add(lockedReplacementPrompt);

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
                    if (pendingLockedReplacement != null) CompleteLockedReplacement(false);
                    else if (clipboardGesture != null || clipboardDragging) EndClipboardGesture(false);
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
                if (!IsVisible) { EndClipboardGesture(false); ClearLockedReplacement(); Flush(); }
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
            SetClipboardStatus("文字、图片、文件都可拖到小球锁定", false);
        }

        internal void OpenMode(string mode)
        {
            if (mode != "notes" && mode != "clipboard") throw new ArgumentException("Unknown utility mode.", "mode");
            if (mode != Mode)
            {
                EndClipboardGesture(false);
                ClearLockedReplacement();
            }
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
            var view = new ClipboardSlotView { IsLockedSlot = index == HistorySlotCount };
            clipboardCards[index] = view;
            var card = new Border
            {
                Background = ColorBrush("#22232D"), BorderBrush = ColorBrush("#383440"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8),
                Focusable = true, Cursor = Cursors.Hand, UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            view.Card = card;
            AutomationProperties.SetAutomationId(card, "ClipboardCard" + (index + 1));
            var grid = new Grid();
            view.ThumbnailColumn = new ColumnDefinition { Width = new GridLength(0) };
            grid.ColumnDefinitions.Add(view.ThumbnailColumn);
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            card.Child = grid;

            view.Image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(view.Image, "ClipboardImage" + (index + 1));
            view.FileIcon = new VectorIcon("document", 20, "#BBB0C8") { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var thumbnailContent = new Grid();
            thumbnailContent.Children.Add(view.Image);
            thumbnailContent.Children.Add(view.FileIcon);
            view.Thumbnail = new Border
            {
                Width = 52, Height = 52, Padding = new Thickness(4), CornerRadius = new CornerRadius(6),
                Background = ColorBrush("#16171D"), Child = thumbnailContent, VerticalAlignment = VerticalAlignment.Center,
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

            var actions = new Grid { VerticalAlignment = VerticalAlignment.Center };
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            if (!view.IsLockedSlot)
            {
                actions.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
                actions.RowDefinitions.Add(new RowDefinition { Height = new GridLength(21) });
            }
            string lockLabel = view.IsLockedSlot ? "解除锁定" : "锁定此项";
            view.LockToggle = SmallButton(String.Empty, lockLabel, delegate { ToggleClipboardLock(view); });
            view.LockToggle.Content = new VectorIcon(view.IsLockedSlot ? "unlock" : "lock", 15, view.IsLockedSlot ? "#E8C574" : "#B7AAC5");
            view.LockToggle.Width = 24;
            view.LockToggle.Height = 24;
            view.LockToggle.Padding = new Thickness(0);
            view.LockToggle.Margin = new Thickness(0);
            view.LockToggle.Background = Brushes.Transparent;
            if (!view.IsLockedSlot) Grid.SetRowSpan(view.LockToggle, 2);
            AutomationProperties.SetAutomationId(view.LockToggle, view.IsLockedSlot ? "UnlockSlot4" : "LockSlot" + (index + 1));
            actions.Children.Add(view.LockToggle);
            view.Copy = SmallButton(String.Empty, "复制此项", delegate { CopyCard(view); });
            view.Copy.Content = new VectorIcon("clipboard", 16, "#CFC2DF");
            view.Copy.Width = 24;
            view.Copy.Height = 22;
            view.Copy.Padding = new Thickness(0);
            view.Copy.Margin = new Thickness(0);
            Grid.SetColumn(view.Copy, 1);
            AutomationProperties.SetAutomationId(view.Copy, "CopySlot" + (index + 1));
            actions.Children.Add(view.Copy);
            if (!view.IsLockedSlot)
            {
                view.Delete = SmallButton(String.Empty, "删除此项", delegate { DeleteCard(view); });
                view.Delete.Content = new VectorIcon("close", 13, "#9D91AB");
                view.Delete.Width = 24;
                view.Delete.Height = 20;
                view.Delete.Padding = new Thickness(0);
                view.Delete.Margin = new Thickness(0);
                view.Delete.Background = Brushes.Transparent;
                Grid.SetColumn(view.Delete, 1);
                Grid.SetRow(view.Delete, 1);
                AutomationProperties.SetAutomationId(view.Delete, "DeleteSlot" + (index + 1));
                actions.Children.Add(view.Delete);
            }
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
                FilePath = entry.FilePath,
                ImagePng = entry.ImagePng == null ? null : (byte[])entry.ImagePng.Clone(),
                PixelWidth = entry.PixelWidth, PixelHeight = entry.PixelHeight,
                CapturedAtUtc = entry.CapturedAtUtc
            };
        }

        private void ToggleClipboardLock(ClipboardSlotView view)
        {
            ClipboardEntry selected = view.Entry;
            if (selected == null) return;
            var history = app.ClipboardHistory;
            if (view.IsLockedSlot)
            {
                bool unlocked = history.Unlock();
                SetClipboardStatus(unlocked ? "已解除锁定" : history.LastError ?? "未能解除锁定。", !unlocked);
                return;
            }
            RequestClipboardLock(delegate
            {
                foreach (var entry in history.Entries)
                    if (entry.Id == selected.Id) return history.Lock(selected.Id);
                // The three normal slots can still rotate while confirmation is open.
                return history.LockDropped(selected);
            });
        }

        internal bool TryLockDroppedFile(string path)
        {
            return TryLockDrop(delegate { return ClipboardMonitor.ReadDroppedFile(path, app.Now()); });
        }

        internal bool TryLockDroppedContent(IDataObject data)
        {
            return TryLockDrop(delegate { return ClipboardMonitor.ReadDroppedContent(data, app.Now()); });
        }

        private bool TryLockDrop(Func<ClipboardEntry> read)
        {
            try
            {
                if (closed || app.IsExiting || clipboardDragging) return false;
                ClipboardEntry dropped = read();
                if (dropped == null)
                {
                    SetClipboardStatus("请拖入选中的文字、一张图片或一个本地文件。", true);
                    return false;
                }
                return RequestClipboardLock(delegate { return app.ClipboardHistory.LockDropped(dropped); });
            }
            catch (Exception error)
            {
                RethrowFatal(error);
                SetClipboardStatus("未能锁定：" + error.Message, true);
                return false;
            }
        }

        private bool RequestClipboardLock(Func<bool> save)
        {
            if (pendingLockedReplacement != null) return false;
            var history = app.ClipboardHistory;
            if (history.LockedEntry == null || !history.ConfirmLockedReplacement) return SaveClipboardLock(save);

            if (!IsVisible || Mode != "clipboard") app.ShowTool("clipboard", false);
            if (!IsVisible || Mode != "clipboard") return false;
            pendingLockedReplacement = save;
            clipboardView.IsEnabled = false;
            lockedReplacementPrompt.Visibility = Visibility.Visible;
            FocusManager.SetFocusedElement(this, cancelLockedReplacement);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
            {
                if (pendingLockedReplacement != null && IsVisible) cancelLockedReplacement.Focus();
            }));
            return true;
        }

        private bool SaveClipboardLock(Func<bool> save)
        {
            bool saved = save();
            string warning = app.ClipboardHistory.LastError;
            SetClipboardStatus(warning ?? (saved ? "已放入底部锁定槽" : "未能锁定。"), !saved || warning != null);
            return saved;
        }

        private void CompleteLockedReplacement(bool confirmed)
        {
            var save = pendingLockedReplacement;
            ClearLockedReplacement();
            if (save == null) return;
            if (confirmed) SaveClipboardLock(save);
            else SetClipboardStatus("已保留原锁定内容", false);
        }

        private void ClearLockedReplacement()
        {
            pendingLockedReplacement = null;
            lockedReplacementPrompt.Visibility = Visibility.Collapsed;
            clipboardView.IsEnabled = true;
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
            ClipboardEntry locked = app.ClipboardHistory.LockedEntry;
            bool compact = locked != null;
            int lockedRow = HistorySlotCount * 2;
            clipboardSlots.RowDefinitions[lockedRow - 1].Height = new GridLength(compact ? 4 : 0);
            clipboardSlots.RowDefinitions[lockedRow].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            confirmLockedReplacement.IsChecked = app.ClipboardHistory.ConfirmLockedReplacement;
            clipboardHint.Text = compact ? "最近 3 项 · 底部是独立锁定槽" : "最近 3 项 · 单击复制 · 拖出使用";
            for (int i = 0; i < clipboardCards.Length; i++)
            {
                var view = clipboardCards[i];
                ClipboardEntry entry = view.IsLockedSlot ? locked : i < entries.Count ? entries[i] : null;
                bool empty = entry == null;
                bool image = !empty && entry.IsImage;
                bool file = !empty && entry.IsFile;
                string id = empty ? null : entry.Id;
                bool changedImage = view.ImageId != id;
                view.Entry = entry;
                view.EntryId = id;
                view.Card.Visibility = view.IsLockedSlot && !compact ? Visibility.Collapsed : Visibility.Visible;
                view.Card.Padding = compact ? new Thickness(8, 5, 8, 5) : new Thickness(8);
                view.Thumbnail.Width = view.Thumbnail.Height = compact ? 38 : 52;
                view.Title.Height = compact ? 16 : 18;
                view.Preview.Height = compact ? 17 : 34;
                view.Copy.IsEnabled = !empty;
                view.LockToggle.IsEnabled = !empty;
                if (view.Delete != null) view.Delete.IsEnabled = !empty;
                view.Card.Focusable = !empty;
                view.Card.Cursor = empty ? Cursors.Arrow : Cursors.Hand;
                view.Card.ToolTip = empty ? null : "单击复制 · 拖出使用";
                view.ThumbnailColumn.Width = new GridLength(image || file ? view.Thumbnail.Width + 8 : 0);
                view.Thumbnail.Visibility = image || file ? Visibility.Visible : Visibility.Collapsed;
                view.Image.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
                view.FileIcon.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
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
                    view.Detail.Text = entry.PixelWidth + " × " + entry.PixelHeight + " · " +
                        (view.IsLockedSlot ? "已锁定" : entry.CapturedAtUtc.ToLocalTime().ToString("HH:mm"));
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
                else if (file)
                {
                    view.Image.Source = null;
                    view.ImageId = null;
                    view.Title.Text = entry.FileName;
                    view.Title.ToolTip = entry.FileName;
                    view.Title.FontSize = 12;
                    view.Title.Foreground = ColorBrush("#E0DAE8");
                    view.Detail.Text = "文件 · 已锁定";
                    view.Detail.Visibility = Visibility.Visible;
                    view.Preview.Visibility = Visibility.Collapsed;
                    AutomationProperties.SetName(view.Card, "锁定的文件：" + entry.FileName);
                }
                else
                {
                    view.Image.Source = null;
                    view.ImageId = null;
                    view.Title.Text = "文字 · " + (view.IsLockedSlot ? "已锁定" : entry.CapturedAtUtc.ToLocalTime().ToString("HH:mm"));
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
            view.Card.Background = ColorBrush(view.IsLockedSlot ? hover ? "#322C2A" : "#29252A" : hover ? "#2B2936" : "#22232D");
            view.Card.BorderBrush = ColorBrush(view.IsLockedSlot ? hover ? "#F0CA75" : "#C49E52" : hover ? "#716082" : "#383440");
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
            internal VectorIcon FileIcon;
            internal TextBlock Title, Detail, Preview;
            internal Button Copy, Delete, LockToggle;
            internal bool IsLockedSlot;
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

        private static ControlTemplate MakeLockConfirmationTemplate()
        {
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            row.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

            var label = new FrameworkElementFactory(typeof(ContentPresenter));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.AppendChild(label);

            var mark = new FrameworkElementFactory(typeof(Grid));
            mark.SetValue(FrameworkElement.WidthProperty, LockConfirmationSize);
            mark.SetValue(FrameworkElement.HeightProperty, LockConfirmationSize);
            mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            row.AppendChild(mark);

            var outlineGeometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
            outlineGeometry.Children.Add(new RectangleGeometry(new Rect(0, 0, LockConfirmationSize, LockConfirmationSize)));
            outlineGeometry.Children.Add(new RectangleGeometry(new Rect(1, 1, LockConfirmationSize - 2, LockConfirmationSize - 2)));
            outlineGeometry.Freeze();
            var outline = new FrameworkElementFactory(typeof(ShapePath));
            outline.SetValue(ShapePath.DataProperty, outlineGeometry);
            outline.SetValue(ShapePath.FillProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            mark.AppendChild(outline);

            var square = new FrameworkElementFactory(typeof(ShapePath), "CheckedSquare");
            square.SetValue(ShapePath.DataProperty, new RectangleGeometry(new Rect(2, 2, LockConfirmationSize - 4, LockConfirmationSize - 4)));
            square.SetValue(ShapePath.FillProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            square.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
            mark.AppendChild(square);

            var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = row };
            var isChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            isChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckedSquare"));
            template.Triggers.Add(isChecked);
            return template;
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
