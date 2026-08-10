using System.Numerics;
using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// The renderable city: flat arrays of boxes and props, already positioned.
/// Pure data — no OpenGL types leak in here, so this is unit-testable.
/// </summary>
public sealed class SceneGraph
{
    public List<BoxInstance> Boxes { get; } = new();
    public List<GroundQuad> Ground { get; } = new();
    /// <summary>Street grid, dependency routes and roundabouts. Flat, rotatable, road-marked.</summary>
    public List<RoadQuad> Roads { get; } = new();
    /// <summary>Routes through the street grid. Traffic follows these; nothing travels in a straight line.</summary>
    public List<TrafficPath> Paths { get; } = new();
    /// <summary>Traffic. Positions are computed per frame by the renderer from elapsed time.</summary>
    public List<Traveller> Travellers { get; } = new();
    /// <summary>Where each type ended up, so dependency roads can be routed after layout.</summary>
    public Dictionary<string, BuildingSite> Sites { get; } = new(StringComparer.Ordinal);
    /// <summary>Each project's district footprint, so rail can connect them once all are placed.</summary>
    public Dictionary<string, Bounds2> Districts { get; } = new(StringComparer.Ordinal);
    /// <summary>Places worth flying to: the incidents and landmarks that show the city is alive.</summary>
    public List<PointOfInterest> Interest { get; } = new();
    /// <summary>The mountain ring, as a real triangle mesh. Null when the city has no border.</summary>
    public TerrainMesh? Terrain { get; set; }
    /// <summary>The city's worst buildings, ranked. The shortlist to actually go and fix.</summary>
    public List<WorstEntry> Worst { get; } = new();


    /// <summary>
    /// The one drivable network. Junctions are nodes, stretches of road are edges, and everything
    /// that drives — ambient cars, the ride-along, highway traffic — routes over this and only this.
    /// </summary>
    public RoadGraph RoadNetwork { get; set; } = new();

    /// <summary>Where a car can join the road from each building. Also the picker's destinations.</summary>
    public List<CarSpawn> CarSpawns { get; } = new();

    /// <summary>
    /// Signal heads, one per approach. Their lamps are coloured each frame from the signal's phase,
    /// because a light that never changes is just a decoration.
    /// </summary>
    public List<SignalHead> SignalHeads { get; } = new();

    /// <summary>Layout scratch: every road the treemap cut, before crossings are worked out.</summary>
    internal List<RoadCut> RoadCuts { get; } = new();

    /// <summary>
    /// Layout scratch: each building's plot and how much of it the building actually uses. Read
    /// after the road network exists, so leftover ground can be filled without paving over a road.
    /// </summary>
    internal List<LotRecord> Lots { get; } = new();
    /// <summary>Per-building metadata for the crosshair inspection HUD, indexed by <see cref="BoxInstance.PickId"/>.</summary>
    public List<PickInfo> PickInfos { get; } = new();
    /// <summary>Billboarded signage: what each building and district is.</summary>
    public List<WorldLabel> Labels { get; } = new();

    /// <summary>Buildings forced to overrun their lot because the treemap cell was too small.</summary>
    public int CrampedLots { get; set; }

    public Vector3 SpawnPosition { get; set; } = new(0, 1.7f, 0);
    public float SpawnYaw { get; set; } = -90f;
    public Bounds2 CityBounds { get; set; } = new(0, 0, 1, 1);
}

/// <summary>An axis-aligned box, positioned by the centre of its <em>base</em> so buildings sit on the ground.</summary>
public struct BoxInstance
{
    public Vector3 BasePosition;
    public Vector3 Size;
    public Vector4 Color;
    /// <summary>Index into <see cref="SceneGraph.PickInfos"/>, or -1 for scenery that can't be inspected.</summary>
    public int PickId;
    /// <summary>Bitfield consumed by the fragment shader. See <see cref="BoxFlags"/>.</summary>
    public uint Flags;
    /// <summary>Windows across this facade. One floor box = one storey, so this is the method's parameter count.</summary>
    public float Detail;
    /// <summary>Fraction of window panes smashed, 0..1. Driven by nullable-reference warnings.</summary>
    public float Damage;
    /// <summary>Which toggleable layer this belongs to. Default (0) is always drawn.</summary>
    public CityLayer Layer;
}

