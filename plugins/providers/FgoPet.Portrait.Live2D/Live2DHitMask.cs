using System.Windows;
using FgoPet.Core.Geometry;

namespace FgoPet.App.Portraits.Live2D;

internal sealed record Live2DHitMask(int Columns, int Rows, byte[] Bits)
{
    public static Live2DHitMask? Parse(int columns, int rows, string? encoded)
    {
        if (columns != 64 || rows != 96 || encoded is null || encoded.Length > 2048)
        {
            return null;
        }
        try
        {
            var bits = Convert.FromBase64String(encoded);
            return bits.Length == (columns * rows + 7) / 8 ? new Live2DHitMask(columns, rows, bits) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool IsHit(Point point, PortraitGeometry geometry)
    {
        if (point.X < 0 || point.Y < 0 || point.X >= geometry.LogicalSize.Width || point.Y >= geometry.LogicalSize.Height)
        {
            return false;
        }
        var x = Math.Min(Columns - 1, (int)(point.X * Columns / geometry.LogicalSize.Width));
        var y = Math.Min(Rows - 1, (int)(point.Y * Rows / geometry.LogicalSize.Height));
        var index = y * Columns + x;
        return (Bits[index >> 3] & (1 << (index & 7))) != 0;
    }
}
