using ASQATools.Shared.Services;
using Xunit;

namespace ASQATools.Tests.Unit;

public class GitDiffPatcherTests
{
    [Fact]
    public void ApplyGitHunks_ShouldApplyAdditionAndDeletionCorrectly()
    {
        string original = "public void Test()\n{\n    int a = 1;\n    return;\n}";
        string hunks = "@@ -2,3 +2,3 @@\n {\n-    int a = 1;\n+    int a = 42;\n     return;";

        string patched = CodeReflectorService.ApplyGitHunks(original, hunks, out int succeeded, out int failed);

        Assert.Equal(1, succeeded);
        Assert.Equal(0, failed);
        Assert.Contains("int a = 42;", patched);
        Assert.DoesNotContain("int a = 1;", patched);
    }

    [Fact]
    public void ApplySearchReplaceBlocks_ShouldMatchEvenWhenFileUsesCrlfAndPatchUsesLf()
    {
        string originalCrlf = "namespace Demo;\r\npublic class Sample\r\n{\r\n    public int Value => 10;\r\n}\r\n";
        string patchWithLf = "<<<<<<< SEARCH\npublic class Sample\n{\n    public int Value => 10;\n}\n=======\npublic class Sample\n{\n    public int Value => 99;\n}\n>>>>>>> REPLACE";

        string patched = CodeReflectorService.ApplySearchReplaceBlocks(originalCrlf, patchWithLf, out int succeeded, out int failed);

        Assert.Equal(1, succeeded);
        Assert.Equal(0, failed);
        Assert.Contains("public int Value => 99;\r\n", patched);
    }

    [Fact]
    public void GetDirectoryChildren_ShouldRejectPathTraversalOutsideRoot()
    {
        var exporter = new CodebaseExporterService();
        string tempRoot = Path.Combine(Path.GetTempPath(), "ASQA_TREE_TEST_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => exporter.GetDirectoryChildren(tempRoot, "../../etc"));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
