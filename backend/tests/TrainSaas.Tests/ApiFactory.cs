using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TrainSaas.Tests;

/// <summary>
/// Integration test host for the TrainSaas API. Points the application at
/// per-factory temporary locations (SQLite db file + upload root) and a fixed
/// test JWT signing key, and forces <c>SeedDemoData=false</c>.
/// Optional <paramref name="configOverrides"/> (applied after the defaults,
/// so they win) allow per-test-factory configuration, e.g.
/// <c>new ApiFactory(new Dictionary&lt;string, string&gt; { ["SeedDemoData"] = "true" })</c>.
/// The temporary directory is deleted on dispose.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private const string TestJwtKey = "trainsaas-integration-test-signing-key-0123456789";

    private readonly Dictionary<string, string?> _configOverrides;
    private readonly string _tempDir;

    /// <summary>
    /// Parameterless constructor. xUnit requires class fixture types to expose
    /// exactly one public constructor, so this is the one it instantiates.
    /// </summary>
    public ApiFactory()
        : this(null)
    {
    }

    /// <summary>
    /// Constructor with configuration overrides, e.g.
    /// <c>new ApiFactory(new Dictionary&lt;string, string&gt; { ["SeedDemoData"] = "true" })</c>.
    /// Internal so it does not compete with the public parameterless constructor
    /// (usable from the same test assembly).
    /// </summary>
    internal ApiFactory(Dictionary<string, string>? configOverrides)
    {
        _configOverrides = (configOverrides ?? new Dictionary<string, string>())
            .ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        _tempDir = Path.Combine(Path.GetTempPath(), $"trainsaas-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(_tempDir, "test.db")}",
                ["Storage:UploadRoot"] = _tempDir,
                ["Jwt:Key"] = TestJwtKey,
                ["SeedDemoData"] = "false",
            });
            // Test-provided overrides win over the defaults above.
            config.AddInMemoryCollection(_configOverrides);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: leftover temp files live under the OS temp root.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
