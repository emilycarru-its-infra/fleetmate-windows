namespace FleetMate.Core.Shared;

/// <summary>
/// How a tab's Graphs strip splits its cards into rows, matching the macOS
/// app. Cards keep their order. A card spanning <c>fullRow</c> or more takes
/// a row of its own. Every other run of cards goes into the fewest rows that
/// hold it (<c>unitsPerRow</c> units each), split so the rows carry as even
/// a number of units as possible — two rows of three rather than five and a
/// stretched one.
/// </summary>
public static class WidgetRowPacking
{
    public static List<List<int>> Rows(IReadOnlyList<int> spans, int unitsPerRow, int fullRow)
    {
        var cap = Math.Max(1, unitsPerRow);
        var result = new List<List<int>>();
        var run = new List<int>();

        void Flush()
        {
            result.AddRange(Balanced(run, run.Select(i => Math.Min(Math.Max(1, spans[i]), cap)).ToList(), cap));
            run = new List<int>();
        }

        for (var i = 0; i < spans.Count; i++)
        {
            if (spans[i] >= fullRow)
            {
                Flush();
                result.Add(new List<int> { i });
            }
            else
            {
                run.Add(i);
            }
        }
        Flush();
        return result;
    }

    /// <summary>Fewest rows first (greedy is optimal for that), then the contiguous split with the smallest spread of row sizes.</summary>
    private static List<List<int>> Balanced(List<int> items, List<int> spans, int cap)
    {
        var n = items.Count;
        if (n == 0) return new();

        var rowCount = 1;
        var used = 0;
        foreach (var s in spans)
        {
            if (used + s > cap && used > 0) { rowCount++; used = 0; }
            used += s;
        }
        if (rowCount == 1) return new() { items };

        var mean = spans.Sum() / (double)rowCount;
        var prefix = new int[n + 1];
        for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + spans[i];

        var cost = new double[rowCount + 1, n + 1];
        var cut = new int[rowCount + 1, n + 1];
        for (var k = 0; k <= rowCount; k++)
            for (var i = 0; i <= n; i++) cost[k, i] = double.PositiveInfinity;
        cost[0, 0] = 0;

        for (var k = 1; k <= rowCount; k++)
        {
            for (var i = 1; i <= n; i++)
            {
                for (var j = k - 1; j < i; j++)
                {
                    var units = prefix[i] - prefix[j];
                    if (units > cap || double.IsPositiveInfinity(cost[k - 1, j])) continue;
                    var c = cost[k - 1, j] + Math.Pow(units - mean, 2);
                    if (c < cost[k, i])
                    {
                        cost[k, i] = c;
                        cut[k, i] = j;
                    }
                }
            }
        }
        if (double.IsPositiveInfinity(cost[rowCount, n])) return new() { items };

        var rows = new List<List<int>>();
        var end = n;
        for (var k = rowCount; k > 0; k--)
        {
            var start = cut[k, end];
            rows.Insert(0, items.GetRange(start, end - start));
            end = start;
        }
        return rows;
    }
}
