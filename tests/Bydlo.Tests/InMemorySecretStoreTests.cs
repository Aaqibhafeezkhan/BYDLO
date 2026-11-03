using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

public sealed class InMemorySecretStoreTests
{
    [Fact]
    public async Task Loaded_values_are_independent_until_explicitly_saved()
    {
        var store = new InMemorySecretStore();
        var original = new ProfileSecrets { Values = { ["password"] = "original" } };
        await store.SaveAsync("profile", original);
        original.Values["password"] = "changed-after-save";
        var edited = await store.LoadAsync("profile");
        Assert.Equal("original", edited["PASSWORD"]);
        edited.Values["password"] = "edited";
        edited.Values["username"] = "new-user";
        var unchanged = await store.LoadAsync("profile");
        Assert.NotSame(edited, unchanged);
        Assert.NotSame(edited.Values, unchanged.Values);
        Assert.Equal("original", unchanged["password"]);
        Assert.Null(unchanged["username"]);
        await store.SaveAsync("profile", edited);
        Assert.Equal("edited", (await store.LoadAsync("profile"))["password"]);
        Assert.Equal("new-user", (await store.LoadAsync("profile"))["username"]);
    }

    [Fact]
    public async Task Missing_and_deleted_profiles_return_independent_empty_values()
    {
        var store = new InMemorySecretStore();
        var missing = await store.LoadAsync("missing");
        missing.Values["password"] = "unsaved";
        Assert.Empty((await store.LoadAsync("missing")).Values);
        await store.SaveAsync("missing", missing);
        await store.DeleteAsync("missing");
        Assert.Empty((await store.LoadAsync("missing")).Values);
    }
}
