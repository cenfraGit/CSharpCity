using CSharpCity.Model;

namespace CSharpCity.Layout.Tests;

/// <summary>
/// The ordinary building types: warehouses, museums and railings.
/// </summary>
public class VernacularTests
{
    static SceneGraph City(Action<TypeNode> adjust)
    {
        var model = Fixture.Connect(Fixture.Solution(2, 10, 2));
        adjust(model.Projects.SelectMany(p => p.Types).First(t => t.Name == "T3"));
        return CityLayout.Build(model);
    }

    /// <summary>Railing posts are the only thin waist-high uprights on an ordinary lot.</summary>
    static int Railings(SceneGraph scene) =>
        scene.Boxes.Count(b => MathF.Abs(b.Size.Y - 1.15f) < 0.001f
                            && MathF.Abs(b.Size.X - 0.09f) < 0.001f);

    [Fact]
    public void AnInternalTypeIsRailedOffAndAPublicOneIsNot()
    {
        // The city has always drawn doors for public constructors, so a type nobody can construct
        // simply had a blank wall — which looks the same as a facade you can't see the door on.
        Assert.True(Railings(City(t => t.IsPublic = false)) > 0);
        Assert.Equal(0, Railings(City(t => t.IsPublic = true)));
    }

    [Fact]
    public void ALongUntouchedTypeIsPreserved()
    {
        // Round columns: the museum's colonnade is the only place an ordinary building has them.
        static int Columns(int daysSinceChange) =>
            City(t => t.DaysSinceChange = daysSinceChange).Boxes
                .Count(b => (b.Flags & (uint)BoxFlags.Round) != 0
                         && MathF.Abs(b.Size.X - 0.5f) < 0.001f);

        Assert.True(Columns(900) > 0, "two years untouched should raise a colonnade");
        Assert.Equal(0, Columns(30));

        // -1 is "no history at all", which must not be read as "very old".
        Assert.Equal(0, Columns(-1));
    }

    [Fact]
    public void ADataOnlyTypeBecomesAWarehouseWithoutChangingSize()
    {
        // The massing rule: this changes what a DTO looks like, never how big it is. A record with
        // twelve properties must occupy exactly the space it did as an anonymous squat storey.
        var model = Fixture.Solution(1, 6, 1);
        var dto = model.Projects[0].Types[0];
        dto.Methods.Clear();
        dto.FieldCount = 4;
        dto.PropertyCount = 8;

        var scene = CityLayout.Build(model);
        var site = scene.Sites[dto.Id];

        float expected = MathF.Max(2.8f, (dto.FieldCount + dto.PropertyCount) * 0.35f + 2.5f);
        var shell = scene.Boxes.First(b => b.PickId == site.PickId
                                        && MathF.Abs(b.Size.Y - expected) < 0.001f);

        Assert.Equal(site.Side, shell.Size.X, 3);

        // And it has no windows: nothing is going on inside a warehouse.
        Assert.Equal(0u, shell.Flags & (uint)BoxFlags.Windows);
    }
}
