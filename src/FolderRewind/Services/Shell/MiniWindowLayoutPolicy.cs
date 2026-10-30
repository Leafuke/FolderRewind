using System;

namespace FolderRewind.Services
{
    internal static class MiniWindowMetrics
    {
        public const double CardSizeDip = 48d;
        public const double CommentCardWidthDip = 220d;
        public const double CardGapDip = 8d;
        public const double ExpandedWidthDip = CardSizeDip + CardGapDip + CommentCardWidthDip;
        public const double CornerRadiusDip = 12d;
        public const double WorkAreaMarginDip = 8d;
        public const double DragThresholdDip = 4d;
    }

    internal enum MiniWindowLayoutDirection
    {
        Left = 0,
        Right = 1,
    }

    internal readonly record struct MiniWindowPixelPoint(int X, int Y);

    internal readonly record struct MiniWindowPixelRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    internal readonly record struct MiniWindowLayoutResult(
        MiniWindowPixelRect WindowBounds,
        MiniWindowPixelRect AnchorBounds,
        MiniWindowLayoutDirection Direction);

    internal static class MiniWindowLayoutPolicy
    {
        public static int DipToPixels(double value, double scale)
        {
            var safeScale = scale > 0 && double.IsFinite(scale) ? scale : 1d;
            return Math.Max(1, (int)Math.Round(value * safeScale, MidpointRounding.AwayFromZero));
        }

        public static MiniWindowPixelRect GetCollapsedBounds(MiniWindowPixelPoint anchor, double scale)
        {
            var size = DipToPixels(MiniWindowMetrics.CardSizeDip, scale);
            return new MiniWindowPixelRect(anchor.X, anchor.Y, size, size);
        }

        public static MiniWindowLayoutResult GetExpandedBounds(
            MiniWindowPixelPoint anchor,
            MiniWindowPixelRect workArea,
            double scale,
            MiniWindowLayoutDirection preferredDirection)
        {
            var cardSize = DipToPixels(MiniWindowMetrics.CardSizeDip, scale);
            var expandedWidth = DipToPixels(MiniWindowMetrics.ExpandedWidthDip, scale);
            var margin = DipToPixels(MiniWindowMetrics.WorkAreaMarginDip, scale);

            var usableLeft = workArea.X + margin;
            var usableTop = workArea.Y + margin;
            var usableRight = workArea.Right - margin;
            var usableBottom = workArea.Bottom - margin;

            if (usableRight - usableLeft < cardSize)
            {
                usableLeft = workArea.X;
                usableRight = workArea.Right;
            }

            if (usableBottom - usableTop < cardSize)
            {
                usableTop = workArea.Y;
                usableBottom = workArea.Bottom;
            }

            var anchorX = Clamp(anchor.X, usableLeft, usableRight - cardSize);
            var anchorY = Clamp(anchor.Y, usableTop, usableBottom - cardSize);
            var rightX = anchorX;
            var leftX = anchorX - (expandedWidth - cardSize);
            var rightFits = rightX + expandedWidth <= usableRight;
            var leftFits = leftX >= usableLeft;

            MiniWindowLayoutDirection direction;
            if (preferredDirection == MiniWindowLayoutDirection.Right && rightFits
                || preferredDirection == MiniWindowLayoutDirection.Left && leftFits)
            {
                direction = preferredDirection;
            }
            else if (rightFits)
            {
                direction = MiniWindowLayoutDirection.Right;
            }
            else if (leftFits)
            {
                direction = MiniWindowLayoutDirection.Left;
            }
            else
            {
                var rightSpace = usableRight - (anchorX + cardSize);
                var leftSpace = anchorX - usableLeft;
                direction = rightSpace >= leftSpace
                    ? MiniWindowLayoutDirection.Right
                    : MiniWindowLayoutDirection.Left;
            }

            var requestedX = direction == MiniWindowLayoutDirection.Left ? leftX : rightX;
            var maxWindowX = usableRight - expandedWidth;
            var windowX = maxWindowX >= usableLeft
                ? Clamp(requestedX, usableLeft, maxWindowX)
                : workArea.X;
            var windowY = Clamp(anchorY, usableTop, usableBottom - cardSize);
            var resolvedAnchorX = direction == MiniWindowLayoutDirection.Left
                ? windowX + expandedWidth - cardSize
                : windowX;

            return new MiniWindowLayoutResult(
                new MiniWindowPixelRect(windowX, windowY, expandedWidth, cardSize),
                new MiniWindowPixelRect(resolvedAnchorX, windowY, cardSize, cardSize),
                direction);
        }

        public static MiniWindowPixelRect ClampCollapsedBounds(
            MiniWindowPixelPoint anchor,
            MiniWindowPixelRect workArea,
            double scale)
        {
            var size = DipToPixels(MiniWindowMetrics.CardSizeDip, scale);
            var margin = DipToPixels(MiniWindowMetrics.WorkAreaMarginDip, scale);
            var minX = workArea.X + margin;
            var minY = workArea.Y + margin;
            var maxX = workArea.Right - margin - size;
            var maxY = workArea.Bottom - margin - size;

            if (maxX < minX)
            {
                minX = workArea.X;
                maxX = Math.Max(workArea.X, workArea.Right - size);
            }

            if (maxY < minY)
            {
                minY = workArea.Y;
                maxY = Math.Max(workArea.Y, workArea.Bottom - size);
            }

            return new MiniWindowPixelRect(
                Clamp(anchor.X, minX, maxX),
                Clamp(anchor.Y, minY, maxY),
                size,
                size);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (maximum < minimum)
            {
                return minimum;
            }

            return Math.Clamp(value, minimum, maximum);
        }
    }
}
