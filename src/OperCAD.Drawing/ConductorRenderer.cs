using OperCAD.Core.Documents;
using OperCAD.Core.Elements;
using OperCAD.Core.Geometry;
using SkiaSharp;

namespace OperCAD.Drawing;

/// <summary>Desenha condutores (polilinha ortogonal) na folha.</summary>
public static class ConductorRenderer
{
    /// <summary>
    /// Desenha um condutor (polilinha ortogonal) na folha.
    /// <paramref name="fromSnap"/>/<paramref name="toSnap"/> são posições (mundo) EXATAS
    /// dos terminais conectados (quando a ponta do condutor é um terminal): o traço é
    /// desenhado encostando nessas posições, eliminando a "abertura" entre o condutor e
    /// o terminal mesmo quando o Path salvo ficou ligeiramente deslocado.
    /// <paramref name="jumps"/> são os pontos de jump deste condutor (cruzamentos sem
    /// conexão): nos pontos de jump, desenha APENAS o arco (sem a linha reta).
    /// </summary>
    public static void RenderConductor(SKCanvas canvas, Conductor conductor, ViewTransform view,
        bool forceBlack = false, Point2D? fromSnap = null, Point2D? toSnap = null,
        List<JumpInfo>? jumps = null)
    {
        if (conductor.Path.Count < 2)
            return;

        var color = conductor.IsSelected ? SKColors.Red
            : forceBlack ? SKColors.Black
            : ParseColor(conductor.Color);

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = color,
            // ConductorRenderer desenha em espaço de tela (pontos mapeados), então a
            // espessura em mm precisa ser convertida para pixels pela escala da vista.
            // Usa GetVisualWidth() para determinar a espessura correta baseada na origem.
            StrokeWidth = (float)Math.Max(conductor.GetVisualWidth() * view.Scale, 0.6),
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        // Se não há jumps, desenha o caminho completo (comportamento original)
        if (jumps is null || jumps.Count == 0)
        {
            using var path = new SKPath();
            var first = view.WorldToScreen(fromSnap ?? conductor.Path[0]);
            path.MoveTo((float)first.X, (float)first.Y);
            for (var i = 1; i < conductor.Path.Count - 1; i++)
            {
                var p = view.WorldToScreen(conductor.Path[i]);
                path.LineTo((float)p.X, (float)p.Y);
            }
            var last = view.WorldToScreen(toSnap ?? conductor.Path[^1]);
            path.LineTo((float)last.X, (float)last.Y);
            canvas.DrawPath(path, paint);
        }
        else
        {
            // Desenha a linha em trechos entre jumps (sem passar pelos pontos de jump)
            DrawConductorWithJumps(canvas, conductor, view, paint, color, jumps, fromSnap, toSnap);
        }

        // Número do condutor (autonumeração): desenhado no meio do maior segmento,
        // como o desenho do condutor do padrão CAD (numsize/displaytext/text_color respeitados).
        if (conductor.DisplayText && !string.IsNullOrEmpty(conductor.Text))
        {
            RenderText(canvas, conductor, view);
            RenderEndpointLabels(canvas, conductor, view);
        }
    }

