using ASQATools.Shared.Services;
using Xunit;

namespace ASQATools.Tests.Unit;

public class ReportExporterServiceTests
{
    [Fact]
    public void ParseRows_ShouldAutoDetectTabAndSkipHeaderRow()
    {
        string tsv = "Region\tDept\tDocId\r\nRiyadh\tMotor\tPOL-1001\r\nJeddah\tMedical\tPOL-2002";

        var rows = ReportExporterService.ParseRows(tsv, "auto", skipHeaderRow: true, out char delim, out var headers);

        Assert.Equal('\t', delim);
        Assert.Equal(new[] { "Region", "Dept", "DocId" }, headers);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "Riyadh", "Motor", "POL-1001" }, rows[0]);
        Assert.Equal(new[] { "Jeddah", "Medical", "POL-2002" }, rows[1]);
    }

    [Fact]
    public void ResolveUrlTemplate_ShouldSubstituteAndEscapeParameters()
    {
        string template = "https://reports.local/pdf?region={0}&dept={1}&id={2}";
        var values = new List<string> { "Riyadh Central", "Motor & Fleet", "POL-1001" };

        string resolved = ReportExporterService.ResolveUrlTemplate(template, values);

        Assert.Equal("https://reports.local/pdf?region=Riyadh%20Central&dept=Motor%20%26%20Fleet&id=POL-1001", resolved);
    }

    [Fact]
    public void ResolveRelativeFilePath_ShouldBuildNestedSubfoldersAndAppendPdfExtension()
    {
        string template = "subdir_{0}/subdir_{1}/file_{2}";
        var values = new List<string> { "Riyadh", "Motor", "POL:1001" };

        string relPath = ReportExporterService.ResolveRelativeFilePath(template, values, rowNumber: 1);

        Assert.Equal("subdir_Riyadh/subdir_Motor/file_POL_1001.pdf", relPath);
    }
}
