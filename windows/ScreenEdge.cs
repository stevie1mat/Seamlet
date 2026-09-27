using System.Drawing;

namespace MultipleMouse;

static class ScreenEdge
{
    public static bool Crossed(Rectangle screen, bool right, Point previous, Point current)
    {
        if (current.Y < screen.Top || current.Y >= screen.Bottom) return false;
        // A fast physical sample can jump past the final pixel. Requiring exact
        // equality made entry wait for a later sample that happened to land on it.
        if (!screen.Contains(previous) && !screen.Contains(current)) return false;
        return right ? current.X >= screen.Right - 1 : current.X <= screen.Left;
    }
}
