namespace CSharpCity.Layout;

/// <summary>
/// Works out where each building meets the road, which is where its traffic comes from and goes to.
/// </summary>
internal static class Kerbs
{
    /// <summary>
    /// How far past its own footprint a building will look for a road. Generous, because a lot in
    /// the middle of a large block is genuinely some way from the nearest street.
    /// </summary>
    const float SearchMargin = 40f;

    /// <summary>
    /// How far from the ends of a road a car will pull out.
    /// </summary>
    /// <remarks>
    /// Projecting a building onto the nearest road clamps to the road's ends, so every building
    /// near a corner lands on the junction itself — and two buildings by the same corner, on two
    /// roads that meet there, get the *same* point. Two cars then pull out of what is geometrically
    /// one spot on two different roads, each correctly seeing its own lane as empty, and end up
    /// occupying the same cubic metre.
    /// </remarks>
    const float JunctionClearance = 5f;

    public static void BuildSpawns(SceneGraph scene, RoadGraph graph)
    {
        if (graph.IsEmpty) return;

        foreach (var (typeId, site) in scene.Sites)
        {
            float reach = site.Side * 0.5f + SearchMargin;
            if (!graph.TryNearestEdge(site.Center, reach, out int edge, out float along)) continue;

            // Only the main network. A building whose nearest road is a stranded fragment would
            // spawn cars that can never get anywhere, and be a destination nothing can reach.
            if (graph.Nodes[graph.Edges[edge].A].Component != graph.MainComponent) continue;
            if (graph.Edges[edge].Kind is RoadKind.HighwayDeck or RoadKind.HighwayRamp) continue;

            float length = graph.Edges[edge].Length;
            if (length < JunctionClearance * 2f + 2f) continue;
            along = Math.Clamp(along, JunctionClearance, length - JunctionClearance);

            scene.CarSpawns.Add(new CarSpawn(edge, along, graph.PointOn(edge, along),
                site.PickId, typeId));
        }
    }
}