    /// <summary>
    /// Desenha o condutor interrompendo a linha reta nos pontos de jump.
    /// Em cada trecho entre jumps, desenha a linha reta. Nos pontos de jump, desenha APENAS o arco.
    /// </summary>
    private static void DrawConductorWithJumps(SKCanvas canvas, Conductor conductor, ViewTransform view,
        SKPaint paint, SKColor color, List<JumpInfo> jumps, Point2D? fromSnap, Point2D? toSnap)
    {
        // Agrupa jumps por segmento
        var jumpsBySegment = new Dictionary<int, List<JumpInfo>>();
        foreach (var j in jumps)
        {
            if (!jumpsBySegment.TryGetValue(j.SegmentIndex, out var list))
            {
                list = new List<JumpInfo>();
                jumpsBySegment[j.SegmentIndex] = list;
            }
            list.Add(j);
        }

        // Para cada segmento do condutor
        for (int pi = 0; pi + 1 < conductor.Path.Count; pi++)
        {
            var segStart = conductor.Path[pi];
            var segEnd = conductor.Path[pi + 1];

            // Pontos do segmento em tela
            var sStart = view.WorldToScreen(pi == 0 ? (fromSnap ?? segStart) : segStart);
            var sEnd = view.WorldToScreen(pi == conductor.Path.Count - 2 ? (toSnap ?? segEnd) : segEnd);

            if (!jumpsBySegment.TryGetValue(pi, out var segJumps) || segJumps.Count == 0)
            {
                // Sem jumps neste segmento: desenha linha reta completa
                using var segPath = new SKPath();
                segPath.MoveTo((float)sStart.X, (float)sStart.Y);
                segPath.LineTo((float)sEnd.X, (float)sEnd.Y);
                canvas.DrawPath(segPath, paint);
            }
            else
            {
                // Tem jumps: desenha trechos da linha entre os jumps,
                // ENCURTANDO cada trecho para não invadir o arco.
                // O arco é um semicírculo centrado no ponto de interseção com raio r.
                // As pontas do arco ficam a ±r ao longo da direção do condutor,
                // então a linha reta deve parar em jump.T ± dt, onde
                // dt = r / comprimentoDoSegmentoEmTela.
                segJumps.Sort((a, b) => a.T.CompareTo(b.T));

                float arcRadiusScreen = (float)(JumpArcRadiusMm * ElementDefinition.GlobalScale * view.Scale);
                float segDx = (float)(sEnd.X - sStart.X);
                float segDy = (float)(sEnd.Y - sStart.Y);
                float screenLen = MathF.Sqrt(segDx * segDx + segDy * segDy);
                float dt = screenLen > 1e-3f ? arcRadiusScreen / screenLen : 0f;

                double currentT = 0.0;
                foreach (var jump in segJumps)
                {
                    // Linha antes do jump: de currentT até (jump.T - dt)
                    double lineEndT = Math.Max(jump.T - dt, currentT);
                    if (lineEndT - currentT > 1e-6)
                    {
                        var p1 = Lerp(segStart, segEnd, currentT);
                        var p2 = Lerp(segStart, segEnd, lineEndT);
                        var sp1 = view.WorldToScreen(p1);
                        var sp2 = view.WorldToScreen(p2);
                        using var segPath = new SKPath();
                        segPath.MoveTo((float)sp1.X, (float)sp1.Y);
                        segPath.LineTo((float)sp2.X, (float)sp2.Y);
                        canvas.DrawPath(segPath, paint);
                    }

                    // Desenha o arco no ponto de jump
                    DrawJumpArc(canvas, view,
                        new SegInfo(segStart, segEnd, 0),
                        jump.Position,
                        color,
                        (float)(conductor.GetVisualWidth() * view.Scale));

                    // Retoma a linha depois do arco: (jump.T + dt) em diante
                    currentT = Math.Min(jump.T + dt, 1.0);
                }

                // Desenha linha do último jump até o fim do segmento
                if (1.0 - currentT > 1e-6)
                {
                    var p1 = Lerp(segStart, segEnd, currentT);
                    var sp1 = view.WorldToScreen(p1);
                    using var segPath = new SKPath();
                    segPath.MoveTo((float)sp1.X, (float)sp1.Y);
                    segPath.LineTo((float)sEnd.X, (float)sEnd.Y);
                    canvas.DrawPath(segPath, paint);
                }
            }
        }
    }

    /// <summary>Interpola linearmente entre dois pontos.</summary>
    private static Point2D Lerp(Point2D a, Point2D b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static void RenderText(SKCanvas canvas, Conductor conductor, ViewTransform view)
    {
        // Maior segmento do caminho
        var mid = MidOfLongestSegment(conductor.Path);
        if (mid is null)
            return;
        var screen = view.WorldToScreen(mid.Value);

        var textSizeMm = conductor.NumSize * 25.4 / 72.0; // numsize em pt
        using var textPaint = new SKPaint
        {
            Color = ParseColor(conductor.TextColor),
            TextSize = (float)(textSizeMm * view.Scale),
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe UI")
        };
        using var halo = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            TextSize = textPaint.TextSize,
            IsAntialias = true,
            Typeface = textPaint.Typeface
        };
        var text = conductor.Text;
        // Ligeiro deslocamento acima da linha (metade da altura do texto)
        var y = (float)screen.Y - textPaint.TextSize * 0.75f;
        canvas.DrawText(text, (float)screen.X, y, halo);
        canvas.DrawText(text, (float)screen.X, y, textPaint);
    }

