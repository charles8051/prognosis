namespace Prognosis.Tests;

public class HealthEvaluationTests
{
    [Fact]
    public void Healthy_ReturnsHealthyWithNoReason()
    {
        var eval = HealthEvaluation.Healthy;

        Assert.Equal(HealthStatus.Healthy, eval.Status);
        Assert.Null(eval.Reason);
    }

    [Fact]
    public void Unhealthy_ReturnsUnhealthyWithReason()
    {
        var eval = HealthEvaluation.Unhealthy("connection refused");

        Assert.Equal(HealthStatus.Unhealthy, eval.Status);
        Assert.Equal("connection refused", eval.Reason);
    }

    [Fact]
    public void Degraded_ReturnsDegradedWithReason()
    {
        var eval = HealthEvaluation.Degraded("high latency");

        Assert.Equal(HealthStatus.Degraded, eval.Status);
        Assert.Equal("high latency", eval.Reason);
    }

    [Fact]
    public void ImplicitConversion_FromHealthStatus_CreatesEvaluationWithNoReason()
    {
        HealthEvaluation eval = HealthStatus.Unknown;

        Assert.Equal(HealthStatus.Unknown, eval.Status);
        Assert.Null(eval.Reason);
    }

    [Fact]
    public void ImplicitConversion_CanBeUsedInMethodReturn()
    {
        // Simulate what a health check delegate would do.
        HealthEvaluation Check() => HealthStatus.Degraded;

        var result = Check();

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void ToString_WithReason_IncludesStatusAndReason()
    {
        var eval = HealthEvaluation.Unhealthy("timeout");

        var str = eval.ToString();

        Assert.Contains("Unhealthy", str);
        Assert.Contains("timeout", str);
    }

    [Fact]
    public void ToString_WithoutReason_ReturnsStatusOnly()
    {
        var eval = HealthEvaluation.Healthy;

        var str = eval.ToString();

        Assert.Equal("Healthy", str);
    }
}
