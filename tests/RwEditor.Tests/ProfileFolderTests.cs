using RwEditor.Core.Apply;
using RwEditor.Core.Diagnostics;
using RwEditor.Core.GameLog;
using RwEditor.Core.Setup;
using Xunit;

namespace RwEditor.Tests;

[CollectionDefinition("profile-env", DisableParallelization = true)]
public sealed class ProfileEnvCollection { }

/// <summary>E7: DEPACHE_RW_EDITOR_HOME moves everything the editor remembers (used by the release check for a clean first run).</summary>
[Collection("profile-env")]
public sealed class ProfileFolderTests
{
    static void WithHome(string? value, Action check)
    {
        string? old = Environment.GetEnvironmentVariable(ProfileFolder.EnvironmentVariable);
        try { Environment.SetEnvironmentVariable(ProfileFolder.EnvironmentVariable, value); check(); }
        finally { Environment.SetEnvironmentVariable(ProfileFolder.EnvironmentVariable, old); }
    }

    [Fact]
    public void The_variable_moves_every_default_folder()
    {
        string home = Path.Combine(Path.GetTempPath(), "rwe-profile-" + Guid.NewGuid().ToString("N"));
        WithHome(home, () =>
        {
            Assert.Equal(home, ProfileFolder.Root);
            Assert.Equal(Path.Combine(home, "backups"), BackupStore.DefaultRoot);
            Assert.Equal(Path.Combine(home, "logs"), SessionLog.DefaultFolder);
            Assert.Equal(Path.Combine(home, "testloads"), TestLoadStore.DefaultFolder);
            Assert.Equal(Path.Combine(home, "app-settings.json"), AppSettings.DefaultPath);
            Assert.Equal(Path.Combine(home, "mymod"), CopyMaker.DefaultFolder);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\folder")]
    public void Without_a_usable_variable_the_profile_is_the_local_app_data_folder(string? value) =>
        WithHome(value, () => Assert.EndsWith(Path.Combine("", "DepacheRwEditor"), ProfileFolder.Root, StringComparison.Ordinal));
}
