namespace OperCAD.Core.Geometry;

/// <summary>Ponto em coordenadas do mundo (milímetros), com eixo Y para cima.</summary>
public readonly record struct Point2D(double X, double Y)
{
    public static Point2D Origin => new(0, 0);

    public static Point2D operator +(Point2D a, Vector2D b) => new(a.X + b.Dx, a.Y + b.Dy);
    public static Point2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);

    public double DistanceTo(Point2D other)
        => Math.Sqrt((other.X - X) * (other.X - X) + (other.Y - Y) * (other.Y - Y));
}

/// <summary>Vetor de deslocamento no plano do mundo.</summary>
public readonly record struct Vector2D(double Dx, double Dy);
