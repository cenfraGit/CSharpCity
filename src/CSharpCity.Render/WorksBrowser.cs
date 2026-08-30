using System.Numerics;
using CSharpCity.Model;
using ImGuiNET;

namespace CSharpCity.Render;

/// <summary>
/// The pull-request browser: what is open, and what each one is doing to the city.
/// </summary>
/// <remarks>
/// The one part of the interface built on Dear ImGui rather than on the project's own HUD. The HUD
/// is a competent immediate-mode renderer and its destination picker is already a filterable modal
/// list, but it is missing two things this panel needs and would have had to grow: mouse input,
/// which does not exist anywhere else in the codebase, and a font atlas beyond printable ASCII.
/// Pull request titles and author names are full of em-dashes, accented letters and emoji, and the
/// existing atlas renders every one of them as a question mark.
///
/// Scoped deliberately: ImGui draws this panel and nothing else. The legend, the worst list, the
/// inspection card and the picker stay on the HUD, where they are tuned and working.
/// </remarks>
internal sealed class WorksBrowser
{
    /// <summary>Which pull request is shown alone, or -1 for all of them.</summary>
    public int IsolatedNumber { get; private set; } = -1;

    string _filter = "";

    /// <summary>Draws the panel. Returns true when the caller must rebuild the overlay.</summary>
    public bool Draw(WorksFeed feed, ref bool open, Action requestRefresh)
    {
        if (!open) return false;

        bool changed = false;
        var snapshot = feed.Snapshot;

        ImGui.SetNextWindowSize(new Vector2(520f, 460f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(40f, 40f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin($"Works — {(snapshot.Repository.Length > 0 ? snapshot.Repository : "no remote")}",
                ref open, ImGuiWindowFlags.NoCollapse))
        {
            if (!snapshot.Available)
            {
                ImGui.TextWrapped(snapshot.Reason ?? "No GitHub data.");
                ImGui.TextWrapped("The city is showing the code alone; nothing here is missing "
                                  + "from it, only what the team is doing to it.");
            }
            else
            {
                changed |= DrawToolbar(feed, requestRefresh);
                ImGui.Separator();
                changed |= DrawList(snapshot);
                DrawBacklog(snapshot);
            }
        }

        ImGui.End();
        return changed;
    }

    bool DrawToolbar(WorksFeed feed, Action requestRefresh)
    {
        bool changed = false;

        ImGui.BeginDisabled(feed.Refreshing);
        if (ImGui.Button(feed.Refreshing ? "Refreshing…" : "Refresh")) requestRefresh();
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Show all") && IsolatedNumber != -1)
        {
            IsolatedNumber = -1;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##filter", "filter by title, author or file", ref _filter, 128);

        if (feed.LastError is not null)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), feed.LastError);

        return changed;
    }

    bool DrawList(GitHubSnapshot snapshot)
    {
        bool changed = false;

        var shown = snapshot.PullRequests
            .Where(Matches)
            .OrderBy(p => p.DaysSinceUpdate)
            .ThenBy(p => p.Number)
            .ToList();

        ImGui.Text($"{shown.Count} of {snapshot.PullRequests.Count} open");
        if (IsolatedNumber >= 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.32f, 1f), $"· showing #{IsolatedNumber} only");
        }

        ImGui.BeginChild("pulls", new Vector2(0f, -92f));

        foreach (var pull in shown)
        {
            bool isolated = IsolatedNumber == pull.Number;

            var (label, colour) = Status(pull);
            ImGui.TextColored(colour, label);
            ImGui.SameLine();

            if (ImGui.Selectable($"#{pull.Number}  {pull.Title}##{pull.Number}", isolated))
            {
                // Clicking the one already isolated puts everything back, so the panel needs no
                // separate "off" affordance for the common case.
                IsolatedNumber = isolated ? -1 : pull.Number;
                changed = true;
            }

            if (ImGui.IsItemHovered() && pull.Files.Count > 0)
            {
                ImGui.BeginTooltip();
                ImGui.Text($"{pull.Author} · +{pull.Additions} −{pull.Deletions} · "
                           + $"{pull.Files.Count} file(s) · {Age(pull.DaysSinceUpdate)}");
                ImGui.Separator();
                foreach (var file in pull.Files.Take(12))
                    ImGui.Text($"  {Mark(file.Change)} {file.Path}");
                if (pull.Files.Count > 12)
                    ImGui.TextDisabled($"  … and {pull.Files.Count - 12} more");
                ImGui.EndTooltip();
            }
        }

        ImGui.EndChild();
        return changed;
    }

    /// <summary>
    /// The backlog, as a count rather than a list.
    /// </summary>
    /// <remarks>
    /// Issues get a summary and no rows on purpose. They carry no file references, so there is
    /// nothing to isolate and nowhere to fly to — listing two hundred of them would be a worse
    /// version of the issue tracker rather than a view of the city.
    /// </remarks>
    static void DrawBacklog(GitHubSnapshot snapshot)
    {
        ImGui.Separator();
        if (snapshot.Issues.Count == 0)
        {
            ImGui.TextDisabled("No open issues.");
            return;
        }

        int bugs = snapshot.Issues.Count(i => i.Category == IssueCategory.Bug);
        int stale = snapshot.Issues.Count(i => i.DaysOpen >= 365);

        ImGui.Text($"Backlog: {snapshot.Issues.Count} open · {bugs} defect(s)");
        ImGui.TextDisabled(stale > 0
            ? $"{stale} open more than a year — the tents outside the civic buildings."
            : "Nothing older than a year.");
    }

    bool Matches(PullRequestInfo pull)
    {
        if (_filter.Length == 0) return true;

        return pull.Title.Contains(_filter, StringComparison.OrdinalIgnoreCase)
               || pull.Author.Contains(_filter, StringComparison.OrdinalIgnoreCase)
               || pull.Number.ToString().Contains(_filter, StringComparison.Ordinal)
               || pull.Files.Any(f => f.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The same vocabulary the city uses, so the panel and the streets agree.</summary>
    static (string Label, Vector4 Colour) Status(PullRequestInfo pull)
    {
        if (pull.Conflicting) return ("BLOCKED ", new Vector4(0.95f, 0.35f, 0.30f, 1f));
        if (pull.Review == ReviewState.ChangesRequested)
            return ("CHANGES ", new Vector4(0.95f, 0.55f, 0.30f, 1f));
        if (pull.IsDraft) return ("DRAFT   ", new Vector4(0.60f, 0.62f, 0.66f, 1f));
        if (pull.ChecksFailing) return ("CI RED  ", new Vector4(0.95f, 0.72f, 0.25f, 1f));
        if (pull.Review == ReviewState.Approved) return ("READY   ", new Vector4(0.35f, 0.85f, 0.45f, 1f));
        return ("REVIEW  ", new Vector4(0.70f, 0.78f, 0.90f, 1f));
    }

    static string Mark(FileChange change) => change switch
    {
        FileChange.Added => "+",
        FileChange.Removed => "−",
        _ => "~",
    };

    static string Age(int days) => days switch
    {
        0 => "today",
        1 => "yesterday",
        _ => $"{days} days ago",
    };
}
