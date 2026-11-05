using System.Text.RegularExpressions;

namespace PatchAdams.Core;

public enum DiffAlgorithm
{
    Myers,
    Patience
}

public enum EditKind
{
    Equal,
    Delete,
    Insert
}

public sealed record Edit(EditKind Kind, int AStart, int ALength, int BStart, int BLength);

public sealed record LineDiff(IReadOnlyList<Edit> Edits)
{
    public int DeletedCount => Edits.Where(edit => edit.Kind == EditKind.Delete).Sum(edit => edit.ALength);
    public int InsertedCount => Edits.Where(edit => edit.Kind == EditKind.Insert).Sum(edit => edit.BLength);
    public bool IsIdentical => Edits.All(edit => edit.Kind == EditKind.Equal);

    public IReadOnlyList<string> Apply(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var result = new List<string>();
        foreach (var edit in Edits)
        {
            if (edit.Kind is EditKind.Equal or EditKind.Insert)
                for (var i = edit.BStart; i < edit.BStart + edit.BLength; i++)
                    result.Add(b[i]);
        }
        return result;
    }
}

public sealed record TokenDiff(IReadOnlyList<string> A, IReadOnlyList<string> B, IReadOnlyList<Edit> Edits)
{
    public double Similarity
    {
        get
        {
            var total = A.Count(token => !string.IsNullOrWhiteSpace(token)) +
                        B.Count(token => !string.IsNullOrWhiteSpace(token));
            if (total == 0) return 1;
            var shared = Edits.Where(edit => edit.Kind == EditKind.Equal)
                .Sum(edit => A.Skip(edit.AStart).Take(edit.ALength).Count(token => !string.IsNullOrWhiteSpace(token)) +
                             B.Skip(edit.BStart).Take(edit.BLength).Count(token => !string.IsNullOrWhiteSpace(token)));
            return (double)shared / total;
        }
    }
}

/// <summary>Line and token diffs for viewer rendering and region comparison.</summary>
public static class DiffEngine
{
    public static LineDiff Lines(string a, string b, DiffAlgorithm algorithm = DiffAlgorithm.Patience) =>
        Lines(SplitLines(a), SplitLines(b), algorithm);

    public static LineDiff Lines(IReadOnlyList<string> a, IReadOnlyList<string> b,
        DiffAlgorithm algorithm = DiffAlgorithm.Patience)
    {
        var builder = new EditBuilder(a.Count, b.Count);
        if (algorithm == DiffAlgorithm.Myers)
            Myers(a, 0, a.Count, b, 0, b.Count, builder);
        else
            Patience(a, 0, a.Count, b, 0, b.Count, builder);
        return new LineDiff(builder.Finish());
    }

    public static TokenDiff Tokens(string a, string b, CancellationToken cancellationToken = default) =>
        Tokens(Tokenize(a, cancellationToken), Tokenize(b, cancellationToken), cancellationToken);

