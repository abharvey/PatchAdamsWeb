using System.Text;

namespace PatchAdams.Core;

/// <summary>Plain post-patch code, independent of viewer gutters, diff mode, or collapsed rows.</summary>
public sealed record ResultingCode(string Text, int OmittedUnknownLines = 0)
{
    public static bool IsAvailable(AppliedFile? file) =>
        file?.PostText is not null && file.Change.Kind != FileChangeKind.Deleted &&
        file.Status.Kind is ApplyStatusKind.Clean or ApplyStatusKind.Fuzz;

    public static ResultingCode? From(AppliedFile? file, bool syntheticBase = false)
    {
        if (!IsAvailable(file)) return null;
        var text = file!.PostText!;
        if (!syntheticBase) return new ResultingCode(text);

        var output = new StringBuilder();
        var omitted = 0;
        var lines = ApplyEngine.SplitLines(text);
        for (var index = 0; index < lines.Count; index++)
        {
            // Only inherited synthetic gaps are unknown. A candidate may deliberately
            // add text that resembles a filler marker; those added lines are real code.
            if (file.LineMap.BaseForPost(index + 1).HasValue &&
                SyntheticBase.IsFiller(lines[index].TrimEnd('\r')))
            {
                omitted++;
                continue;
            }
            output.Append(lines[index]);
            if (index < lines.Count - 1 || text.EndsWith('\n'))
                output.Append('\n');
        }
        return new ResultingCode(output.ToString(), omitted);
    }
}
