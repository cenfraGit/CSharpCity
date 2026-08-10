using Microsoft.Build.Locator;

namespace CSharpCity.Analysis;

/// <summary>
/// Points MSBuildWorkspace at the installed .NET SDK.
/// </summary>
/// <remarks>
/// This must run before <em>any</em> Roslyn workspace type is loaded, which is why it lives in its
/// own class that touches nothing but <see cref="MSBuildLocator"/> — the JIT would otherwise resolve
/// MSBuild assemblies while jitting the calling method and register too late.
/// </remarks>
public static class MsBuildBootstrap
{
    static bool _registered;

    public static void Register()
    {
        if (_registered || MSBuildLocator.IsRegistered) return;

        var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
        if (instances.Count == 0)
            throw new InvalidOperationException(
                "No MSBuild instance found. Install the .NET SDK or Visual Studio Build Tools.");

        // Newest SDK wins — it's the one most likely to understand the target solution.
        var instance = instances.OrderByDescending(i => i.Version).First();
        MSBuildLocator.RegisterInstance(instance);
        _registered = true;
        Console.WriteLine($"Using MSBuild {instance.Version} from {instance.MSBuildPath}");
    }
}
