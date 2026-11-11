using Bydlo.Core.Updates;

namespace Bydlo.Tests;

/// <summary>
/// Issue #411: a catalog produced by <c>scripts/sign-update-catalog.ps1</c> must verify in Bydlo.Core.
/// <c>scripts/test-update-catalog-interop.ps1</c> signs synthetic packages with a throwaway key and runs
/// this test with <c>BYDLO_UPDATE_CATALOG_INTEROP_DIR</c> set; without it the test has nothing to check.
/// </summary>
public sealed class UpdateCatalogInteropTests
{
    [Fact]
    public void Catalog_signed_by_release_script_verifies_for_every_rid()
    {
        var dir = Environment.GetEnvironmentVariable("BYDLO_UPDATE_CATALOG_INTEROP_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return; // Plain test runs have no script output; CI runs the interop script after the build.
        }

        var json = File.ReadAllText(Path.Combine(dir, UpdateCatalogClient.AssetName));
        var keys = new Dictionary<string, string> { ["interop-test"] = File.ReadAllText(Path.Combine(dir, "public-key.txt")).Trim() };
        var verifier = new UpdateCatalogVerifier(keys, []);
        foreach (var rid in UpdateCatalogVerifier.SupportedRids)
        {
            var trusted = verifier.Verify(json, UpdateCatalogVerifier.StableChannel, rid, "0.0.1");
            var package = Path.Combine(dir, trusted.FileName);
            Assert.Equal("9.8.7", trusted.Version);
            Assert.Equal(new FileInfo(package).Length, trusted.Size);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(package))), trusted.Sha256);
        }
    }
}
