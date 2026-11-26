using System;
using System.Windows;

namespace Orbit
{
    internal enum IdleEyeGesture { LookAround, PeekUpLeft, RollEyes }

    internal static class IdleEyeMotion
    {
        private const double MaximumRadius = 21.2;
        // The short reverse moves are intentional overshoots, followed by holds.
        private static readonly Frame[] Look = {
            new Frame(0, 0, 0), new Frame(.42, -19.4, 0), new Frame(.59, -17.5, .3),
            new Frame(.88, -17.5, .3), new Frame(1.34, 20.4, 1.5), new Frame(1.53, 18.4, 1.3),
            new Frame(1.83, 18.4, 1.3), new Frame(2.26, -19.8, -.3), new Frame(2.44, -18, .1),
            new Frame(2.76, -18, .1), new Frame(3.26, 1.2, 0), new Frame(3.48, 0, 0),
            new Frame(3.6, 0, 0)
        };
        private static readonly Frame[] Peek = {
            new Frame(0, 0, 0), new Frame(.48, -14.6, -14.2), new Frame(.68, -13.4, -13),
            new Frame(1.64, -13.4, -13), new Frame(2.18, .8, .4), new Frame(2.38, 0, 0),
            new Frame(2.5, 0, 0)
        };

        internal static IdleEyeGesture Choose(Random random)
        {
            if (random == null) throw new ArgumentNullException("random");
            int choice = random.Next(100);
            return choice < 68 ? IdleEyeGesture.LookAround : choice < 85 ? IdleEyeGesture.PeekUpLeft : IdleEyeGesture.RollEyes;
        }

        internal static double Duration(IdleEyeGesture gesture)
        {
            switch (gesture)
            {
                case IdleEyeGesture.LookAround: return 3.6;
                case IdleEyeGesture.PeekUpLeft: return 2.5;
                case IdleEyeGesture.RollEyes: return 2.9;
                default: throw new ArgumentOutOfRangeException("gesture");
            }
        }

        internal static Vector Sample(IdleEyeGesture gesture, double elapsedSeconds)
        {
            double duration = Duration(gesture);
            if (Double.IsNaN(elapsedSeconds) || elapsedSeconds <= 0 || elapsedSeconds >= duration) return new Vector();
            Vector value;
            if (gesture == IdleEyeGesture.RollEyes)
            {
                const double radius = 18.6, begin = .45, end = 2.45;
                if (elapsedSeconds < begin) value = new Vector(0, -radius * Ease(elapsedSeconds / begin));
                else if (elapsedSeconds > end) value = new Vector(0, -radius * (1 - Ease((elapsedSeconds - end) / (duration - end))));
                else
                {
                    double angle = -Math.PI / 2 + Math.PI * 2 * Ease((elapsedSeconds - begin) / (end - begin));
                    value = new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
                }
            }
            else value = Interpolate(gesture == IdleEyeGesture.LookAround ? Look : Peek, elapsedSeconds);
            double length = value.Length;
            return length > MaximumRadius ? value * (MaximumRadius / length) : value;
        }

        private static Vector Interpolate(Frame[] frames, double time)
        {
            for (int i = 1; i < frames.Length; i++)
            {
                if (time > frames[i].Time) continue;
                Frame a = frames[i - 1], b = frames[i];
                double progress = Ease((time - a.Time) / (b.Time - a.Time));
                return new Vector(a.X + (b.X - a.X) * progress, a.Y + (b.Y - a.Y) * progress);
            }
            return new Vector();
        }

        private static double Ease(double value) { return value * value * (3 - 2 * value); }
        private struct Frame
        {
            internal readonly double Time, X, Y;
            internal Frame(double time, double x, double y) { Time = time; X = x; Y = y; }
        }
    }
}
