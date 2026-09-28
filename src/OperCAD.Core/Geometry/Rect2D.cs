namespace OperCAD.Core.Geometry;

/// <summary>Retângulo alinhado aos eixos em coordenadas do mundo.</summary>
public readonly record struct Rect2D(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public bool Contains(Point2D p)
        => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public Rect2D Inflate(double amount)
        => new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);
}
