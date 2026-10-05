using System.Numerics;
using CSharpCity.Render;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// Guards <c>--walkable</c>, the mode Simulation Lab opens the city in.
/// </summary>
/// <remarks>
/// Its whole point is that the keys feel the same as they did in the line view a moment ago: Shift
/// four times the walk, Alt a quarter of it, and no flying off. The ordinary walk keeps its own
/// absolute sprint, which these must not disturb.
/// </remarks>
public class WalkableTests
{
    static float Walked(bool walkable, bool fast, bool slow)
    {
        var camera = new Camera { Walkable = walkable, Yaw = 0f };
        camera.Move(new Vector3(0, 0, 1), 1f, fast, slow);
        return camera.Position.X;
    }

    [Fact]
    public void ShiftAndAltMultiplyTheWalk()
    {
        var walk = new Camera().WalkSpeed;

        Assert.Equal(walk, Walked(true, false, false), 3);
        Assert.Equal(walk * Camera.WalkableFast, Walked(true, true, false), 3);
        Assert.Equal(walk * Camera.WalkableSlow, Walked(true, false, true), 3);
    }

    [Fact]
    public void AnOrdinaryWalkStillSprintsAtItsOwnSpeed()
    {
        var camera = new Camera();

        Assert.Equal(camera.WalkSprintSpeed, Walked(false, true, false), 3);
        Assert.Equal(camera.WalkSpeed, Walked(false, false, true), 3);
    }

    [Fact]
    public void AWalkableCameraWillNotFly()
    {
        var camera = new Camera { Walkable = true };

        camera.ToggleFly();

        Assert.False(camera.Flying);
    }
}
