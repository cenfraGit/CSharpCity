using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Police, fire and ambulance responding to the worst problems in the city, plus a single police
/// helicopter over the worst building of all.
/// </summary>
/// <remarks>
/// This layer answers a question the rest of the city is bad at: <em>where do I go first?</em>
///
/// Everything here is deliberately rare. The city already carries a thousand findings as quiet
/// conditions — grime, broken glass, litter — and those read in aggregate. An emergency response is
/// the opposite: it must be scarce or it stops meaning anything. So incidents are capped to the
/// worst handful in the entire solution, not applied per-threshold. Eight crime scenes across 1,164
/// buildings is a signal; two hundred would be wallpaper.
/// </remarks>
internal static class EmergencyServices
{
    /// <summary>At most this many of each response type, city-wide.</summary>
    const int MaxResponses = 6;

    static readonly Vector4 PoliceBody = new(0.10f, 0.16f, 0.42f, 1f);
    static readonly Vector4 FireBody = new(0.62f, 0.08f, 0.06f, 1f);
    static readonly Vector4 Ambulance = new(0.90f, 0.91f, 0.93f, 1f);
    static readonly Vector4 Tape = new(0.95f, 0.78f, 0.06f, 1f);
    static readonly Vector4 Blue = new(0.15f, 0.35f, 1.0f, 1f);
    static readonly Vector4 Red = new(1.0f, 0.12f, 0.10f, 1f);

    public sealed record Result(int CrimeScenes, int Fires, int Leaks, string? WorstBuilding);

    public static Result Build(SceneGraph scene, CityModel model)
    {
        var types = model.Projects.SelectMany(p => p.Types)
            .Where(t => scene.Sites.ContainsKey(t.Id))
            .ToList();

        // Security first: these are the rarest and the most worth walking to.
        var crimes = types.Where(t => t.SecurityFindings > 0)
            .OrderByDescending(t => t.SecurityFindings)
            .Take(MaxResponses).ToList();
        foreach (var type in crimes)
        {
            var site = scene.Sites[type.Id];
            AddCrimeScene(scene, site);
            AddSignalBeam(scene, site, type);
            Mark(scene, site, "SECURITY SCENE",
                $"{type.Name} · {type.SecurityFindings} finding(s)");
        }

        var burning = types
            .Select(t => (Type: t, Fires: t.Smells.FirstOrDefault(s => s.Kind == SmellKind.EmptyCatch)?.Count ?? 0))
            .Where(x => x.Fires > 0)
            .OrderByDescending(x => x.Fires)
            .Take(MaxResponses).ToList();
        foreach (var (type, fires) in burning)
        {
            AddFireResponse(scene, scene.Sites[type.Id]);
            Mark(scene, scene.Sites[type.Id], "FIRE",
                $"{type.Name} · {fires} swallowed exception(s)");
        }

        var leaking = types.Where(t => t.LeakFindings > 0)
            .OrderByDescending(t => t.LeakFindings)
            .Take(MaxResponses).ToList();
        foreach (var type in leaking)
        {
            AddAmbulance(scene, scene.Sites[type.Id]);
            Mark(scene, scene.Sites[type.Id], "RESOURCE LEAK",
                $"{type.Name} · {type.LeakFindings} undisposed");
        }

        // One helicopter, over the single worst building in the solution.
        var worst = types.OrderByDescending(Severity).FirstOrDefault();
        if (worst is not null && Severity(worst) > 0)
        {
            AddHelicopter(scene, scene.Sites[worst.Id]);
            // Deliberately first in the list, so the tour opens on the thing that matters most.
            scene.Interest.Insert(0, new PointOfInterest
            {
                Focus = scene.Sites[worst.Id].Center with { Y = 14f },
                Distance = 62f,
                Headline = "WORST IN CITY",
                Detail = $"{worst.Name} · {worst.Loc} LOC · {worst.Methods.Count} methods",
            });
        }

        RankWorst(scene, types);
        return new Result(crimes.Count, burning.Count, leaking.Count, worst?.Name);
    }

