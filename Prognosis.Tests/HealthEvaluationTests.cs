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
    public void Unknown_ReturnsUnknownWithReason()
    {
        var eval = HealthEvaluation.Unknown("awaiting first probe");

        Assert.Equal(HealthStatus.Unknown, eval.Status);
        Assert.Equal("awaiting first probe", eval.Reason);
    }

    /// <summary>
    /// The point of the factory (ADR-008 §3): an Unknown node can now explain itself in the
    /// rollup. Previously Unknown was the only status with no reason-carrying factory, so an
    /// Unknown parent fell back to the bare "{node} is Unknown" — which is what an operator
    /// saw during the incident that motivated the ADR.
    /// </summary>
    [Fact]
    public void Unknown_WithReason_SurfacesThatReasonInTheRollup()
    {
        var dep = HealthNode.Create("Inference")
            .WithHealthProbe(() => HealthEvaluation.Unknown("model not loaded"));
        var parent = HealthNode.Create("Parent").DependsOn(dep, Importance.Important);
        var graph = HealthGraph.Create(parent);

        var rolled = graph.GetReport().Nodes.First(n => n.Name == "Parent");

        Assert.Equal(HealthStatus.Unknown, rolled.Status);
        Assert.Contains("model not loaded", rolled.Reason!);
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
