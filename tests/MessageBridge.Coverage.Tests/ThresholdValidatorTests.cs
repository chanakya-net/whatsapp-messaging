namespace MessageBridge.Coverage.Tests;

public class ThresholdValidatorTests
{
    [Fact]
    public void PassWhenCombinedLineAboveThreshold()
    {
        var coverage = new CoverageResult("MyProject", linePercent: 85.5m, branchPercent: 80m);
        var result = CoverageValidator.ValidateThreshold(coverage);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void FailWhenCombinedLineBelowThreshold()
    {
        var coverage = new CoverageResult("MyProject", linePercent: 84.9m, branchPercent: 80m);
        var result = CoverageValidator.ValidateThreshold(coverage);
        result.Passed.Should().BeFalse("line coverage below 85%");
    }

    [Fact]
    public void FailWhenCombinedBranchBelowThreshold()
    {
        var coverage = new CoverageResult("MyProject", linePercent: 85m, branchPercent: 79.9m);
        var result = CoverageValidator.ValidateThreshold(coverage);
        result.Passed.Should().BeFalse("branch coverage below 80%");
    }

    [Fact]
    public void FailWhenProjectLineBelowThreshold()
    {
        var coverage = new CoverageResult("MyProject", linePercent: 85m, branchPercent: 80m, projectLinePercent: 79.9m);
        var result = CoverageValidator.ValidateThreshold(coverage);
        result.Passed.Should().BeFalse("per-project line coverage below 80%");
    }

    [Fact]
    public void ResultIncludesMeasuredProjectAndValue()
    {
        var coverage = new CoverageResult("TestProject", linePercent: 84m, branchPercent: 80m);
        var result = CoverageValidator.ValidateThreshold(coverage);
        result.Message.Should().Contain("TestProject", "must identify which project failed");
        result.Message.Should().Contain("84", "must include measured value");
    }
}

public record CoverageResult(string Project, decimal linePercent, decimal branchPercent, decimal? projectLinePercent = null)
{
    public static CoverageResult Combined(decimal linePercent, decimal branchPercent) => new("combined", linePercent, branchPercent);
}

public record ValidationResult(bool Passed, string Message);

public static class CoverageValidator
{
    private const decimal CombinedLineThreshold = 85m;
    private const decimal CombinedBranchThreshold = 80m;
    private const decimal ProjectLineThreshold = 80m;

    public static ValidationResult ValidateThreshold(CoverageResult coverage)
    {
        if (coverage.linePercent < CombinedLineThreshold)
            return new(false, $"{coverage.Project}: line coverage {coverage.linePercent}% below {CombinedLineThreshold}%");

        if (coverage.branchPercent < CombinedBranchThreshold)
            return new(false, $"{coverage.Project}: branch coverage {coverage.branchPercent}% below {CombinedBranchThreshold}%");

        if (coverage.projectLinePercent.HasValue && coverage.projectLinePercent < ProjectLineThreshold)
            return new(false, $"{coverage.Project}: project line coverage {coverage.projectLinePercent}% below {ProjectLineThreshold}%");

        return new(true, $"{coverage.Project}: all thresholds passed");
    }
}
