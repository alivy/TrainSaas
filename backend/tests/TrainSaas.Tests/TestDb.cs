using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests;

/// <summary>
/// Test database infrastructure: every <see cref="Create"/> call returns an
/// <see cref="AppDbContext"/> bound to its own open in-memory SQLite connection
/// (the schema is created with <c>EnsureCreated</c>). In-memory SQLite databases
/// only live as long as their connection stays open, so the connection is
/// returned alongside the context and must be kept alive by the caller.
/// </summary>
public static class TestDb
{
    public static (AppDbContext db, SqliteConnection conn) Create(ICurrentUser? user = null)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new AppDbContext(options, user ?? new FakeCurrentUser());
        db.Database.EnsureCreated();
        return (db, conn);
    }
}

/// <summary>
/// Mutable <see cref="ICurrentUser"/> stand-in for unit tests: every property
/// can be set directly. <see cref="IsAuthenticated"/> mirrors the real
/// <see cref="CurrentUser"/> semantics (any non-anonymous user is authenticated).
/// </summary>
public class FakeCurrentUser : ICurrentUser
{
    public UserType UserType { get; set; } = UserType.Anonymous;
    public int? InstitutionId { get; set; }
    public int? StaffId { get; set; }
    public StaffRole? Role { get; set; }
    public string? Phone { get; set; }
    public string? Name { get; set; }
    public bool IsAuthenticated => UserType != UserType.Anonymous;
}
