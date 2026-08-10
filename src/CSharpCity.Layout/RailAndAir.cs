using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Rail between districts and an airport in each â€” the two dependency layers footpaths can't show.
/// </summary>
/// <remarks>
/// Footpaths only exist where code actually references code, so they can never reveal a dependency
/// that was <em>declared</em> and then not used. Rail can: a line is laid for every
/// &lt;ProjectReference&gt;, and trains run on it in proportion to real cross-project type usage.
/// <b>A rusted line with no trains is a project reference you can delete.</b> That's the whole point
/// of this phase, and it's why unused lines are never culled â€” they're the interesting ones.
///
/// Airports carry the other invisible layer: external NuGet packages arrive from outside the city
/// entirely, so nothing on the ground can represent them.
/// </remarks>
internal static class RailAndAir
{
    const float RailY = 0.44f;        // above footpaths, below everything structural
    const float GaugeHalf = 0.75f;
    const float SleeperWidth = 2.4f;

    static readonly Vector4 LiveRail = new(0.34f, 0.33f, 0.31f, 1f);
    static readonly Vector4 DeadRail = new(0.38f, 0.24f, 0.16f, 1f);   // rust

    public sealed record Result(int Lines, int Unused, int Airports);

    public static Result Build(SceneGraph scene, CityModel model)
    {
        // Real usage per ordered project pair, from the type graph.
        var usage = new Dictionary<(string From, string To), int>();
        var projectOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in model.Projects)
            foreach (var type in project.Types)
                projectOf[type.Id] = project.Name;

        foreach (var edge in model.Edges)
        {
            if (!projectOf.TryGetValue(edge.FromId, out var from)) continue;
            if (!projectOf.TryGetValue(edge.ToId, out var to)) continue;
            if (string.Equals(from, to, StringComparison.Ordinal)) continue;

            var key = (from, to);
            usage[key] = usage.GetValueOrDefault(key) + edge.Weight;
        }

        int lines = 0, unused = 0, airports = 0;

        foreach (var project in model.Projects)
        {
            if (!scene.Districts.TryGetValue(project.Name, out var origin)) continue;

            foreach (var referenced in project.ProjectReferences)
            {
                if (!scene.Districts.TryGetValue(referenced, out var destination)) continue;

                int weight = usage.GetValueOrDefault((project.Name, referenced));
                LayLine(scene, origin, destination, weight);
                lines++;
                if (weight == 0) unused++;
            }

            if (LayAirport(scene, project, origin)) airports++;
        }