    private static Point2D? MidOfLongestSegment(List<Point2D> path)
    {
        Point2D? best = null;
        var bestLen = 0.0;
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            var len = a.DistanceTo(b);
            if (len > bestLen)
            {
                bestLen = len;
                best = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            }
        }
        return best;
    }

    /// <summary>Desenha rótulos nas pontas do condutor (primeira e última).</summary>
    private static void RenderEndpointLabels(SKCanvas canvas, Conductor conductor, ViewTransform view)
    {
        if (conductor.Path.Count < 2)
            return;

        var textSizeMm = conductor.NumSize * 25.4 / 72.0;
        using var textPaint = new SKPaint
        {
            Color = ParseColor(conductor.TextColor),
            TextSize = (float)(textSizeMm * view.Scale),
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe UI")
        };
        using var halo = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            TextSize = textPaint.TextSize,
            IsAntialias = true,
            Typeface = textPaint.Typeface
        };

        var text = conductor.Text;
        var first = view.WorldToScreen(conductor.Path[0]);
        var last = view.WorldToScreen(conductor.Path[^1]);

        var offset = textPaint.TextSize * 0.75f;
        canvas.DrawText(text, (float)first.X, (float)first.Y - offset, halo);
        canvas.DrawText(text, (float)first.X, (float)first.Y - offset, textPaint);
        canvas.DrawText(text, (float)last.X, (float)last.Y + textPaint.TextSize + offset * 0.5f, halo);
        canvas.DrawText(text, (float)last.X, (float)last.Y + textPaint.TextSize + offset * 0.5f, textPaint);
    }

    /// <summary>Tolerância (mm) para considerar uma ponta de segmento "no" ponto de interseção (junção T).</summary>
    private const double JunctionTolerance = 0.01;

    /// <summary>Raio (mm, mundo) da bolinha de junção (junção T / vértice compartilhado). Deve ser igual ao raio dos terminais dos elementos para manter homogeneidade visual.</summary>
    private const double JunctionRadiusMm = 0.6;

    /// <summary>Raio (mm, mundo) do arco de pulo nos cruzamentos SEM conexão (formato "C" suave).
    /// Valor reduzido para um salto sutil e profissional, apenas o suficiente para
    /// subir, saltar sobre o condutor cruzado e aterrissar alinhado à linha do cruzador.</summary>
    private const double JumpArcRadiusMm = 1.0;

    /// <summary>Segmento (par de pontos) + índice do condutor dono.</summary>
    internal readonly record struct SegInfo(Point2D A, Point2D B, int Conductor);

    /// <summary>Informação de um ponto de jump (cruzamento sem conexão) no condutor.</summary>
    public readonly record struct JumpInfo(Point2D Position, int SegmentIndex, double T);

    /// <summary>Extrai todos os segmentos de todos os condutores (com o índice do condutor dono).</summary>
    internal static List<SegInfo> BuildSegments(IReadOnlyList<Conductor> conductors)
    {
        var segments = new List<SegInfo>();
        for (int ci = 0; ci < conductors.Count; ci++)
        {
            var path = conductors[ci].Path;
            for (int pi = 0; pi + 1 < path.Count; pi++)
                segments.Add(new SegInfo(path[pi], path[pi + 1], ci));
        }
        return segments;
    }

    /// <summary>
    /// Encontra todos os pontos de jump para um condutor específico.
    /// Um jump ocorre quando o condutor cruza outro condutor (sem conexão T).
    /// Retorna a lista de jumps ordenada pela posição ao longo do segmento.
    /// </summary>
    internal static List<JumpInfo> FindJumpsForConductor(
        int conductorIndex, IReadOnlyList<Conductor> conductors, List<SegInfo> allSegments)
    {
        var jumps = new List<JumpInfo>();
        var conductor = conductors[conductorIndex];

        // Para cada segmento deste condutor
        for (int pi = 0; pi + 1 < conductor.Path.Count; pi++)
        {
            var segA = conductor.Path[pi];
            var segB = conductor.Path[pi + 1];

            // Verifica cruzamento com todos os outros segmentos
            foreach (var other in allSegments)
            {
                // Pula segmentos do mesmo condutor
                if (other.Conductor == conductorIndex)
                    continue;

                // Verifica interseção
                if (!TryLineIntersection(segA, segB, other.A, other.B, out var hit))
                    continue;

                // Junção T real não é jump (tem bolinha)
                var teA = new SegInfo(segA, segB, conductorIndex);
                var teB = other;
                if (EndpointAt(teA, hit) || EndpointAt(teB, hit))
                    continue;

                // Só o condutor "por cima" (índice maior) pula neste cruzamento.
                // O condutor cruzado NÃO recebe jump: permanece contínuo, passando
                // inteiro por baixo do arco de pulo do cruzador.
                if (conductorIndex < other.Conductor)
                    continue;

                // Calcula a posição relativa T no segmento (0.0 a 1.0)
                var dx = segB.X - segA.X;
                var dy = segB.Y - segA.Y;
                var lenSq = dx * dx + dy * dy;
                double t = lenSq < 1e-9 ? 0 : ((hit.X - segA.X) * dx + (hit.Y - segA.Y) * dy) / lenSq;

                jumps.Add(new JumpInfo(hit, pi, t));
            }
        }

        // Ordena por índice do segmento, depois por T
        jumps.Sort((a, b) => a.SegmentIndex != b.SegmentIndex
            ? a.SegmentIndex.CompareTo(b.SegmentIndex)
            : a.T.CompareTo(b.T));

        return jumps;
    }

    /// <summary>
    /// Desenha as BOLINHAS de junção: vértice compartilhado por condutores diferentes
    /// (preta) e junção T real — ponta de um condutor sobre o corpo de outro (bolinha na
    /// cor do condutor derivado). Cruzamentos puros (sem conexão) são desenhados como
    /// arco de pulo em <see cref="RenderJumpArcs"/>.
    /// </summary>
    public static void RenderJunctions(SKCanvas canvas, IReadOnlyList<Conductor> conductors, ViewTransform view)
    {
        if (conductors.Count == 0)
            return;

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = SKColors.Black,
            IsAntialias = true
        };
        var radius = (float)(JunctionRadiusMm * ElementDefinition.GlobalScale * view.Scale);

        var pointIndexMap = new Dictionary<(double, double), int>();
        var allPoints = new List<Point2D>();

        int IndexOf(double x, double y)
        {
            var key = (Math.Round(x, 4), Math.Round(y, 4));
            if (pointIndexMap.TryGetValue(key, out var idx))
                return idx;
            idx = allPoints.Count;
            allPoints.Add(new Point2D(x, y));
            pointIndexMap[key] = idx;
            return idx;
        }

        var segEndpoints = new List<(int pointIdx, int conductorIdx)>();

        for (int ci = 0; ci < conductors.Count; ci++)
        {
            var path = conductors[ci].Path;
            for (int pi = 0; pi < path.Count; pi++)
            {
                var idx = IndexOf(path[pi].X, path[pi].Y);
                segEndpoints.Add((idx, ci));
            }
        }

        var pointToConductors = new Dictionary<int, HashSet<int>>();
        var pointSegmentCount = new Dictionary<int, int>();
        foreach (var (pointIdx, conductorIdx) in segEndpoints)
        {
            if (!pointToConductors.TryGetValue(pointIdx, out var set))
            {
                set = new HashSet<int>();
                pointToConductors[pointIdx] = set;
            }
            set.Add(conductorIdx);
            pointSegmentCount.TryGetValue(pointIdx, out var cnt);
            pointSegmentCount[pointIdx] = cnt + 1;
        }

        foreach (var (pointIdx, conductorSet) in pointToConductors)
        {
            var segCount = pointSegmentCount[pointIdx];
            bool sharedByDifferent = conductorSet.Count >= 2;
            bool manySegments = segCount >= 3;
            if (sharedByDifferent || manySegments)
            {
                var pt = allPoints[pointIdx];
                var sp = view.WorldToScreen(pt);
                canvas.DrawCircle((float)sp.X, (float)sp.Y, radius, paint);
            }
        }

        var segments = BuildSegments(conductors);
        for (int i = 0; i < segments.Count; i++)
        {
            for (int j = i + 1; j < segments.Count; j++)
            {
                if (segments[i].Conductor == segments[j].Conductor)
                    continue;

                if (!TryLineIntersection(segments[i].A, segments[i].B, segments[j].A, segments[j].B, out var hit))
                    continue;

                // JUNÇÃO T REAL: um dos condutores TERMINA exatamente no cruzamento
                // (a ponta do segmento coincide com o ponto de interseção) → bolinha.
                var teA = EndpointAt(segments[i], hit);
                var teB = EndpointAt(segments[j], hit);
                if (teA || teB)
                {
                    var colorIdx = teA == teB ? -1 : (teA ? segments[i].Conductor : segments[j].Conductor);
                    using var dot = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = colorIdx >= 0 ? ParseColor(conductors[colorIdx].Color) : SKColors.Black,
                        IsAntialias = true
                    };
                    var sp = view.WorldToScreen(hit);
                    canvas.DrawCircle((float)sp.X, (float)sp.Y, radius, dot);
                }
            }
        }
    }

    /// <summary>
    /// Desenha os ARCOS DE PULO nos cruzamentos puros (dois condutores que apenas se
    /// cruzam, sem conexão elétrica): o condutor desenhado por cima (índice maior)
    /// "pula" sobre o outro com um semicírculo pequeno — em vez de marcar um ponto de
    /// conexão como nas junções T. As junções T reais ficam em <see cref="RenderJunctions"/>.
    /// </summary>
    public static void RenderJumpArcs(SKCanvas canvas, IReadOnlyList<Conductor> conductors, ViewTransform view)
    {
        if (conductors.Count == 0)
            return;

        var segments = BuildSegments(conductors);
        for (int i = 0; i < segments.Count; i++)
        {
            for (int j = i + 1; j < segments.Count; j++)
            {
                if (segments[i].Conductor == segments[j].Conductor)
                    continue;

                if (!TryLineIntersection(segments[i].A, segments[i].B, segments[j].A, segments[j].B, out var hit))
                    continue;

                // Junção T real não entra aqui (tem bolinha no RenderJunctions).
                if (EndpointAt(segments[i], hit) || EndpointAt(segments[j], hit))
                    continue;

                // Cruzamento puro: o condutor por cima (índice maior) dá o pulo.
                var jumping = segments[i].Conductor >= segments[j].Conductor ? i : j;
                DrawJumpArc(canvas, view, segments[jumping], hit, conductors);
            }
        }
    }

    /// <summary>True quando uma das pontas do segmento está (quase) no ponto <paramref name="p"/>.</summary>
    private static bool EndpointAt(SegInfo seg, Point2D p)
        => seg.A.DistanceTo(p) <= JunctionTolerance || seg.B.DistanceTo(p) <= JunctionTolerance;

    /// <summary>
    /// Desenha o arco de pulo em formato "C": um semicírculo suave no cruzamento onde o
    /// condutor passa POR CIMA do outro (sem conexão elétrica).
    /// O arco tem um pequeno respiro (1px) entre ele e o condutor cruzado, denotando
    /// que um passa por cima e o outro por baixo.
    /// </summary>
    private static void DrawJumpArc(SKCanvas canvas, ViewTransform view,
        SegInfo seg, Point2D hit, SKColor color, float width)
    {
        // Direção do segmento que pula, em espaço de tela (Y-down).
        var sA = view.WorldToScreen(seg.A);
        var sB = view.WorldToScreen(seg.B);
        float dx = (float)(sB.X - sA.X);
        float dy = (float)(sB.Y - sA.Y);
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f)
            return;
        dx /= len;
        dy /= len;

        var sp = view.WorldToScreen(hit);
        float r = (float)(JumpArcRadiusMm * ElementDefinition.GlobalScale * view.Scale);
        float jumpWidth = Math.Max(width, 0.6f);

        // Perpendicular (sentido do pulo). Semicírculo com início na extremidade oposta.
        float px = -dy, py = dx;
        float thetaP = (float)(Math.Atan2(py, px) * 180.0 / Math.PI);
        float start = thetaP - 90f;
        var rect = SKRect.Create((float)sp.X - r, (float)sp.Y - r, 2 * r, 2 * r);

        using var arcPath = new SKPath();
        arcPath.AddArc(rect, start, 180f);

        // 1) Linha de respiro branca muito fina (1px) entre o arco e o condutor cruzado.
        float gapWidth = Math.Max(1.0f, (float)(0.15 * view.Scale));
        using var gap = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.White,
            StrokeWidth = gapWidth,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawPath(arcPath, gap);

        // 2) Arco do pulo em "C" (mesma cor/espessura do condutor que pula).
        using var arc = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = color,
            StrokeWidth = jumpWidth,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawPath(arcPath, arc);
    }

    /// <summary>Sobrecarga para uso no RenderJumpArcs (compatibilidade com o fluxo antigo).</summary>
    private static void DrawJumpArc(SKCanvas canvas, ViewTransform view,
        SegInfo seg, Point2D hit, IReadOnlyList<Conductor> conductors)
    {
        var cond = conductors[seg.Conductor];
        DrawJumpArc(canvas, view, seg, hit,
            ParseColor(cond.Color),
            (float)(cond.GetVisualWidth() * view.Scale));
    }

    private static bool TryLineIntersection(Point2D a1, Point2D b1, Point2D a2, Point2D b2, out Point2D hit)
    {
        hit = default;

        var d1x = b1.X - a1.X;
        var d1y = b1.Y - a1.Y;
        var d2x = b2.X - a2.X;
        var d2y = b2.Y - a2.Y;

        var cross = d1x * d2y - d1y * d2x;
        if (Math.Abs(cross) < 1e-9)
            return false;

        var dx = a2.X - a1.X;
        var dy = a2.Y - a1.Y;
        var t = (dx * d2y - dy * d2x) / cross;
        var u = (dx * d1y - dy * d1x) / cross;

        if (t < -1e-6 || t > 1 + 1e-6 || u < -1e-6 || u > 1 + 1e-6)
            return false;

        hit = new Point2D(a1.X + t * d1x, a1.Y + t * d1y);
        return true;
    }

    /// <summary>
    /// Desenha handles de edição sobre um condutor: vértices como quadrados arrastáveis
    /// e segmentos com highlight de hover.
    /// </summary>
    /// <param name="canvas">Canvas SkiaSharp.</param>
    /// <param name="conductor">Condutor sendo editado.</param>
    /// <param name="view">Transformação de vista (mundo→tela).</param>
    /// <param name="hoverVertex">Índice do vértice sob o cursor (-1 = nenhum).</param>
    /// <param name="hoverSegment">Índice do segmento sob o cursor (-1 = nenhum).</param>
    public static void RenderEditHandles(SKCanvas canvas, Conductor conductor, ViewTransform view,
        int hoverVertex, int hoverSegment)
    {
        if (conductor.Path.Count < 2)
            return;

        // Segmento destacado (hover)
        if (hoverSegment >= 0 && hoverSegment < conductor.Path.Count - 1)
        {
            var p1 = view.WorldToScreen(conductor.Path[hoverSegment]);
            var p2 = view.WorldToScreen(conductor.Path[hoverSegment + 1]);
            using var hoverPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(0, 120, 215),
                StrokeWidth = Math.Max(2.0f, (float)(1.5 * view.Scale)),
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };
            canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, hoverPaint);
        }

        // Vértices como quadrados
        var handleSize = Math.Max(5.0f, (float)(3.5 * view.Scale));
        using var vertexPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
            IsAntialias = true
        };
        using var borderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(0, 120, 215),
            StrokeWidth = 1.5f,
            IsAntialias = true
        };
        using var hoverVertexPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0, 120, 215),
            IsAntialias = true
        };

        for (var i = 0; i < conductor.Path.Count; i++)
        {
            var sp = view.WorldToScreen(conductor.Path[i]);
            var rect = SKRect.Create(
                (float)sp.X - handleSize, (float)sp.Y - handleSize,
                handleSize * 2, handleSize * 2);

            if (i == hoverVertex)
            {
                canvas.DrawRect(rect, hoverVertexPaint);
                canvas.DrawRect(rect, borderPaint);
            }
            else
            {
                canvas.DrawRect(rect, vertexPaint);
                canvas.DrawRect(rect, borderPaint);
            }
        }
    }

    /// <summary>Converte nome/hex de cor padrão CAD em SKColor.</summary>
    private static SKColor ParseColor(string color)
    {
        var c = color.Trim();
        if (c.StartsWith('#'))
            return SKColor.TryParse(c, out var parsed) ? parsed : SKColors.Black;
        return c.ToLowerInvariant() switch
        {
            "black" => SKColors.Black,
            "white" => SKColors.White,
            "red" => SKColors.Red,
            "green" => SKColors.Green,
            "blue" => SKColors.Blue,
            "yellow" => SKColors.Yellow,
            "gray" or "grey" => SKColors.Gray,
            _ => SKColors.Black
        };
    }
}
