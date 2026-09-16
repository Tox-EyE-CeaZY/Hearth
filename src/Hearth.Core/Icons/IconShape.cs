using System.Windows;
using System.Windows.Media;

namespace Hearth.Core.Icons;

/// <summary>
/// The silhouette every icon gets masked into. Matching Android's set on
/// purpose — this is the single setting that does most of the work of making a
/// pile of mismatched Windows icons read as one coherent grid.
/// </summary>
public enum IconShapeKind
{
    Squircle,
    Circle,
    RoundedSquare,
    Square,
    Teardrop,
}

/// <summary>
/// Builds the mask geometry for a given shape at a given size.
///
/// Geometries are frozen and cached per (shape, size): the grid asks for the
/// same handful of sizes thousands of times, and an unfrozen Geometry would
/// re-tessellate on every render.
/// </summary>
public static class IconShape
{
    private static readonly Dictionary<(IconShapeKind, int), Geometry> Cache = [];
    private static readonly object CacheLock = new();

    public static Geometry Get(IconShapeKind kind, double size)
    {
        var key = (kind, (int)Math.Round(size));
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var geometry = Build(kind, key.Item2);
            geometry.Freeze();
            Cache[key] = geometry;
            return geometry;
        }
    }

    private static Geometry Build(IconShapeKind kind, double s) => kind switch
    {
        IconShapeKind.Circle => new EllipseGeometry(new Rect(0, 0, s, s)),
        IconShapeKind.Square => new RectangleGeometry(new Rect(0, 0, s, s)),
        IconShapeKind.RoundedSquare => new RectangleGeometry(new Rect(0, 0, s, s), s * 0.22, s * 0.22),
        IconShapeKind.Teardrop => Teardrop(s),
        _ => Superellipse(s, exponent: 4.2),
    };

    /// <summary>
    /// A true superellipse (|x|^n + |y|^n = 1), not the four-arcs-and-a-prayer
    /// approximation. n = 4.2 lands close to the iOS/Android squircle: visibly
    /// rounder than a rounded rect, with no flat spot where arc meets edge.
    /// </summary>
    private static Geometry Superellipse(double s, double exponent)
    {
        const int segments = 192; // smooth past 256 px; costs nothing when frozen
        var r = s / 2.0;
        var power = 2.0 / exponent;
        var points = new PointCollection(segments);

        for (var i = 1; i < segments; i++)
        {
            var t = 2 * Math.PI * i / segments;
            var cos = Math.Cos(t);
            var sin = Math.Sin(t);
            var x = r + r * Math.Sign(cos) * Math.Pow(Math.Abs(cos), power);
            var y = r + r * Math.Sign(sin) * Math.Pow(Math.Abs(sin), power);
            points.Add(new Point(x, y));
        }

        var figure = new PathFigure
        {
            StartPoint = new Point(s, r), // t = 0
            IsClosed = true,
            IsFilled = true,
        };
        figure.Segments.Add(new PolyLineSegment(points, isStroked: false));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>Three round corners and one tight one, pointing bottom-right.</summary>
    private static Geometry Teardrop(double s)
    {
        var big = s * 0.5;
        var small = s * 0.12;
        return RoundedRect(s, topLeft: big, topRight: big, bottomRight: small, bottomLeft: big);
    }

    private static Geometry RoundedRect(double s, double topLeft, double topRight, double bottomRight, double bottomLeft)
    {
        var figure = new PathFigure { StartPoint = new Point(topLeft, 0), IsClosed = true, IsFilled = true };

        figure.Segments.Add(new LineSegment(new Point(s - topRight, 0), false));
        figure.Segments.Add(Arc(new Point(s, topRight), topRight));

        figure.Segments.Add(new LineSegment(new Point(s, s - bottomRight), false));
        figure.Segments.Add(Arc(new Point(s - bottomRight, s), bottomRight));

        figure.Segments.Add(new LineSegment(new Point(bottomLeft, s), false));
        figure.Segments.Add(Arc(new Point(0, s - bottomLeft), bottomLeft));

        figure.Segments.Add(new LineSegment(new Point(0, topLeft), false));
        figure.Segments.Add(Arc(new Point(topLeft, 0), topLeft));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;

        static ArcSegment Arc(Point to, double radius) => new(
            to, new Size(radius, radius), 0, false, SweepDirection.Clockwise, isStroked: false);
    }

    /// <summary>
    /// Fraction of the tile a foreground logo may occupy when we generate a
    /// background for it. Mirrors Android's adaptive-icon safe zone (66/108):
    /// large enough to read, small enough that no shape clips the artwork.
    /// </summary>
    public const double SafeZoneScale = 66.0 / 108.0;
}
