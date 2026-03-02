namespace Prognosis.Tests;

public class DetectCyclesTests
{
    [Fact]
    public void DetectCycles_AcyclicGraph_ReturnsEmpty()
    {
        var leaf = HealthNode.Create("Leaf");
        var root = HealthNode.Create("Root")
            .DependsOn(leaf, Importance.Required);
        var graph = HealthGraph.Create(root);

        var cycles = graph.DetectCycles();

        Assert.Empty(cycles);
    }

    [Fact]
    public void DetectCycles_SingleNode_ReturnsEmpty()
    {
        var graph = HealthGraph.Create(HealthNode.Create("Only"));

        var cycles = graph.DetectCycles();

        Assert.Empty(cycles);
    }

    [Fact]
    public void DetectCycles_DirectCycle_ReturnsCycle()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B").DependsOn(a, Importance.Required);
        a.DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(a);

        var cycles = graph.DetectCycles();

        Assert.NotEmpty(cycles);
        // The cycle should contain both A and B.
        var cycle = cycles[0];
        Assert.Contains("A", cycle);
        Assert.Contains("B", cycle);
    }

    [Fact]
    public void DetectCycles_DiamondGraph_NoCycle()
    {
        var shared = HealthNode.Create("Shared");
        var a = HealthNode.Create("A").DependsOn(shared, Importance.Required);
        var b = HealthNode.Create("B").DependsOn(shared, Importance.Required);
        var root = HealthNode.Create("Root")
            .DependsOn(a, Importance.Required)
            .DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(root);

        var cycles = graph.DetectCycles();

        Assert.Empty(cycles);
    }

    [Fact]
    public void DetectCycles_ThreeNodeCycle_ReturnsCycle()
    {
        var a = HealthNode.Create("A");
        var b = HealthNode.Create("B");
        var c = HealthNode.Create("C");
        a.DependsOn(b, Importance.Required);
        b.DependsOn(c, Importance.Required);
        c.DependsOn(a, Importance.Required);
        var graph = HealthGraph.Create(a);

        var cycles = graph.DetectCycles();

        Assert.NotEmpty(cycles);
    }

    [Fact]
    public void DetectCycles_DeepChain_NoCycle()
    {
        var d = HealthNode.Create("D");
        var c = HealthNode.Create("C").DependsOn(d, Importance.Required);
        var b = HealthNode.Create("B").DependsOn(c, Importance.Required);
        var a = HealthNode.Create("A").DependsOn(b, Importance.Required);
        var graph = HealthGraph.Create(a);

        var cycles = graph.DetectCycles();

        Assert.Empty(cycles);
    }
}
