namespace PatchAdamsSharp;

/// <summary>Estimates useful code widths without letting one unusually long line dominate.</summary>
internal static class ColumnWidthBalancer
{
    public static double[] Allocate(
        double availableWidth,
        double fontSize,
        IReadOnlyList<IReadOnlyList<string>> displayedRows)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (displayedRows.Count == 0)
            return [];

        var minimum = Math.Min(90, availableWidth / displayedRows.Count);
        var demand = displayedRows.Select(rows =>
            Math.Max(0, EstimatedWidth(rows, fontSize) - minimum)).ToArray();
        var totalDemand = demand.Sum();
        var distributable = availableWidth - minimum * displayedRows.Count;
        return demand.Select(need => minimum + distributable *
            (totalDemand > 0 ? need / totalDemand : 1d / displayedRows.Count)).ToArray();
    }

    private static double EstimatedWidth(IReadOnlyList<string> rows, double fontSize)
    {
        var lengths = rows.Select(row => row.Length).Order().ToArray();
        var typicalLength = lengths.Length == 0 ? 48 : lengths[(lengths.Length - 1) * 9 / 10];
        return 40 + Math.Clamp(typicalLength, 24, 140) * fontSize * 0.62;
    }
}
