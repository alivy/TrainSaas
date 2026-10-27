using Microsoft.AspNetCore.Authorization;

namespace TrainSaas.Api.Auth;

/// <summary>
/// The authorization policies shared by the whole API. The names and rules
/// are fixed by the Global Constraints and reused by later tasks; new
/// policies (e.g. AdminOrSales in Task 6) are appended in
/// <see cref="AddPolicies"/> so every name stays in one place.
/// </summary>
public static class AuthPolicies
{
    public const string PlatformOnly = nameof(PlatformOnly);
    public const string StaffOnly = nameof(StaffOnly);
    public const string ParentOnly = nameof(ParentOnly);
    public const string AdminOnly = nameof(AdminOnly);

    /// <summary>
    /// Registers the fixed policies. Policies key off the literal JWT claims:
    /// <c>user_type</c> (platform|staff|parent) and, for AdminOnly,
    /// <c>role</c> = Admin.
    /// </summary>
    public static void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PlatformOnly, policy =>
            policy.RequireAuthenticatedUser().RequireClaim("user_type", "platform"));

        options.AddPolicy(StaffOnly, policy =>
            policy.RequireAuthenticatedUser().RequireClaim("user_type", "staff"));

        options.AddPolicy(ParentOnly, policy =>
            policy.RequireAuthenticatedUser().RequireClaim("user_type", "parent"));

        // AdminOnly = 员工且 role=Admin.
        options.AddPolicy(AdminOnly, policy =>
            policy.RequireAuthenticatedUser()
                .RequireClaim("user_type", "staff")
                .RequireClaim("role", "Admin"));
    }
}