[Flags]
public enum BoxFlags : uint
{
    None = 0,
    /// <summary>Draw the window grid on the facade.</summary>
    Windows = 1 << 0,
    /// <summary>Windows glow at night — this type's members are public.</summary>
    LitWindows = 1 << 1,
    /// <summary>Overlay grime/cracks, intensity carried in the alpha of a secondary channel.</summary>
    Grimy = 1 << 2,
    /// <summary>Translucent glass (interfaces).</summary>
    Glass = 1 << 3,
    /// <summary>Boarded windows, no lights (dead code).</summary>
    Abandoned = 1 << 4,
    /// <summary>Scaffolding stripes (abstract classes).</summary>
    Scaffold = 1 << 5,
    /// <summary>Self-lit; ignores the sun. Hazard lights.</summary>
    Emissive = 1 << 6,
    /// <summary>
    /// Live flame: flickers over time and runs a red-to-yellow gradient up its own height. Brighter
    /// than <see cref="Emissive"/>, and brighter still at night.
    /// </summary>
    Fire = 1 << 7,
    /// <summary>Smoke: sways and swells as it rises, and drifts darker with height.</summary>
    Smoke = 1 << 8,
    /// <summary>Emergency beacon: strobes between its own colour and white, and blazes at night.</summary>
    Beacon = 1 << 9,
    /// <summary>Pressurised water: shimmers, wobbles along its length, and is lit at night.</summary>
    Water = 1 << 10,
    /// <summary>
    /// Tapered to a point, with a reflective band round it: a traffic cone rather than a box.
    /// </summary>
    /// <remarks>
    /// Done in the vertex shader rather than by shipping cone geometry. Everything in this city is
    /// one instanced cube, and adding a second mesh for a prop half a metre tall would cost a draw
    /// call and a buffer for something a single multiply achieves.
    /// </remarks>
    Cone = 1 << 11,
    /// <summary>
    /// A cylinder rather than a box: round about whichever axis the shape is thinnest on.
    /// </summary>
    /// <remarks>
    /// Carved out of the cube in the fragment shader by discarding everything outside the circle,
    /// so it stays one instance in the same buffer as everything else. Meant for the handful of
    /// things that read as obviously wrong when square — a bicycle wheel most of all.
    /// </remarks>
    Round = 1 << 12,
}

/// <summary>One row of the worst-buildings ranking.</summary>
public sealed class WorstEntry
{
    public string Name = "";
    public string Project = "";
    /// <summary>What earned it the place — the two or three worst things about it.</summary>
    public string Reason = "";
    public int Score;
    public Vector3 Position;
}

/// <summary>
/// Somewhere the city is doing something. The cinematic tour flies between these, which is the
/// only way most of them will ever be found in a 1.4 km city.
/// </summary>
public sealed class PointOfInterest
{
    /// <summary>What the camera should end up looking at.</summary>
    public Vector3 Focus;
    /// <summary>How far back to stand. Bigger subjects need more room.</summary>
    public float Distance = 34f;
    public string Headline = "";
    public string Detail = "";
}

/// <summary>Where a type's building stands. Roads are routed between these after layout finishes.</summary>
public sealed class BuildingSite
{
    public Vector3 Center;
    public float Side;
    public string ProjectName = "";
    /// <summary>Decides whether a dependency is foot traffic, street traffic or a cross-project haul.</summary>
    public string Namespace = "";
    /// <summary>
    /// Index into <see cref="SceneGraph.PickInfos"/>, joining this position to the building's name.
    /// </summary>
    /// <remarks>
    /// The two halves of a building lived in separate structures with nothing linking them:
    /// <see cref="PickInfo"/> knows what a building is called and not where it is, and this knows
    /// where it is and not what it is called. Anything that has to show a named place — the
    /// destination picker, a car setting off from a doorway — needs both.
    /// </remarks>
    public int PickId = -1;
}