        return new Result(lines, unused, airports);
    }

    /// <summary>Two rails and their sleepers, running district centre to district centre.</summary>
    static void LayLine(SceneGraph scene, Bounds2 from, Bounds2 to, int weight)
    {
        var start = new Vector3(from.CenterX, RailY, from.CenterZ);
        var end = new Vector3(to.CenterX, RailY, to.CenterZ);

        var delta = end - start;
        float length = new Vector2(delta.X, delta.Z).Length();
        if (length < 12f) return;

        float yaw = MathF.Atan2(delta.Z, delta.X);
        var colour = weight == 0 ? DeadRail : LiveRail;
        var middle = new Vector3((start.X + end.X) * 0.5f, RailY, (start.Z + end.Z) * 0.5f);

        // Sleeper bed first, then the two rails on top of it.
        scene.Roads.Add(new RoadQuad
        {
            Center = middle,
            Length = length,
            Width = SleeperWidth,
            Yaw = yaw,
            Color = weight == 0 ? new Vector4(0.24f, 0.20f, 0.15f, 1f) : new Vector4(0.20f, 0.19f, 0.18f, 1f),
            Flags = (uint)RoadFlags.Rail,
            Layer = CityLayer.Rail,
        });

        var across = new Vector3(-MathF.Sin(yaw), 0f, MathF.Cos(yaw)) * GaugeHalf;
        for (int side = -1; side <= 1; side += 2)
        {
            scene.Roads.Add(new RoadQuad
            {
                Center = (middle + across * side) with { Y = RailY + 0.03f },
                Length = length,
                Width = 0.18f,
                Yaw = yaw,
                Color = colour,
                Flags = (uint)RoadFlags.None,
                Layer = CityLayer.Rail,
            });
        }

        if (weight == 0) return;   // a dead line gets no trains: that is the signal

        // One train, length scaled by how much traffic the reference actually carries.
        int carriages = Math.Clamp(2 + weight / 6, 2, 7);
        int pathIndex = TrafficNetwork.AddPath(scene,
            new[] { start with { Y = RailY + 0.5f }, end with { Y = RailY + 0.5f } }, loop: false);

        for (int i = 0; i < carriages; i++)
        {
            scene.Travellers.Add(new Traveller
            {
                PathIndex = pathIndex,
                // Carriages are coupled: a small constant offset keeps them nose to tail.
                Phase = i * (3.2f / MathF.Max(length, 1f)),
                Speed = 16f,
                Color = i == 0
                    ? new Vector4(0.90f, 0.72f, 0.25f, 1f)      // locomotive
                    : new Vector4(0.34f, 0.40f, 0.52f, 1f),
                Kind = TravellerKind.Truck,
                Layer = CityLayer.Rail,
            });
        }
    }

    /// <summary>
    /// An apron, a control tower and circling aircraft, sized by how many external packages the
    /// project takes on.
    /// </summary>
    static bool LayAirport(SceneGraph scene, ProjectNode project, Bounds2 district)
    {
        int packages = project.PackageReferences.Count;
        if (packages == 0) return false;

        // Tucked into a corner of the district, the way real airports sit outside the centre.
        // A cramped district simply doesn't get one â€” Math.Clamp would throw here, because on a tiny
        // district the available room falls below the minimum apron.
        float room = MathF.Min(district.Width, district.Depth) * 0.30f;
        if (room < 6f) return false;
        float apron = MathF.Min(8f + packages * 1.6f, room);
        var centre = new Vector3(
            district.X + district.Width - apron * 0.6f,
            0f,
            district.Z + apron * 0.6f);

        scene.Roads.Add(new RoadQuad
        {
            Center = centre with { Y = CityLayout.PlazaSurfaceY },
            Length = apron,
            Width = apron * 0.6f,
            Yaw = 0f,
            Color = new Vector4(0.30f, 0.30f, 0.32f, 1f),
            Flags = (uint)RoadFlags.EdgeLines,
            Layer = CityLayer.Air,
        });

        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = centre with { X = centre.X - apron * 0.4f },
            Size = new Vector3(2.2f, 12f, 2.2f),
            Color = new Vector4(0.58f, 0.57f, 0.54f, 1f),
            PickId = -1,
            Detail = 1f,
        });
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = new Vector3(centre.X - apron * 0.4f, 12f, centre.Z),
            Size = new Vector3(4f, 2.4f, 4f),
            Color = new Vector4(0.35f, 0.55f, 0.70f, 0.75f),
            PickId = -1,
            Detail = 1f,
        });

        scene.Labels.Add(new WorldLabel
        {
            Position = new Vector3(centre.X - apron * 0.4f, 16f, centre.Z),
            Text = "AIRPORT",
            Subtitle = $"{packages} external packages",
            Size = 1.1f,
            Color = new Vector4(0.72f, 0.88f, 1.00f, 1f),
            FadeDistance = 200f,
            Priority = 6500,
        });

        AddApproach(scene, centre, apron, packages);
        return true;
    }

    /// <summary>Aircraft on a holding circuit above the district â€” cargo arriving from outside.</summary>
    static void AddApproach(SceneGraph scene, Vector3 centre, float apron, int packages)
    {
        const int Segments = 20;
        float radius = apron * 2.2f;
        float height = 46f;

        var circuit = new Vector3[Segments + 1];
        for (int i = 0; i <= Segments; i++)
        {
            float angle = MathF.Tau * (i % Segments) / Segments;
            circuit[i] = new Vector3(
                centre.X + MathF.Cos(angle) * radius,
                // Gentle climb and descent, so the circuit doesn't read as a flat ring.
                height + MathF.Sin(angle * 2f) * 6f,
                centre.Z + MathF.Sin(angle) * radius * 0.7f);
        }

        int pathIndex = TrafficNetwork.AddPath(scene, circuit, loop: true);
        int aircraft = Math.Clamp(packages / 6, 1, 4);

        for (int i = 0; i < aircraft; i++)
        {
            scene.Travellers.Add(new Traveller
            {
                PathIndex = pathIndex,
                Phase = (float)i / aircraft,
                Speed = 22f,
                Color = new Vector4(0.92f, 0.94f, 0.98f, 1f),
                Kind = TravellerKind.Plane,
                Layer = CityLayer.Air,
            });
        }
    }
}

