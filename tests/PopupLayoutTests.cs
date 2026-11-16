using System;
using System.Drawing;
using Orbit;

internal static class PopupLayoutTests
{
    private static int failures;

    private static int Main()
    {
        Run("toolbar anchor is independent of child height", ToolbarIndependentOfChildren);
        Run("initial placement prefers a usable lower region", InitialRegion);
        Run("stationary anchors retain the chosen side", StationarySide);
        Run("moving anchors apply hysteresis before returning below", MovingHysteresis);
        Run("bottom-edge tools open above without overlap", BottomEdge);
        Run("negative-coordinate monitor placement", NegativeMonitor);
        Run("400 varying screen positions stay bounded", BoundedPlacement);
        Console.WriteLine(failures == 0 ? "All 7 popup layout test groups passed." : failures + " popup layout test group(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    private static void ToolbarIndependentOfChildren()
    {
        var work = new Rectangle(0, 0, 1920, 1040);
        var dot = new Rectangle(920, 200, 76, 76);
        var barSize = new Size(228, 64);
        var expected = new Rectangle(844, 276, 228, 64);
        var bar = PopupLayout.Toolbar(dot, work, barSize);
        Equal(expected, bar, "initial toolbar location");
        var region = PopupLayout.ChooseRegion(dot, bar, work, 220, null, false, 32);
        foreach (int height in new[] { 80, 180, 360, 600 })
        {
            var panel = PopupLayout.Panel(bar, work, region, new Size(320, Math.Min(height, region.AvailableHeight)));
            Equal(expected, PopupLayout.Toolbar(dot, work, barSize), "child height " + height + " moved toolbar");
            Equal(region.AnchorY, panel.Top, "content growth changed lower anchor");
            Assert(panel.Top >= bar.Bottom + 4, "child overlaps toolbar");
        }
    }

    private static void InitialRegion()
    {
        var work = new Rectangle(0, 0, 1000, 800);
        var dot = new Rectangle(450, 100, 76, 76);
        var bar = PopupLayout.Toolbar(dot, work, new Size(228, 64));
        var region = PopupLayout.ChooseRegion(dot, bar, work, 240, null, false, 32);
        Equal(PopupSide.Below, region.Side, "preferred side");
        Equal(244, region.AnchorY, "lower anchor");
        Equal(556, region.AvailableHeight, "lower available height");

        var compactWork = new Rectangle(0, 0, 1000, 300);
        var compactDot = new Rectangle(450, 60, 76, 76);
        var compactBar = PopupLayout.Toolbar(compactDot, compactWork, new Size(228, 64));
        var compact = PopupLayout.ChooseRegion(compactDot, compactBar, compactWork, 220, null, false, 32);
        Equal(PopupSide.Below, compact.Side, "neither side fits: choose more space");
        Equal(96, compact.AvailableHeight, "available height on compact screen");
    }

    private static void StationarySide()
    {
        var work = new Rectangle(0, 0, 1000, 1000);
        var dot = new Rectangle(450, 400, 76, 76);
        var bar = PopupLayout.Toolbar(dot, work, new Size(228, 64));
        var above = PopupLayout.ChooseRegion(dot, bar, work, 240, PopupSide.Above, false, 32);
        var below = PopupLayout.ChooseRegion(dot, bar, work, 240, PopupSide.Below, false, 32);
        Equal(PopupSide.Above, above.Side, "existing upper side was discarded");
        Equal(PopupSide.Below, below.Side, "existing lower side was discarded");
        foreach (int height in new[] { 120, 220, 360 })
        {
            var panel = PopupLayout.Panel(bar, work, above, new Size(320, height));
            Equal(396, panel.Bottom, "upper anchor changes when content grows");
            Equal(PopupSide.Above, PopupLayout.ChooseRegion(dot, bar, work, 240, above.Side, false, 32).Side, "child growth flips region");
        }
    }

    private static void MovingHysteresis()
    {
        var work = new Rectangle(0, 0, 1000, 1000);
        var almost = new Rectangle(450, 585, 76, 76); // 271 px below, threshold is 272.
        var bar = PopupLayout.Toolbar(almost, work, new Size(228, 64));
        Equal(PopupSide.Above, PopupLayout.ChooseRegion(almost, bar, work, 240, PopupSide.Above, true, 32).Side, "switched below before hysteresis threshold");
        var threshold = new Rectangle(450, 584, 76, 76);
        bar = PopupLayout.Toolbar(threshold, work, new Size(228, 64));
        Equal(PopupSide.Below, PopupLayout.ChooseRegion(threshold, bar, work, 240, PopupSide.Above, true, 32).Side, "did not return below at threshold");
        var low = new Rectangle(450, 620, 76, 76);
        bar = PopupLayout.Toolbar(low, work, new Size(228, 64));
        Equal(PopupSide.Above, PopupLayout.ChooseRegion(low, bar, work, 240, PopupSide.Below, true, 32).Side, "insufficient lower region did not switch upward");
        var exact = new Rectangle(450, 616, 76, 76);
        bar = PopupLayout.Toolbar(exact, work, new Size(228, 64));
        Equal(PopupSide.Below, PopupLayout.ChooseRegion(exact, bar, work, 240, PopupSide.Below, true, 32).Side, "usable lower region switched at minimum boundary");
        var shallowWork = new Rectangle(0, 0, 1000, 400);
        var top = new Rectangle(450, 0, 76, 76);
        bar = PopupLayout.Toolbar(top, shallowWork, new Size(228, 64));
        Equal(PopupSide.Below, PopupLayout.ChooseRegion(top, bar, shallowWork, 240, PopupSide.Above, true, 32).Side, "hysteresis retained an upper region with no space");
    }

    private static void BottomEdge()
    {
        var work = new Rectangle(0, 0, 1000, 1000);
        var dot = new Rectangle(450, 940, 60, 60);
        var bar = PopupLayout.Toolbar(dot, work, new Size(228, 64));
        Equal(876, bar.Top, "toolbar should fit above bottom-edge dot");
        var region = PopupLayout.ChooseRegion(dot, bar, work, 220, null, false, 32);
        Equal(PopupSide.Above, region.Side, "bottom-edge side");
        Equal(872, region.AnchorY, "upper child anchor");
        var panel = PopupLayout.Panel(bar, work, region, new Size(320, 400));
        Equal(872, panel.Bottom, "card bottom should stay attached above toolbar");
        Assert(panel.Bottom <= bar.Top - 4, "upper card overlaps toolbar");
        Inside(panel, work, "bottom-edge card");
    }

    private static void NegativeMonitor()
    {
        var work = new Rectangle(-1920, -200, 1920, 1040);
        var dot = new Rectangle(-1880, 10, 76, 76);
        var bar = PopupLayout.Toolbar(dot, work, new Size(228, 64));
        Equal(-1920, bar.Left, "left edge should clamp in negative-coordinate monitor");
        var region = PopupLayout.ChooseRegion(dot, bar, work, 220, null, false, 32);
        var panel = PopupLayout.Panel(bar, work, region, new Size(320, 300));
        Equal(-1920, panel.Left, "negative-coordinate card clamp");
        Inside(bar, work, "negative-coordinate toolbar");
        Inside(panel, work, "negative-coordinate card");

        dot = new Rectangle(-86, 600, 76, 76);
        bar = PopupLayout.Toolbar(dot, work, new Size(228, 64));
        region = PopupLayout.ChooseRegion(dot, bar, work, 220, null, false, 32);
        panel = PopupLayout.Panel(bar, work, region, new Size(320, 250));
        Equal(0, panel.Right, "right edge of negative-coordinate monitor");
        Inside(panel, work, "negative-coordinate right-edge card");
    }

    private static void BoundedPlacement()
    {
        var screens = new[] { new Rectangle(0, 0, 1280, 720), new Rectangle(-1920, 0, 1920, 1080), new Rectangle(2560, -480, 1600, 900), new Rectangle(0, 0, 800, 600) };
        var random = new Random(713);
        for (int i = 0; i < 400; i++)
        {
            var work = screens[i % screens.Length];
            var dot = new Rectangle(work.Left + random.Next(work.Width - 75), work.Top + random.Next(work.Height - 75), 76, 76);
            var bar = PopupLayout.Toolbar(dot, work, new Size(268, 68));
            PopupSide? previous = i % 3 == 0 ? (PopupSide?)null : i % 3 == 1 ? PopupSide.Above : PopupSide.Below;
            var region = PopupLayout.ChooseRegion(dot, bar, work, 220, previous, i % 2 == 0, 32);
            var size = new Size(320 + random.Next(80), Math.Min(region.AvailableHeight, 80 + random.Next(480)));
            var panel = PopupLayout.Panel(bar, work, region, size);
            Inside(bar, work, "toolbar #" + i);
            Inside(panel, work, "panel #" + i);
            Equal(size, panel.Size, "valid measured size changed #" + i);
            Assert(region.AvailableHeight >= 0, "negative region #" + i);
            if (panel.Height > 0)
            {
                if (region.Side == PopupSide.Below) Equal(region.AnchorY, panel.Top, "lower anchor drift #" + i);
                else Equal(region.AnchorY, panel.Bottom, "upper anchor drift #" + i);
            }
        }
    }

    private static void Inside(Rectangle child, Rectangle parent, string message)
    {
        Assert(child.Left >= parent.Left && child.Top >= parent.Top && child.Right <= parent.Right && child.Bottom <= parent.Bottom, message + " is outside working area");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!Object.Equals(expected, actual)) throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
