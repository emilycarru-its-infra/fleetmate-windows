namespace FleetMate.Core.Shared;

/// <summary>One treemap tile: where it sits in the chart's bounds, and the slice's position in the input.</summary>
public readonly record struct TreemapTile(ChartSlice Slice, double X, double Y, double Width, double Height, int Index);

/// <summary>A squarified treemap, laid out the same in both Windows apps and as the macOS app lays it out.</summary>
public static class TreemapLayout
{
    /// <summary>
    /// Squarified treemap rectangles (Bruls, Huizing and van Wijk): slices
    /// fill strips along the shorter side, and a strip takes another slice
    /// only while that keeps its worst aspect ratio from getting worse. Slices
    /// go in largest first; empty ones are skipped. <see cref="TreemapTile.Index"/>
    /// is the slice's position in <paramref name="slices"/>.
    /// </summary>
    public static List<TreemapTile> Tiles(IReadOnlyList<ChartSlice> slices, double x, double y, double width, double height)
    {
        var ordered = slices.Select((s, i) => (Slice: s, Index: i))
            .Where(t => t.Slice.Value > 0)
            .OrderByDescending(t => t.Slice.Value)
            .ToList();
        var result = new List<TreemapTile>();
        var total = (double)ordered.Sum(t => t.Slice.Value);
        if (ordered.Count == 0 || total <= 0 || width <= 0 || height <= 0) return result;

        var areas = ordered.Select(t => t.Slice.Value / total * width * height).ToList();
        var (rx, ry, rw, rh) = (x, y, width, height);
        var i = 0;
        while (i < ordered.Count)
        {
            var wide = rw >= rh;
            var side = wide ? rh : rw;
            var row = new List<int>();
            var rowArea = 0.0;
            var bestWorst = double.PositiveInfinity;

            for (var j = i; j < ordered.Count; j++)
            {
                var candidate = rowArea + areas[j];
                var strip = candidate / side;
                var worst = row.Append(j).Max(k =>
                {
                    var length = areas[k] / strip;
                    return Math.Max(length / strip, strip / length);
                });
                if (row.Count == 0 || worst <= bestWorst)
                {
                    row.Add(j);
                    rowArea = candidate;
                    bestWorst = worst;
                }
                else
                {
                    break;
                }
            }

            var thickness = rowArea / side;
            var offset = 0.0;
            foreach (var k in row)
            {
                var length = areas[k] / thickness;
                result.Add(wide
                    ? new TreemapTile(ordered[k].Slice, rx, ry + offset, thickness, length, ordered[k].Index)
                    : new TreemapTile(ordered[k].Slice, rx + offset, ry, length, thickness, ordered[k].Index));
                offset += length;
            }

            if (wide) { rx += thickness; rw = Math.Max(0, rw - thickness); }
            else { ry += thickness; rh = Math.Max(0, rh - thickness); }
            i += row.Count;
        }

        return result;
    }

    /// <summary>A slice's whole-number share of the total, rounded down as the Mac does.</summary>
    public static int SharePercent(int value, int total) => total > 0 ? (int)((double)value / total * 100) : 0;
}