/// <summary>A building's plot, the ground it stands on, and how much of it it uses.</summary>
internal readonly record struct LotRecord(Bounds2 Lot, Vector3 Centre, float Side, bool IsLandmark,
    int Seed);

/// <summary>Where a car can pull out of a building's frontage onto the road.</summary>
public readonly record struct CarSpawn(int Edge, float Along, Vector3 Kerbside, int PickId,
    string TypeId);

/// <summary>
/// A flat, yaw-rotated slab of road surface. Unlike <see cref="BoxInstance"/> these rotate, because
/// a dependency road runs at whatever angle the two buildings happen to sit at.
/// </summary>
public struct RoadQuad
{
    /// <summary>Centre of the slab. Y is the surface height above the ground plane.</summary>
    public Vector3 Center;
    /// <summary>Extent along the road's direction.</summary>
    public float Length;
    /// <summary>Extent across the road.</summary>
    public float Width;
    /// <summary>Radians about Y. 0 means the road runs along +X.</summary>
    public float Yaw;
    /// <summary>Radians of climb along the road's length. Non-zero only for highway ramps.</summary>
    public float Pitch;
    public Vector4 Color;
    public uint Flags;
    /// <summary>Which toggleable layer this belongs to. Default (0) is always drawn.</summary>
    public CityLayer Layer;
}

/// <summary>
/// Optional visual layer a piece of scenery belongs to, so it can be switched off to study the
/// others. Zero means "always visible" — only layers worth isolating are tagged.
/// </summary>
[Flags]
public enum CityLayer : uint
{
    Always = 0,
    /// <summary>The worn ground paths themselves. Separate from the people on them.</summary>
    Footpaths = 1 << 0,
    /// <summary>
    /// The walkers. Kept apart from <see cref="Footpaths"/> because the people carry the signal —
    /// density is the reference count — while the paths are just the ground they wear down.
    /// </summary>
    Walkers = 1 << 6,
    Rail = 1 << 1,
    Roundabouts = 1 << 2,
    Smog = 1 << 3,
    Highways = 1 << 4,
    Air = 1 << 5,
    /// <summary>Kerbs and everything standing on them. Off-switch for a busy street scene.</summary>
    Sidewalks = 1 << 7,
}

[Flags]
public enum RoadFlags : uint
{
    None = 0,
    /// <summary>Solid white lines down both edges.</summary>
    EdgeLines = 1 << 0,
    /// <summary>Dashed white centre line — a two-way street rather than an alley.</summary>
    DashedCenter = 1 << 1,
    /// <summary>Yellow/black diagonal hazard stripes. Circular dependencies.</summary>
    Hazard = 1 << 2,
    /// <summary>Self-lit centre stripe, so dependency routes read at night.</summary>
    Glow = 1 << 3,
    /// <summary>A worn dirt/stone desire line between two buildings. No markings, soft edges.</summary>
    Footpath = 1 << 4,
    /// <summary>Ballast with sleepers banded across it.</summary>
    Rail = 1 << 5,
    /// <summary>Still water with a slow ripple across it.</summary>
    Pond = 1 << 6,
    /// <summary>Sports surface: perimeter, halfway line and centre circle painted on.</summary>
    Court = 1 << 7,
    /// <summary>Car park: bay markings across the short axis, with an aisle down the middle.</summary>
    Parking = 1 << 8,
    /// <summary>The pool a lamp casts: a soft disc, night only, and never lit by the sun.</summary>
    LightPool = 1 << 9,
}

/// <summary>
/// A route through the city, as a polyline of waypoints. Produced by pathfinding around buildings,
/// so it follows streets rather than cutting across lots.
/// </summary>
public sealed class TrafficPath
{
    public Vector3[] Points = Array.Empty<Vector3>();
    /// <summary>Arc length at each point, so a traveller can be placed by distance travelled.</summary>
    public float[] Cumulative = Array.Empty<float>();
    public float Length;
    /// <summary>True for roundabouts: the path closes on itself and traffic never leaves.</summary>
    public bool Loop;
}

