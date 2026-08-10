using System.Numerics;

namespace CSharpCity.Layout;

/// <summary>
/// A circular dependency becomes a roundabout the cycle's members sit around, ringed with hazard
/// stripes and circled by traffic that never leaves.
/// </summary>
/// <remarks>
/// This is the one place the city gets geometry that isn't already there. It earns the exception:
/// a cycle is precisely the thing you cannot read off routed traffic, because every individual
/// route in a cycle looks like an ordinary trip. Only the closed loop shows it.
/// </remarks>
internal static class Roundabout
{
    const int Segments = 32;
    const float LaneWidth = 4.5f;

    public static void Lay(SceneGraph scene, List<string> group)
    {
        var centres = group.Select(id => scene.Sites[id].Center).ToList();
        if (centres.Count == 0) return;

        var centre = new Vector3(centres.Average(c => c.X), CityLayout.RoundaboutSurfaceY,
            centres.Average(c => c.Z));

        // Big enough to enclose the members where they cluster, capped so a cycle spread across a
        // district doesn't pave the whole thing.
        float spread = centres.Max(c => Vector2.Distance(
            new Vector2(c.X, c.Z), new Vector2(centre.X, centre.Z)));
        float radius = Math.Clamp(spread * 0.75f, 9f, 34f);

        // Which parts of the ring land on open ground.
        //
        // The centre is the mean of the cycle's members, and nothing ever checked what was there.
        // When a cycle's members are spread across a district that mean falls inside a block, and
        // the ring gets painted straight over the rooftops — with its traffic circling a metre
        // above them. That is the "cars flying over buildings, far from any street" you can see
        // from the air and never find on foot.
        var open = new bool[Segments];
        int blocked = 0;
        for (int i = 0; i < Segments; i++)
        {
            float angle = 2f * MathF.PI * i / Segments;
            var at = new Vector3(centre.X + MathF.Cos(angle) * radius, 0f,
                centre.Z + MathF.Sin(angle) * radius);

            open[i] = !OverlapsABuilding(scene, at);
            if (!open[i]) blocked++;
        }

        // Mostly buried: better to keep the label and the tour stop, which are what actually carry
        // the finding, than to draw a broken circle over somebody's roof.
        if (blocked > Segments * 0.4f)
        {
            AddMarker(scene, group, centre, radius);
            return;
        }

        float arc = 2f * MathF.PI * radius / Segments;
        for (int i = 0; i < Segments; i++)
        {
            if (!open[i]) continue;

            float angle = 2f * MathF.PI * i / Segments;
            scene.Roads.Add(new RoadQuad
            {
                Center = new Vector3(
                    centre.X + MathF.Cos(angle) * radius,
                    CityLayout.RoundaboutSurfaceY,
                    centre.Z + MathF.Sin(angle) * radius),
                // Slight overlap so the ring has no gaps between its straight chords.
                Length = arc * 1.08f,
                Width = LaneWidth,
                Yaw = angle + MathF.PI * 0.5f,
                Color = new Vector4(0.20f, 0.16f, 0.06f, 1f),
                Flags = (uint)RoadFlags.Hazard,
                Layer = CityLayer.Roundabouts,
            });
        }

        // No traffic on the ring.
        //
        // It used to circle forever, on the reasoning that a cycle is traffic that can never leave.
        // The trouble is that everything else on wheels in this city is now a real car with a route
        // and somewhere to be, so a handful going round and round in a circle no longer reads as a
        // metaphor — it reads as cars that are lost. The hazard-striped ring, the label and the
        // tour stop carry the finding perfectly well on their own.
        scene.Interest.Add(new PointOfInterest
        {
            Focus = centre with { Y = 4f },
            Distance = radius * 2.2f + 24f,
            Headline = "CIRCULAR DEPENDENCY",
            Detail = string.Join(" -> ", group.Take(3).Select(ShortName)),
        });

        scene.Labels.Add(new WorldLabel
        {
            Position = centre with { Y = 8f },
            Text = "CIRCULAR DEPENDENCY",
            Subtitle = string.Join(" -> ", group.Take(3).Select(ShortName))
                       + (group.Count > 3 ? $" -> +{group.Count - 3} more" : " -> ..."),
            Size = 1.5f,
            Color = new Vector4(1.00f, 0.42f, 0.28f, 1f),
            FadeDistance = 220f,
            Priority = 8000,
        });
    }

    /// <summary>Whether a point falls inside any building's footprint.</summary>
    static bool OverlapsABuilding(SceneGraph scene, Vector3 at)
    {
        foreach (var site in scene.Sites.Values)
        {
            float half = site.Side * 0.5f + LaneWidth * 0.5f;
            if (MathF.Abs(at.X - site.Center.X) < half && MathF.Abs(at.Z - site.Center.Z) < half)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The cycle, with no ring: a label and a tour stop where the roundabout would have gone.
    /// </summary>
    /// <remarks>
    /// What makes a circular dependency findable is being told it exists and being flown to it. The
    /// tarmac is the illustration, and an illustration drawn across three roofs is worse than none.
    /// </remarks>
    static void AddMarker(SceneGraph scene, List<string> group, Vector3 centre, float radius)
    {
        scene.Interest.Add(new PointOfInterest
        {
            Focus = centre with { Y = 4f },
            Distance = radius * 2.2f + 24f,
            Headline = "CIRCULAR DEPENDENCY",
            Detail = string.Join(" -> ", group.Take(3).Select(ShortName)),
        });

        scene.Labels.Add(new WorldLabel
        {
            Position = centre with { Y = 8f },
            Text = "CIRCULAR DEPENDENCY",
            Subtitle = string.Join(" -> ", group.Take(3).Select(ShortName))
                       + (group.Count > 3 ? $" -> +{group.Count - 3} more" : " -> ..."),
            Size = 1.5f,
            Color = new Vector4(1.00f, 0.42f, 0.28f, 1f),
            FadeDistance = 220f,
            Priority = 8000,
        });
    }

    static string ShortName(string id)
    {
        var trimmed = id.Replace("global::", "");
        int dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }
}