    /// <summary>
    /// The shortlist: the ten buildings most worth a developer's time, in order.
    /// </summary>
    /// <remarks>
    /// Every other channel shows one problem in one place. This answers the question a person
    /// actually arrives with — "there are 1,180 findings, where do I start?" — which no amount of
    /// walking around reliably answers on a 1.4 km map.
    /// </remarks>
    static void RankWorst(SceneGraph scene, List<TypeNode> types)
    {
        foreach (var type in types.OrderByDescending(Severity).Take(10))
        {
            if (Severity(type) <= 0) break;

            scene.Worst.Add(new WorstEntry
            {
                Name = type.Name,
                Project = scene.Sites[type.Id].ProjectName,
                Reason = Reason(type),
                Score = Severity(type),
                Position = scene.Sites[type.Id].Center,
            });
        }
    }

    /// <summary>The two or three things most responsible for a building's score.</summary>
    static string Reason(TypeNode type)
    {
        var parts = new List<(int Weight, string Text)>();

        if (type.CompileErrors > 0) parts.Add((type.CompileErrors * 40, $"{type.CompileErrors} errors"));
        if (type.SecurityFindings > 0)
            parts.Add((type.SecurityFindings * 25, $"{type.SecurityFindings} security"));

        int fires = type.Smells.FirstOrDefault(s => s.Kind == SmellKind.EmptyCatch)?.Count ?? 0;
        if (fires > 0) parts.Add((fires * 8, $"{fires} swallowed exc"));
        if (type.LeakFindings > 0) parts.Add((type.LeakFindings * 6, $"{type.LeakFindings} leaks"));
        if (type.NullWarnings > 0) parts.Add((type.NullWarnings * 2, $"{type.NullWarnings} nullable"));
        if (type.AnalyzerWarnings > 0) parts.Add((type.AnalyzerWarnings, $"{type.AnalyzerWarnings} findings"));

        return parts.Count == 0
            ? $"{type.Loc} LOC"
            : string.Join(" · ", parts.OrderByDescending(p => p.Weight).Take(3).Select(p => p.Text));
    }

    static void Mark(SceneGraph scene, BuildingSite site, string headline, string detail) =>
        scene.Interest.Add(new PointOfInterest
        {
            Focus = site.Center with { Y = 6f },
            Distance = MathF.Max(site.Side * 1.6f, 30f),
            Headline = headline,
            Detail = detail,
        });

    /// <summary>
    /// How bad a type is, all in. Errors dominate, then security, then everything that leaks or
    /// burns; ordinary untidiness barely registers, which is the point.
    /// </summary>
    static int Severity(TypeNode type) =>
        type.CompileErrors * 40
        + type.SecurityFindings * 25
        + type.LeakFindings * 6
        + (type.Smells.FirstOrDefault(s => s.Kind == SmellKind.EmptyCatch)?.Count ?? 0) * 8
        + type.NullWarnings * 2
        + type.AnalyzerWarnings;