    internal static TokenDiff Tokens(
        IReadOnlyList<string> left, IReadOnlyList<string> right, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (left.SequenceEqual(right))
            return new TokenDiff(left, right, left.Count == 0
                ? [] : [new Edit(EditKind.Equal, 0, left.Count, 0, right.Count)]);
        var builder = new EditBuilder(left.Count, right.Count);
        Myers(left, 0, left.Count, right, 0, right.Count, builder, cancellationToken);
        return new TokenDiff(left, right, builder.Finish(cancellationToken));
    }

    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Split('\n').ToList();
        if (text.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    internal static IReadOnlyList<string> Tokenize(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tokens = new List<string>();
        foreach (Match match in Regex.Matches(text, @"[A-Za-z0-9_]+|[ \t\r\n]+|.", RegexOptions.Singleline))
        {
            if ((tokens.Count & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            tokens.Add(match.Value);
        }
        return tokens;
    }

    private static void Myers<T>(IReadOnlyList<T> a, int alo, int ahi, IReadOnlyList<T> b, int blo, int bhi,
        EditBuilder sink, CancellationToken cancellationToken = default) where T : notnull
    {
        var scratch = new MyersScratch(a.Count + b.Count + 3, cancellationToken);
        MyersRange(a, alo, ahi, b, blo, bhi, sink, scratch);
    }

    private static void MyersRange<T>(
        IReadOnlyList<T> a, int alo, int ahi,
        IReadOnlyList<T> b, int blo, int bhi,
        EditBuilder sink,
        MyersScratch scratch) where T : notnull
    {
        scratch.CancellationToken.ThrowIfCancellationRequested();
        var originalAhi = ahi;
        var originalBhi = bhi;
        while (alo < ahi && blo < bhi && EqualityComparer<T>.Default.Equals(a[alo], b[blo]))
        {
            if ((alo & 1023) == 0) scratch.CancellationToken.ThrowIfCancellationRequested();
            sink.Equal(alo++, blo++);
        }
        while (alo < ahi && blo < bhi && EqualityComparer<T>.Default.Equals(a[ahi - 1], b[bhi - 1]))
        {
            if ((ahi & 1023) == 0) scratch.CancellationToken.ThrowIfCancellationRequested();
            ahi--;
            bhi--;
        }

        if (alo == ahi)
        {
            for (var j = blo; j < bhi; j++) sink.Insert(j);
            AppendSuffix(a, b, ahi, bhi, originalAhi, originalBhi, sink);
            return;
        }
        if (blo == bhi)
        {
            for (var i = alo; i < ahi; i++) sink.Delete(i);
            AppendSuffix(a, b, ahi, bhi, originalAhi, originalBhi, sink);
            return;
        }

        var (x, y, u, v) = MiddleSnake(a, alo, ahi, b, blo, bhi, scratch);
        if (x == u && y == v && (x == 0 || y == 0) &&
            (u == ahi - alo || v == bhi - blo))
        {
            for (var i = alo; i < ahi; i++) sink.Delete(i);
            for (var j = blo; j < bhi; j++) sink.Insert(j);
            AppendSuffix(a, b, ahi, bhi, originalAhi, originalBhi, sink);
            return;
        }
        for (var offset = 0; offset < u - x; offset++)
            sink.Equal(alo + x + offset, blo + y + offset);
        MyersRange(a, alo, alo + x, b, blo, blo + y, sink, scratch);
        MyersRange(a, alo + u, ahi, b, blo + v, bhi, sink, scratch);
        AppendSuffix(a, b, ahi, bhi, originalAhi, originalBhi, sink);
    }

    private static (int X, int Y, int U, int V) MiddleSnake<T>(
        IReadOnlyList<T> a, int alo, int ahi,
        IReadOnlyList<T> b, int blo, int bhi,
        MyersScratch scratch) where T : notnull
    {
        var n = ahi - alo;
        var m = bhi - blo;
        var delta = n - m;
        var odd = (delta & 1) == 1;
        var max = (n + m + 1) / 2;
        var offset = max + 1;
        var forward = scratch.Forward;
        var reverse = scratch.Reverse;
        forward[offset + 1] = 0;
        reverse[offset + 1] = 0;

        for (var d = 0; d <= max; d++)
        {
            scratch.CancellationToken.ThrowIfCancellationRequested();
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && forward[offset + k - 1] < forward[offset + k + 1])
                    ? forward[offset + k + 1]
                    : forward[offset + k - 1] + 1;
                var y = x - k;
                var startX = x;
                var startY = y;
                while (x < n && y < m && EqualityComparer<T>.Default.Equals(a[alo + x], b[blo + y]))
                {
                    x++;
                    y++;
                }
                forward[offset + k] = x;
                if (odd && delta - (d - 1) <= k && k <= delta + (d - 1) &&
                    reverse[offset + delta - k] + x >= n)
                    return (startX, startY, x, y);
            }

            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && reverse[offset + k - 1] < reverse[offset + k + 1])
                    ? reverse[offset + k + 1]
                    : reverse[offset + k - 1] + 1;
                var y = x - k;
                var startX = x;
                var startY = y;
                while (x < n && y < m &&
                       EqualityComparer<T>.Default.Equals(a[ahi - 1 - x], b[bhi - 1 - y]))
                {
                    x++;
                    y++;
                }
                reverse[offset + k] = x;
                if (!odd && delta - d <= k && k <= delta + d &&
                    forward[offset + delta - k] + x >= n)
                    return (n - x, m - y, n - startX, m - startY);
            }
        }
        return (0, 0, 0, 0);
    }

    private sealed class MyersScratch(int capacity, CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public int[] Forward { get; } = new int[2 * capacity + 2];
        public int[] Reverse { get; } = new int[2 * capacity + 2];
    }

    private static void Patience<T>(IReadOnlyList<T> a, int alo, int ahi, IReadOnlyList<T> b, int blo, int bhi,
        EditBuilder sink) where T : notnull
    {
        var originalAhi = ahi;
        var originalBhi = bhi;
        while (alo < ahi && blo < bhi && EqualityComparer<T>.Default.Equals(a[alo], b[blo]))
            sink.Equal(alo++, blo++);
        while (alo < ahi && blo < bhi && EqualityComparer<T>.Default.Equals(a[ahi - 1], b[bhi - 1]))
        {
            ahi--;
            bhi--;
        }

        if (alo == ahi || blo == bhi)
        {
            Myers(a, alo, ahi, b, blo, bhi, sink);
        }
        else
        {
            var anchors = UniqueAnchors(a, alo, ahi, b, blo, bhi);
            if (anchors.Count == 0)
                Myers(a, alo, ahi, b, blo, bhi, sink);
            else
            {
                var ai = alo;
                var bi = blo;
                foreach (var (left, right) in anchors)
                {
                    Patience(a, ai, left, b, bi, right, sink);
                    sink.Equal(left, right);
                    ai = left + 1;
                    bi = right + 1;
                }
                Patience(a, ai, ahi, b, bi, bhi, sink);
            }
        }

        AppendSuffix(a, b, ahi, bhi, originalAhi, originalBhi, sink);
    }

    private static void AppendSuffix<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, int aStart, int bStart,
        int aEnd, int bEnd, EditBuilder sink)
    {
        var suffixCount = Math.Min(aEnd - aStart, bEnd - bStart);
        for (var offset = 0; offset < suffixCount; offset++)
            sink.Equal(aStart + offset, bStart + offset);
    }

    private static List<(int A, int B)> UniqueAnchors<T>(IReadOnlyList<T> a, int alo, int ahi,
        IReadOnlyList<T> b, int blo, int bhi)
        where T : notnull
    {
        var left = new Dictionary<T, (int Index, int Count)>();
        var right = new Dictionary<T, (int Index, int Count)>();
        for (var i = alo; i < ahi; i++)
            left[a[i]] = left.TryGetValue(a[i], out var item) ? (i, item.Count + 1) : (i, 1);
        for (var i = blo; i < bhi; i++)
            right[b[i]] = right.TryGetValue(b[i], out var item) ? (i, item.Count + 1) : (i, 1);
        var candidates = left
            .Where(pair => pair.Value.Count == 1 && right.TryGetValue(pair.Key, out var match) && match.Count == 1)
            .Select(pair => (A: pair.Value.Index, B: right[pair.Key].Index))
            .OrderBy(pair => pair.A)
            .ToArray();
        return LongestIncreasingByB(candidates);
    }

    private static List<(int A, int B)> LongestIncreasingByB((int A, int B)[] values)
    {
        if (values.Length == 0) return [];
        var previous = Enumerable.Repeat(-1, values.Length).ToArray();
        var tails = new List<int>();
        for (var i = 0; i < values.Length; i++)
        {
            var low = 0;
            var high = tails.Count;
            while (low < high)
            {
                var mid = (low + high) / 2;
                if (values[tails[mid]].B < values[i].B) low = mid + 1;
                else high = mid;
            }
            if (low > 0) previous[i] = tails[low - 1];
            if (low == tails.Count) tails.Add(i);
            else tails[low] = i;
        }
        var result = new List<(int A, int B)>();
        for (var index = tails[^1]; index >= 0; index = previous[index])
            result.Add(values[index]);
        result.Reverse();
        return result;
    }

    private sealed class EditBuilder(int aCount, int bCount)
    {
        private readonly HashSet<int> deleted = [];
        private readonly HashSet<int> inserted = [];
        private readonly HashSet<(int A, int B)> equals = [];

        public void Delete(int index) => deleted.Add(index);
        public void Insert(int index) => inserted.Add(index);
        public void Equal(int a, int b) => equals.Add((a, b));

        public IReadOnlyList<Edit> Finish(CancellationToken cancellationToken = default)
        {
            var output = new List<Edit>();
            var i = 0;
            var j = 0;
            void Push(EditKind kind, int a, int b)
            {
                if (output.Count > 0)
                {
                    var last = output[^1];
                    if (last.Kind == kind && last.AStart + last.ALength == a && last.BStart + last.BLength == b)
                    {
                        output[^1] = last with
                        {
                            ALength = last.ALength + (kind == EditKind.Delete || kind == EditKind.Equal ? 1 : 0),
                            BLength = last.BLength + (kind == EditKind.Insert || kind == EditKind.Equal ? 1 : 0)
                        };
                        return;
                    }
                }
                output.Add(new Edit(kind, a, kind == EditKind.Delete || kind == EditKind.Equal ? 1 : 0,
                    b, kind == EditKind.Insert || kind == EditKind.Equal ? 1 : 0));
            }

            while (i < aCount || j < bCount)
            {
                if (((i + j) & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (deleted.Contains(i))
                {
                    Push(EditKind.Delete, i++, j);
                }
                else if (inserted.Contains(j))
                {
                    Push(EditKind.Insert, i, j++);
                }
                else if (equals.Contains((i, j)))
                {
                    Push(EditKind.Equal, i++, j++);
                }
                else
                {
                    throw new InvalidOperationException($"Diff does not cover both sequences at ({i}, {j}).");
                }
            }
            return output;
        }
    }
}
