using System.Text;

namespace ASQATools.Shared.Services;

public static class FastDiffEngine
{
    private enum DiffOp { Unchanged, Added, Deleted }
    private record struct DiffEntry(DiffOp Op, int? OldLine, int? NewLine, string Text);

    private const long MaxMatrixCells = 4_000_000;

    public static bool HasOnlyWhitespaceOrNewlineChanges(string? oldText, string? newText)
    {
        if (ReferenceEquals(oldText, newText) || string.Equals(oldText, newText, StringComparison.Ordinal))
            return true;

        if (oldText == null || newText == null)
            return false;

        int i = 0, j = 0;
        int lenOld = oldText.Length;
        int lenNew = newText.Length;

        while (i < lenOld || j < lenNew)
        {
            bool hadOldSpace = false;
            while (i < lenOld && char.IsWhiteSpace(oldText[i])) { i++; hadOldSpace = true; }

            bool hadNewSpace = false;
            while (j < lenNew && char.IsWhiteSpace(newText[j])) { j++; hadNewSpace = true; }

            if (i < lenOld && j < lenNew)
            {
                if (i > 0 && j > 0 && hadOldSpace != hadNewSpace)
                    return false;

                if (oldText[i] != newText[j])
                    return false;

                i++;
                j++;
            }
            else
            {
                return i == lenOld && j == lenNew;
            }
        }
        return true;
    }

    public static string GenerateUnifiedDiffWithLineNumbers(string oldText, string newText, int contextLines = 3)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return "No textual difference.";

        var oldLines = (oldText ?? string.Empty).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var newLines = (newText ?? string.Empty).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        int n = oldLines.Length;
        int m = newLines.Length;

        // Trim common unchanged prefix and suffix lines to minimize the LCS DP matrix size
        int prefix = 0;
        while (prefix < n && prefix < m && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < (n - prefix) && suffix < (m - prefix) &&
               oldLines[n - 1 - suffix] == newLines[m - 1 - suffix])
        {
            suffix++;
        }

        int midN = n - prefix - suffix;
        int midM = m - prefix - suffix;

        if ((long)midN * midM > MaxMatrixCells)
            return "--- [FILE TOO LARGE FOR INLINE MATRIX DIFF - REFER TO FULL PROPOSED FILE] ---";

        var diffEntries = new List<DiffEntry>(Math.Max(n, m));

        for (int i = 0; i < prefix; i++)
        {
            diffEntries.Add(new DiffEntry(DiffOp.Unchanged, i + 1, i + 1, oldLines[i]));
        }

        if (midN > 0 || midM > 0)
        {
            int[,] dp = new int[midN + 1, midM + 1];
            for (int i = 0; i < midN; i++)
            {
                for (int j = 0; j < midM; j++)
                {
                    if (oldLines[prefix + i] == newLines[prefix + j])
                        dp[i + 1, j + 1] = dp[i, j] + 1;
                    else
                        dp[i + 1, j + 1] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            var midEntries = new List<DiffEntry>(midN + midM);
            int x = midN, y = midM;

            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && oldLines[prefix + x - 1] == newLines[prefix + y - 1])
                {
                    midEntries.Add(new DiffEntry(DiffOp.Unchanged, prefix + x, prefix + y, oldLines[prefix + x - 1]));
                    x--;
                    y--;
                }
                else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
                {
                    midEntries.Add(new DiffEntry(DiffOp.Added, null, prefix + y, newLines[prefix + y - 1]));
                    y--;
                }
                else if (x > 0 && (y == 0 || dp[x, y - 1] < dp[x - 1, y]))
                {
                    midEntries.Add(new DiffEntry(DiffOp.Deleted, prefix + x, null, oldLines[prefix + x - 1]));
                    x--;
                }
            }

            midEntries.Reverse();
            diffEntries.AddRange(midEntries);
        }

        for (int i = 0; i < suffix; i++)
        {
            int oldIdx = n - suffix + i;
            int newIdx = m - suffix + i;
            diffEntries.Add(new DiffEntry(DiffOp.Unchanged, oldIdx + 1, newIdx + 1, oldLines[oldIdx]));
        }

        var sb = new StringBuilder();
        int total = diffEntries.Count;

        var changedIndices = new List<int>();
        for (int i = 0; i < total; i++)
        {
            if (diffEntries[i].Op != DiffOp.Unchanged)
                changedIndices.Add(i);
        }

        if (changedIndices.Count == 0)
            return "No textual difference.";

        var ranges = new List<(int Start, int End)>();
        int currentStart = Math.Max(0, changedIndices[0] - contextLines);
        int currentEnd = Math.Min(total - 1, changedIndices[0] + contextLines);

        for (int i = 1; i < changedIndices.Count; i++)
        {
            int idx = changedIndices[i];
            int rangeStart = Math.Max(0, idx - contextLines);
            int rangeEnd = Math.Min(total - 1, idx + contextLines);

            if (rangeStart <= currentEnd + 1)
            {
                currentEnd = Math.Max(currentEnd, rangeEnd);
            }
            else
            {
                ranges.Add((currentStart, currentEnd));
                currentStart = rangeStart;
                currentEnd = rangeEnd;
            }
        }
        ranges.Add((currentStart, currentEnd));

        foreach (var (start, end) in ranges)
        {
            int oldHunkStart = diffEntries.Skip(start).FirstOrDefault(e => e.OldLine.HasValue).OldLine ?? 1;
            int newHunkStart = diffEntries.Skip(start).FirstOrDefault(e => e.NewLine.HasValue).NewLine ?? 1;

            int oldCount = diffEntries.Skip(start).Take(end - start + 1).Count(e => e.Op != DiffOp.Added);
            int newCount = diffEntries.Skip(start).Take(end - start + 1).Count(e => e.Op != DiffOp.Deleted);

            sb.AppendLine($"@@ -{oldHunkStart},{oldCount} +{newHunkStart},{newCount} @@");

            for (int i = start; i <= end; i++)
            {
                var entry = diffEntries[i];
                switch (entry.Op)
                {
                    case DiffOp.Added:
                        sb.AppendLine($"+ L{entry.NewLine,5} | {entry.Text}");
                        break;
                    case DiffOp.Deleted:
                        sb.AppendLine($"- L{entry.OldLine,5} | {entry.Text}");
                        break;
                    case DiffOp.Unchanged:
                        sb.AppendLine($"  L{entry.NewLine,5} | {entry.Text}");
                        break;
                }
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
