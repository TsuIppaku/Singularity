using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Orbit
{
    internal sealed class LauncherWindow : Window
    {
        private readonly Dictionary<string, Button> buttons = new Dictionary<string, Button>();
        private readonly Dictionary<string, VectorIcon> glyphs = new Dictionary<string, VectorIcon>();
        internal string Selected { get; private set; }

        internal LauncherWindow(AppController app)
        {
            Title = "Orbit · 工具";
            Width = 228;
            Height = 68;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            var surface = new Border
            {
                Background = SurfaceGradient(), BorderBrush = EdgeGradient(),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
                Margin = new Thickness(6), Padding = new Thickness(11, 5, 11, 5),
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            var row = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true };
            for (int i = 0; i < 3; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            string[] modes = { "timer", "notes", "clipboard" };
            string[] labels = { "任务计时器", "记事本", "剪贴板" };
            for (int i = 0; i < modes.Length; i++)
            {
                string mode = modes[i];
                var icon = new VectorIcon(mode == "notes" ? "note" : mode, 24, "#CFD0DB");
                var button = new Button
                {
                    Width = 48, Height = 44, Content = icon, Background = Display.Brush("#292A32"),
                    BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Focusable = true,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    UseLayoutRounding = true, SnapsToDevicePixels = true, Template = ButtonTemplate()
                };
                var tip = new ToolTip
                {
                    Content = labels[i], Background = Display.Brush("#292A32"),
                    Foreground = Display.Brush("#EBE6F1"), BorderBrush = Display.Brush("#494250"),
                    BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6),
                    FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontSize = 11,
                    UseLayoutRounding = true, SnapsToDevicePixels = true
                };
                TextOptions.SetTextFormattingMode(tip, TextFormattingMode.Display);
                TextOptions.SetTextRenderingMode(tip, TextRenderingMode.Grayscale);
                button.ToolTip = tip;
                ToolTipService.SetInitialShowDelay(button, 350);
                ToolTipService.SetShowDuration(button, 6000);
                System.Windows.Automation.AutomationProperties.SetName(button, labels[i]);
                System.Windows.Automation.AutomationProperties.SetAutomationId(button, "tool-" + mode);
                button.Click += delegate { app.ShowTool(mode, true); };
                button.MouseEnter += delegate { PaintButton(mode); };
                button.MouseLeave += delegate { PaintButton(mode); };
                button.GotKeyboardFocus += delegate { PaintButton(mode); };
                button.LostKeyboardFocus += delegate { PaintButton(mode); };
                Motion.WireHover(button);
                Motion.WirePress(button);
                buttons.Add(mode, button);
                glyphs.Add(mode, icon);
                Grid.SetColumn(button, i);
                row.Children.Add(button);
            }
            surface.Child = row;
            Content = surface;
            SourceInitialized += delegate { NativeDesktop.PreventActivation(this); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!app.IsExiting) { e.Cancel = true; app.HideTools(); }
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { app.HideTools(); e.Handled = true; }
            };
            SizeChanged += delegate { app.RepositionTools(); };
            Loaded += delegate { app.RepositionTools(); };
        }

        internal Button ToolButton(string mode) { return buttons[mode]; }

        internal void SetSelected(string mode)
        {
            Selected = mode;
            foreach (string key in buttons.Keys) PaintButton(key);
        }

        private void PaintButton(string mode)
        {
            bool active = mode == Selected;
            Button button = buttons[mode];
            button.Background = active ? Display.Brush("#393247") :
                button.IsMouseOver || button.IsKeyboardFocused ? Display.Brush("#3A3643") : Display.Brush("#292A32");
            button.BorderBrush = active ? Display.Brush("#74618C") : Brushes.Transparent;
            glyphs[mode].ColorHex = active ? "#D3B4FA" : "#CFD0DB";
        }

        private static LinearGradientBrush SurfaceGradient()
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#30313A"), 0));
            gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#292A32"), .5));
            gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#25262E"), 1));
            gradient.Freeze();
            return gradient;
        }

        private static LinearGradientBrush EdgeGradient()
        {
            var gradient = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#514A5B"),
                (Color)ColorConverter.ConvertFromString("#2B2833"), 90);
            gradient.Freeze();
            return gradient;
        }

        private static ControlTemplate ButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "ButtonSurface");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, Display.Brush("#A595BD"), "ButtonSurface"));
            template.Triggers.Add(focus);
            return template;
        }
    }

    /// <summary>Resolution-independent icons drawn on one 24 by 24 coordinate system.</summary>
    internal sealed class VectorIcon : FrameworkElement
    {
        private static readonly Dictionary<string, Geometry> Paths = CreatePaths();
        private string kind;
        private string colorHex;
        private SolidColorBrush ink;

        internal VectorIcon(string kind, double size) : this(kind, size, "#CFD0DB") { }

        internal VectorIcon(string kind, double size, string hex)
        {
            if (Double.IsNaN(size) || Double.IsInfinity(size) || size <= 0) throw new ArgumentOutOfRangeException("size");
            Width = size;
            Height = size;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
            Focusable = false;
            Kind = kind;
            ColorHex = hex;
        }

        internal string Kind
        {
            get { return kind; }
            set
            {
                string next = value == "notes" ? "note" : value ?? String.Empty;
                if (kind == next) return;
                kind = next;
                InvalidateVisual();
            }
        }

        internal string ColorHex
        {
            get { return colorHex; }
            set
            {
                if (colorHex == value && ink != null) return;
                SolidColorBrush next = Display.Brush(value ?? "#CFD0DB");
                colorHex = value ?? "#CFD0DB";
                ink = next;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            if (ink == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            double size = Math.Min(ActualWidth, ActualHeight);
            drawing.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
            drawing.PushTransform(new ScaleTransform(size / 24, size / 24));
            var pen = new Pen(ink, 1.7)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
                DashCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            Geometry path;
            if (kind == "timer")
            {
                drawing.DrawEllipse(null, pen, new Point(12, 12), 8.75, 8.75);
                drawing.DrawGeometry(null, pen, Paths["timer"]);
            }
            else if (kind == "clipboard")
            {
                drawing.DrawGeometry(null, pen, Paths["clipboard"]);
                drawing.DrawRoundedRectangle(null, pen, new Rect(8.5, 2.75, 7, 4.5), 1.4, 1.4);
            }
            else if (kind == "grip")
            {
                for (int row = 0; row < 3; row++)
                {
                    drawing.DrawEllipse(ink, null, new Point(9, 6 + row * 6), 1.05, 1.05);
                    drawing.DrawEllipse(ink, null, new Point(15, 6 + row * 6), 1.05, 1.05);
                }
            }
            else if (Paths.TryGetValue(kind, out path)) drawing.DrawGeometry(null, pen, path);
            drawing.Pop();
            drawing.Pop();
        }

        private static Dictionary<string, Geometry> CreatePaths()
        {
            var paths = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            AddPath(paths, "timer", "M12,7 L12,12.25 L15.5,14.25");
            AddPath(paths, "note", "M6.75,3.75 L14.25,3.75 L18.5,8 L18.5,19.25 Q18.5,20.25 17.5,20.25 L6.5,20.25 Q5.5,20.25 5.5,19.25 L5.5,4.75 Q5.5,3.75 6.75,3.75 M14,4 L14,8.5 L18,8.5 M8.75,12.25 L15.25,12.25 M8.75,16 L14.25,16");
            AddPath(paths, "clipboard", "M8.5,5 L7.5,5 C6.4,5 5.5,5.9 5.5,7 L5.5,19 C5.5,20.1 6.4,21 7.5,21 L16.5,21 C17.6,21 18.5,20.1 18.5,19 L18.5,7 C18.5,5.9 17.6,5 16.5,5 L15.5,5 M9,11.5 L15,11.5 M9,15.5 L15,15.5");
            AddPath(paths, "play", "M8.5,5.5 L18,12 L8.5,18.5 Z");
            AddPath(paths, "pause", "M8.5,6 L8.5,18 M15.5,6 L15.5,18");
            AddPath(paths, "close", "M6.75,6.75 L17.25,17.25 M17.25,6.75 L6.75,17.25");
            AddPath(paths, "enter", "M18,6.5 L18,13 L6.5,13 M10.25,9.25 L6.5,13 L10.25,16.75");
            AddPath(paths, "check", "M5.75,12.25 L10,16.5 L18.25,7.75");
            AddPath(paths, "chevronDown", "M7,9.5 L12,14.5 L17,9.5");
            AddPath(paths, "chevronUp", "M7,14.5 L12,9.5 L17,14.5");
            AddPath(paths, "trash", "M8,7 L16,7 M10,7 L10.8,4.5 L13.2,4.5 L14,7 M9.2,9 L9.8,19.25 L14.2,19.25 L14.8,9 M11,11.5 L11,16.5 M13,11.5 L13,16.5");
            AddPath(paths, "edit", "M5.5,17.8 L5.5,19.2 L6.9,19.2 L17.9,8.2 L16.5,6.8 Z M15.2,8.1 L16.6,9.5");
            AddPath(paths, "restart", "M18.25,10.4 C17.5,7.4 14.9,5.25 11.75,5.25 C8,5.25 5,8.25 5,12 C5,15.75 8,18.75 11.75,18.75 C14.6,18.75 17.05,17.05 18,14.65 M18.25,5.25 L18.25,10.4 L13.1,10.4");
            return paths;
        }

        private static void AddPath(Dictionary<string, Geometry> paths, string key, string data)
        {
            Geometry geometry = Geometry.Parse(data);
            geometry.Freeze();
            paths.Add(key, geometry);
        }
    }

    internal static class Motion
    {
        private sealed class ScaleState
        {
            internal int Flags;
            internal int Generation;
            internal bool Pressed;
            internal ScaleTransform Scale;
        }

        private sealed class RevealState
        {
            internal int Generation;
        }

        private static readonly DependencyProperty ScaleStateProperty = DependencyProperty.RegisterAttached("ScaleState", typeof(ScaleState), typeof(Motion), new PropertyMetadata(null));
        private static readonly DependencyProperty RevealStateProperty = DependencyProperty.RegisterAttached("RevealState", typeof(RevealState), typeof(Motion), new PropertyMetadata(null));

        private static ScaleState State(FrameworkElement element)
        {
            var state = element.GetValue(ScaleStateProperty) as ScaleState;
            if (state != null) return state;
            state = new ScaleState { Scale = new ScaleTransform(1, 1) };
            element.SetValue(ScaleStateProperty, state);
            element.RenderTransform = state.Scale;
            element.RenderTransformOrigin = new Point(.5, .5);
            element.UseLayoutRounding = true;
            element.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(element, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(element, TextRenderingMode.Grayscale);
            element.Unloaded += delegate { ResetScale(state); };
            element.IsVisibleChanged += delegate { if (!element.IsVisible) ResetScale(state); };
            element.IsEnabledChanged += delegate { if (!element.IsEnabled) ResetScale(state); };
            return state;
        }

        internal static void WireHover(FrameworkElement element)
        {
            ScaleState state = State(element);
            if ((state.Flags & 1) != 0) return;
            state.Flags |= 1;
            element.MouseEnter += delegate
            {
                if (element.IsEnabled && !state.Pressed) AnimateScale(state, new[] { 1.025, 1.0 }, new[] { 75, 170 });
            };
            element.MouseLeave += delegate
            {
                if (!state.Pressed) AnimateScale(state, new[] { 1.0 }, new[] { 100 });
            };
        }

        internal static void WirePress(FrameworkElement element)
        {
            ScaleState state = State(element);
            if ((state.Flags & 2) != 0) return;
            state.Flags |= 2;
            element.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(delegate
            {
                if (!element.IsEnabled) return;
                state.Pressed = true;
                AnimateScale(state, new[] { .92, 1.0 }, new[] { 60, 165 });
            }), true);
            element.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(delegate
            {
                if (!state.Pressed) return;
                state.Pressed = false;
                Bounce(element);
            }), true);
            element.LostMouseCapture += delegate { if (state.Pressed) ResetScale(state); };
            element.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if ((e.Key == Key.Space || e.Key == Key.Enter) && !e.IsRepeat && element.IsEnabled)
                {
                    state.Pressed = true;
                    AnimateScale(state, new[] { .92, 1.0 }, new[] { 60, 165 });
                }
            };
            element.PreviewKeyUp += delegate(object sender, KeyEventArgs e)
            {
                if ((e.Key == Key.Space || e.Key == Key.Enter) && state.Pressed) { state.Pressed = false; Bounce(element); }
            };
        }

        internal static void Bounce(FrameworkElement element)
        {
            AnimateScale(State(element), new[] { .92, 1.05, 1.0 }, new[] { 35, 110, 220 });
        }

        private static void AnimateScale(ScaleState state, double[] values, int[] times)
        {
            int generation = ++state.Generation;
            double from = state.Scale.ScaleX;
            state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            state.Scale.ScaleX = 1;
            state.Scale.ScaleY = 1;
            var x = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(times[times.Length - 1]) };
            x.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            for (int i = 0; i < values.Length; i++)
                x.KeyFrames.Add(new EasingDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(times[i])), new CubicEase { EasingMode = EasingMode.EaseOut }));
            var y = x.Clone();
            y.Completed += delegate
            {
                if (state.Generation == generation)
                {
                    state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    state.Scale.ScaleX = 1;
                    state.Scale.ScaleY = 1;
                }
            };
            state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, x, HandoffBehavior.SnapshotAndReplace);
            state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, y, HandoffBehavior.SnapshotAndReplace);
        }

        private static void ResetScale(ScaleState state)
        {
            state.Generation++;
            state.Pressed = false;
            state.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            state.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            state.Scale.ScaleX = 1;
            state.Scale.ScaleY = 1;
        }

        internal static void Reveal(Window window)
        {
            var content = window.Content as FrameworkElement;
            if (content == null) return;
            var state = content.GetValue(RevealStateProperty) as RevealState;
            if (state == null)
            {
                state = new RevealState();
                content.SetValue(RevealStateProperty, state);
                content.IsVisibleChanged += delegate { if (!content.IsVisible) ResetReveal(content, state); };
                content.Unloaded += delegate { ResetReveal(content, state); };
            }
            ResetReveal(content, state);
            int generation = ++state.Generation;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fade.Completed += delegate { if (state.Generation == generation) ResetReveal(content, state); };
            content.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }

        private static void ResetReveal(FrameworkElement content, RevealState state)
        {
            state.Generation++;
            content.BeginAnimation(UIElement.OpacityProperty, null);
            content.Opacity = 1;
        }
    }
}
