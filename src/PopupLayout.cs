using System;
using System.Drawing;

namespace Orbit
{
    internal enum PopupSide { Below, Above }

    internal sealed class PopupRegion
    {
        internal PopupSide Side;
        // Top edge when Below; bottom edge when Above. All values are screen pixels.
        internal int AnchorY;
        internal int AvailableHeight;
    }

    /// <summary>Screen-space layout that never uses child content to move its anchor.</summary>
    internal static class PopupLayout
    {
        internal static Rectangle Toolbar(Rectangle dot, Rectangle work, Size bar)
        {
            Size size = FitSize(bar, work);
            int x = dot.Left + (dot.Width - size.Width) / 2;
            int y = dot.Bottom + size.Height <= work.Bottom ? dot.Bottom : dot.Top - size.Height;
            return Clamp(new Rectangle(x, y, size.Width, size.Height), work);
        }

        internal static PopupRegion ChooseRegion(Rectangle dot, Rectangle bar, Rectangle work,
            int minimumHeight, PopupSide? previous, bool anchorMoved, int hysteresis)
        {
            int belowAnchor = Math.Max(dot.Bottom, bar.Bottom) + 4;
            int aboveAnchor = Math.Min(dot.Top, bar.Top) - 4;
            int belowSpace = Math.Max(0, work.Bottom - Math.Max(work.Top, belowAnchor));
            int aboveSpace = Math.Max(0, Math.Min(work.Bottom, aboveAnchor) - work.Top);
            int minimum = Math.Max(0, minimumHeight);
            PopupSide side;

            if (!previous.HasValue)
            {
                side = PreferredSide(belowSpace, aboveSpace, minimum);
            }
            else if (previous.Value == PopupSide.Below)
            {
                // Child growth never flips a usable region. An anchor near the
                // bottom changes side only when the upper region is actually better.
                side = belowSpace < minimum && aboveSpace > belowSpace ? PopupSide.Above : PopupSide.Below;
            }
            else if (!anchorMoved && aboveSpace >= minimum)
            {
                side = PopupSide.Above;
            }
            else if (anchorMoved && belowSpace >= (long)minimum + Math.Max(0, hysteresis))
            {
                side = PopupSide.Below;
            }
            else if (aboveSpace >= minimum)
            {
                side = PopupSide.Above;
            }
            else
            {
                // A monitor/anchor move can remove the old region completely.
                // Never preserve an unusable side merely to satisfy hysteresis.
                side = PreferredSide(belowSpace, aboveSpace, minimum);
            }

            return new PopupRegion
            {
                Side = side,
                AnchorY = side == PopupSide.Below ? belowAnchor : aboveAnchor,
                AvailableHeight = side == PopupSide.Below ? belowSpace : aboveSpace
            };
        }

        internal static Rectangle Panel(Rectangle bar, Rectangle work, PopupRegion region, Size actualSize)
        {
            if (region == null) throw new ArgumentNullException("region");
            Size size = FitSize(actualSize, work);
            // Normally the caller already measures against AvailableHeight. Keep
            // the pure result inside the region if handed an oversized measurement.
            size.Height = Math.Min(size.Height, Math.Max(0, region.AvailableHeight));
            int x = bar.Left + (bar.Width - size.Width) / 2;
            int y = region.Side == PopupSide.Below ? region.AnchorY : region.AnchorY - size.Height;
            return Clamp(new Rectangle(x, y, size.Width, size.Height), work);
        }

        private static PopupSide PreferredSide(int below, int above, int minimum)
        {
            if (below >= minimum) return PopupSide.Below;
            if (above >= minimum) return PopupSide.Above;
            return above > below ? PopupSide.Above : PopupSide.Below;
        }

        private static Size FitSize(Size size, Rectangle work)
        {
            return new Size(Math.Min(Math.Max(0, size.Width), Math.Max(0, work.Width)),
                Math.Min(Math.Max(0, size.Height), Math.Max(0, work.Height)));
        }

        private static Rectangle Clamp(Rectangle rectangle, Rectangle work)
        {
            int x = Math.Max(work.Left, Math.Min(rectangle.Left, work.Right - rectangle.Width));
            int y = Math.Max(work.Top, Math.Min(rectangle.Top, work.Bottom - rectangle.Height));
            return new Rectangle(x, y, rectangle.Width, rectangle.Height);
        }
    }
}
