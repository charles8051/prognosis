namespace Prognosis.Tests;

public class HealthSnapshotTests
{
    [Fact]
    public void ToString_WithReason_IncludesNameStatusAndReason()
    {
        var snapshot = new HealthSnapshot("DB", HealthStatus.Unhealthy, "connection refused");

        var str = snapshot.ToString();

        Assert.Contains("DB", str);
        Assert.Contains("Unhealthy", str);
        Assert.Contains("connection refused", str);
    }

    [Fact]
    public void ToString_WithoutReason_IncludesNameAndStatus()
    {
        var snapshot = new HealthSnapshot("Cache", HealthStatus.Healthy);

        var str = snapshot.ToString();

        Assert.Contains("Cache", str);
        Assert.Contains("Healthy", str);
    }

    [Fact]
    public void Reason_DefaultsToNull()
    {
        var snapshot = new HealthSnapshot("Svc", HealthStatus.Healthy);

        Assert.Null(snapshot.Reason);
    }

    [Fact]
    public void RecordEquality_SameData_AreEqual()
    {
        var a = new HealthSnapshot("Svc", HealthStatus.Degraded, "slow");
        var b = new HealthSnapshot("Svc", HealthStatus.Degraded, "slow");

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentStatus_AreNotEqual()
    {
        var a = new HealthSnapshot("Svc", HealthStatus.Healthy);
        var b = new HealthSnapshot("Svc", HealthStatus.Degraded);

        Assert.NotEqual(a, b);
    }
}