public enum TravellerKind
{
    /// <summary>Two types in the same namespace talking to each other. Foot traffic.</summary>
    Pedestrian,
    /// <summary>Across namespaces inside one project. Street traffic.</summary>
    Car,
    /// <summary>Across project boundaries. The expensive haul.</summary>
    Truck,
    /// <summary>Waterfowl on a pond. Tiny, slow, and entirely decorative.</summary>
    Duck,
    /// <summary>
    /// An aeroplane on a flight path, and a helicopter on an orbit.
    /// </summary>
    /// <remarks>
    /// These used to be trucks. Nothing checked, because the traveller shapes only had wheeled
    /// vehicles in them and a truck was the closest thing to hand — so every airport in the city
    /// had lorries circling above it, and the helicopter watching the worst building was a lorry
    /// too. It is the "flying cars" you see, and they were flying cars quite literally.
    /// </remarks>
    Plane,
    Helicopter,
}

/// <summary>
/// One vehicle or person. The renderer advances it along its path from elapsed time, so traffic
/// animates without the layout knowing anything about frames.
/// </summary>
public struct Traveller
{
    public int PathIndex;
    /// <summary>Starting offset along the path, 0..1, so traffic doesn't move in lockstep.</summary>
    public float Phase;
    public float Speed;
    public Vector4 Color;
    public TravellerKind Kind;
    /// <summary>Hidden along with the layer it belongs to, so trains vanish with their track.</summary>
    public CityLayer Layer;
}

public struct GroundQuad
{
    public Vector3 BasePosition;
    /// <summary>X and Z extent; Y is ignored.</summary>
    public Vector2 Size;
    public Vector4 Color;
}

/// <summary>A billboarded sign in world space. Always faces the camera.</summary>
public sealed class WorldLabel
{
    public Vector3 Position;
    public string Text = "";
    /// <summary>Second, smaller line. Carries the kind and metrics under a type's name.</summary>
    public string? Subtitle;
    /// <summary>World units per em. District banners are much larger than building nameplates.</summary>
    public float Size = 1.2f;
    public Vector4 Color = new(1f, 1f, 1f, 1f);
    /// <summary>Beyond this many metres the label fades out, so distant districts don't turn to mush.</summary>
    public float FadeDistance = 140f;
    /// <summary>
    /// If non-zero, the label is mounted on a building wall rather than floating: the renderer slides
    /// it this far out from <see cref="Position"/> toward the viewer, so it rides whichever facade you
    /// are actually looking at instead of being buried inside the geometry.
    /// </summary>
    public float FaceRadius;
    /// <summary>Higher wins when two labels fight for the same patch of screen.</summary>
    public int Priority;
}

public sealed class PickInfo
{
    public string DisplayName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Loc { get; set; }
    public double AvgComplexity { get; set; }
    public TypeKind Kind { get; set; }
    public List<string> SmellLabels { get; set; } = new();
    /// <summary>Compiler diagnostics, already formatted. What turns visible decay into a fix.</summary>
    public List<string> DiagnosticLabels { get; set; } = new();
}

/// <summary>An axis-aligned rectangle in the ground plane (X/Z).</summary>
public readonly struct Bounds2
{
    public readonly float X, Z, Width, Depth;

    public Bounds2(float x, float z, float width, float depth)
    {
        X = x; Z = z; Width = width; Depth = depth;
    }

    public float Area => Width * Depth;
    public float CenterX => X + Width * 0.5f;
    public float CenterZ => Z + Depth * 0.5f;

    public Bounds2 Deflate(float margin)
    {
        var w = MathF.Max(0f, Width - margin * 2f);
        var d = MathF.Max(0f, Depth - margin * 2f);
        return new Bounds2(X + (Width - w) * 0.5f, Z + (Depth - d) * 0.5f, w, d);
    }

    /// <summary>
    /// Deflate by <paramref name="desired"/>, but never by more than a fixed share of the rectangle.
    /// A fixed margin applied at every level of a deep namespace tree consumes the whole block and
    /// silently deletes the buildings inside it.
    /// </summary>
    public Bounds2 DeflateCapped(float desired, float maxFraction = 0.14f)
    {
        var limit = MathF.Min(Width, Depth) * maxFraction;
        return Deflate(MathF.Min(desired, limit));
    }
}
