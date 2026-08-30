using CSharpCity.Model;

namespace CSharpCity.Layout;

/// <summary>
/// Throws the overlay away and builds it again from a newer snapshot.
/// </summary>
/// <remarks>
/// The only part of a built city that may be changed after <see cref="CityLayout.Build"/> has
/// returned, and the reason the split between the city and the overlay is worth having at all.
/// Somebody opens a pull request, the overlay is rebuilt, and not one building has moved: the
/// street plan, the districts, the roads and the terrain are all derived from source, and source
/// has not changed.
///
/// Everything removed here is identified by its layer rather than by remembering what was added,
/// which is why <see cref="PointOfInterest"/> and <see cref="WorldLabel"/> carry one. A rebuild that
/// left old tour stops behind would fly you to a building site that had already been merged.
/// </remarks>
public static class Overlay
{
    const CityLayer Volatile = CityLayer.Works | CityLayer.Backlog;

    public static void Rebuild(SceneGraph scene, CityModel model, GitHubSnapshot github)
    {
        scene.Boxes.RemoveAll(b => (b.Layer & Volatile) != 0);
        scene.Interest.RemoveAll(i => (i.Layer & Volatile) != 0);
        scene.Labels.RemoveAll(l => (l.Layer & Volatile) != 0);

        if (github.Available) Works.Apply(scene, model, github);
    }
}
