using Xunit;

namespace RwEditor.Tests;

/// <summary>
/// The mod's development repository (the folder above tools\RwEditor). Tests marked [RepoFact] read the REAL shipped mod
/// source (read-only) and are skipped - visibly, not silently passed - when the editor is built outside that repository (e.g. a standalone editor repo).
/// </summary>
public static class Repo
{
    public static readonly string? Root = Find();

    static string? Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "VERSION")) && File.Exists(Path.Combine(d.FullName, "src", "editor-support.json"))
                && File.Exists(Path.Combine(d.FullName, "maps", "DESIGN_EDITOR.md")))
                return d.FullName;
        return null;
    }

    public static string Src => Path.Combine(Root!, "src");
    public static string Baseline => Path.Combine(Root!, "baseline_V271.7_pristine");
}

public sealed class RepoFactAttribute : FactAttribute
{
    public RepoFactAttribute()
    {
        if (Repo.Root == null) Skip = "the mod repository (VERSION + src/editor-support.json + maps/DESIGN_EDITOR.md) is not above the test folder";
    }
}