    /// <summary>
    /// A searchlight firing straight up out of the lot, so a crime scene can be found from anywhere
    /// in the city instead of only by walking into it.
    /// </summary>
    /// <remarks>
    /// Cordon tape and a patrol car read beautifully from ten metres and not at all from two hundred.
    /// With four scenes hidden among 1,164 buildings, the layer needed something that beats every
    /// skyline in the city — and a vertical beam is the one shape nothing else here makes.
    /// The beam widens and fades with height so it reads as light rather than a column.
    /// </remarks>
    static void AddSignalBeam(SceneGraph scene, BuildingSite site, TypeNode type)
    {
        const int Segments = 22;
        float top = 130f + type.SecurityFindings * 35f;   // worse scenes throw a taller beam
        var origin = site.Center + new Vector3(site.Side * 0.5f + 2.2f, 0f, 0f);

        // The lamp housing at the foot of the beam.
        scene.Boxes.Add(Box(origin, new Vector3(1.8f, 1.0f, 1.8f),
            new Vector4(0.22f, 0.23f, 0.26f, 1f)));
        scene.Boxes.Add(new BoxInstance
        {
            BasePosition = origin with { Y = 1.0f },
            Size = new Vector3(1.5f, 1.2f, 1.5f),
            Color = new Vector4(0.75f, 0.86f, 1.0f, 1f),
            PickId = -1,
            Flags = (uint)BoxFlags.Emissive,
            Detail = 1f,
        });

        for (int i = 0; i < Segments; i++)
        {
            float t = i / (float)Segments;
            float y = 2.2f + t * top;
            float span = (top / Segments) * 1.02f;   // slight overlap so the beam has no gaps

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = origin with { Y = y },
                // Widens with height, the way a real beam spreads.
                Size = new Vector3(1.5f + t * 5.5f, span, 1.5f + t * 5.5f),
                Color = new Vector4(0.58f, 0.78f, 1.0f, 0.30f * (1f - t * 0.82f)),
                PickId = -1,
                Flags = (uint)BoxFlags.Emissive,
                Detail = 1f,
            });
        }

        scene.Labels.Add(new WorldLabel
        {
            Position = origin with { Y = top * 0.55f },
            Text = "SECURITY",
            Subtitle = $"{type.Name} · {type.SecurityFindings} finding(s)",
            Size = 2.4f,
            Color = new Vector4(0.70f, 0.86f, 1.0f, 1f),
            FadeDistance = 900f,
            Priority = 8800,
        });
    }

    /// <summary>Cordon tape on posts, and a patrol car with its lights running.</summary>
    static void AddCrimeScene(SceneGraph scene, BuildingSite site)
    {
        float ring = site.Side * 0.5f + 3.2f;

        // Tape strung between posts around the front of the lot.
        for (int i = 0; i < 7; i++)
        {
            float angle = MathF.PI * (0.15f + i / 6f * 0.7f);
            var post = site.Center + new Vector3(MathF.Cos(angle) * ring, 0f, MathF.Sin(angle) * ring);

            scene.Boxes.Add(Box(post, new Vector3(0.12f, 1.15f, 0.12f),
                new Vector4(0.30f, 0.30f, 0.32f, 1f)));
            scene.Boxes.Add(Box(post with { Y = 0.95f }, new Vector3(0.9f, 0.1f, 0.9f), Tape));
        }

        AddVehicle(scene, site, PoliceBody, 6.0f, lightbar: true);
    }

    /// <summary>
    /// A full turntable appliance: engine, outriggers, an extended ladder, and a monitor throwing a
    /// heavy jet into the upper floors, with spray bursting off the facade and water pooling below.
    /// </summary>
    /// <remarks>
    /// Sized like the real thing — around 10 m long against a 4 m patrol car — because at city scale
    /// a fire engine that reads as "a red car" tells you nothing. The jet is the point: it's what
    /// makes the response visible from a distance, so it's thick, lit, and aimed high up the facade.
    /// </remarks>
    static void AddFireResponse(SceneGraph scene, BuildingSite site)
    {
        const float Length = 10.5f;
        const float Width = 2.9f;
        float standOff = site.Side * 0.5f + 11f;
        var stand = site.Center + new Vector3(0f, 0f, standOff);

        // Chassis, pump housing, and a cab set forward of it.
        scene.Boxes.Add(Box(stand, new Vector3(Length, 1.05f, Width),
            new Vector4(0.16f, 0.16f, 0.17f, 1f)));
        scene.Boxes.Add(Box(stand with { Y = 1.05f }, new Vector3(Length * 0.62f, 2.0f, Width),
            FireBody));
        scene.Boxes.Add(Box(stand + new Vector3(Length * 0.36f, 1.05f, 0f),
            new Vector3(Length * 0.28f, 2.5f, Width), FireBody));
        scene.Boxes.Add(Box(stand + new Vector3(Length * 0.36f, 2.6f, 0f),
            new Vector3(Length * 0.24f, 1.0f, Width * 0.92f),
            new Vector4(0.20f, 0.26f, 0.34f, 1f)));

        // Outriggers braced against the road.
        for (int sx = -1; sx <= 1; sx += 2)
        for (int sz = -1; sz <= 1; sz += 2)
        {
            scene.Boxes.Add(Box(stand + new Vector3(Length * 0.28f * sx, 0.1f, Width * 0.75f * sz),
                new Vector3(0.5f, 0.9f, 1.5f), new Vector4(0.55f, 0.52f, 0.14f, 1f)));
        }

        // Turntable and the ladder running from it up to the fire floor.
        float tipHeight = 22f;
        var tip = site.Center + new Vector3(0f, tipHeight, site.Side * 0.5f + 3.5f);
        var basePoint = stand + new Vector3(-Length * 0.15f, 3.1f, 0f);

        scene.Boxes.Add(Box(basePoint, new Vector3(2.4f, 0.8f, 2.4f),
            new Vector4(0.50f, 0.47f, 0.13f, 1f)));

        for (int i = 0; i <= 16; i++)
        {
            float t = i / 16f;
            var at = Vector3.Lerp(basePoint with { Y = basePoint.Y + 0.8f }, tip, t);
            scene.Boxes.Add(Box(at, new Vector3(1.5f, 0.22f, 0.5f),
                new Vector4(0.78f, 0.66f, 0.14f, 1f)));
            // Side rails, so it reads as a ladder rather than a ramp.
            for (int s = -1; s <= 1; s += 2)
                scene.Boxes.Add(Box(at + new Vector3(0.65f * s, 0.22f, 0f),
                    new Vector3(0.18f, 0.5f, 0.42f), new Vector4(0.62f, 0.52f, 0.12f, 1f)));
        }

        // The monitor at the tip, and the jet it throws into the building.
        scene.Boxes.Add(Box(tip with { Y = tip.Y + 0.3f }, new Vector3(1.0f, 0.7f, 1.0f),
            new Vector4(0.35f, 0.34f, 0.32f, 1f)));

        var impact = site.Center + new Vector3(0f, tipHeight + 5f, site.Side * 0.5f);
        const int JetSegments = 18;

        for (int i = 0; i <= JetSegments; i++)
        {
            float t = i / (float)JetSegments;
            // Rises then falls, the way a real monitor arcs onto a facade.
            var at = Vector3.Lerp(tip, impact, t) + new Vector3(0f, MathF.Sin(t * MathF.PI) * 4.5f, 0f);
            // Fat at the nozzle, broadening again into spray as it breaks up on the wall.
            float bore = 1.5f - t * 0.7f + t * t * 1.9f;

            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = at,
                Size = new Vector3(bore, bore * 0.85f, bore),
                Color = new Vector4(0.60f, 0.80f, 1.0f, 0.62f - t * 0.18f),
                PickId = -1,
                Flags = (uint)BoxFlags.Water,
                Detail = 1f,
            });
        }

        // Spray bursting back off the facade.
        for (int i = 0; i < 9; i++)
        {
            float spread = (i / 8f - 0.5f) * 2f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = impact + new Vector3(spread * 5.5f, MathF.Abs(spread) * -3.5f + 2f,
                    -MathF.Abs(spread) * 1.2f),
                Size = new Vector3(3.0f, 2.6f, 2.4f),
                Color = new Vector4(0.78f, 0.90f, 1.0f, 0.34f),
                PickId = -1,
                Flags = (uint)BoxFlags.Water,
                Detail = 1f,
            });
        }

        // Water running off across the street.
        for (int i = 0; i < 5; i++)
        {
            float t = i / 4f;
            scene.Boxes.Add(new BoxInstance
            {
                BasePosition = Vector3.Lerp(site.Center with { Z = site.Center.Z + site.Side * 0.5f },
                    stand, t) with { Y = 0.16f },
                Size = new Vector3(6.5f - t * 2f, 0.09f, 5.0f - t * 1.5f),
                Color = new Vector4(0.30f, 0.44f, 0.60f, 0.45f),
                PickId = -1,
                Detail = 1f,
            });
        }

        // Beacons on the appliance roof.
        scene.Boxes.Add(Beacon(stand + new Vector3(-1.2f, 3.2f, 0f), Red));
        scene.Boxes.Add(Beacon(stand + new Vector3(1.2f, 3.2f, 0f), Blue));
    }

    static void AddAmbulance(SceneGraph scene, BuildingSite site) =>
        AddVehicle(scene, site, Ambulance, 4.2f, lightbar: true, length: 3.4f);

    /// <summary>Parks a vehicle on the street side of the lot. Returns where it stands.</summary>
    static Vector3 AddVehicle(SceneGraph scene, BuildingSite site, Vector4 body, float offset,
        bool lightbar, float length = 3.0f)
    {
        var stand = site.Center + new Vector3(0f, 0f, site.Side * 0.5f + offset);

        scene.Boxes.Add(Box(stand, new Vector3(length, 1.3f, 1.7f), body));
        scene.Boxes.Add(Box(stand with { Y = 1.3f }, new Vector3(length * 0.55f, 0.9f, 1.5f),
            new Vector4(0.18f, 0.20f, 0.24f, 1f)));

        if (!lightbar) return stand;

        // Two lamps, opposite colours, so the strobe alternates across the roof.
        scene.Boxes.Add(Beacon(stand + new Vector3(-0.5f, 2.2f, 0f), Blue));
        scene.Boxes.Add(Beacon(stand + new Vector3(0.5f, 2.2f, 0f), Red));
        return stand;
    }

    /// <summary>A police helicopter orbiting the worst building in the solution. There is only one.</summary>
    static void AddHelicopter(SceneGraph scene, BuildingSite site)
    {
        const int Segments = 24;
        float radius = MathF.Max(site.Side * 1.6f, 26f);
        float height = 54f;

        var orbit = new Vector3[Segments + 1];
        for (int i = 0; i <= Segments; i++)
        {
            float angle = MathF.Tau * (i % Segments) / Segments;
            orbit[i] = site.Center + new Vector3(
                MathF.Cos(angle) * radius,
                height + MathF.Sin(angle * 3f) * 2.5f,
                MathF.Sin(angle) * radius);
        }

        int path = TrafficNetwork.AddPath(scene, orbit, loop: true);
        scene.Travellers.Add(new Traveller
        {
            PathIndex = path,
            Phase = 0f,
            Speed = 17f,
            Color = new Vector4(0.16f, 0.18f, 0.22f, 1f),
            Kind = TravellerKind.Helicopter,
        });

        scene.Labels.Add(new WorldLabel
        {
            Position = site.Center with { Y = height + 7f },
            Text = "WORST IN CITY",
            Size = 2.0f,
            Color = new Vector4(1.0f, 0.45f, 0.35f, 1f),
            FadeDistance = 700f,
            Priority = 8500,
        });
    }

    static BoxInstance Box(Vector3 at, Vector3 size, Vector4 colour) => new()
    {
        BasePosition = at,
        Size = size,
        Color = colour,
        PickId = -1,
        Detail = 1f,
    };

    static BoxInstance Beacon(Vector3 at, Vector4 colour) => new()
    {
        BasePosition = at,
        Size = new Vector3(0.45f, 0.35f, 0.45f),
        Color = colour,
        PickId = -1,
        Flags = (uint)BoxFlags.Beacon,
        Detail = 1f,
    };
}
