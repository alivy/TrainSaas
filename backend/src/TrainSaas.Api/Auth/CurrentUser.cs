using System.Security.Claims;
using TrainSaas.Api.Domain;

namespace TrainSaas.Api.Auth;

/// <summary>
/// Production implementation of <see cref="ICurrentUser"/>: reads the JWT
/// claims of the current HTTP request. Claim names are fixed by the platform
/// contract (user_type, institution_id, staff_id, role, phone, name); missing
/// or invalid claims degrade to <see cref="UserType.Anonymous"/> instead of
/// throwing, so the API works before authentication exists (Task 3).
/// </summary>
public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public UserType UserType =>
        Enum.TryParse(Principal?.FindFirst("user_type")?.Value, ignoreCase: true, out UserType type)
            ? type
            : UserType.Anonymous;

    public int? InstitutionId =>
        int.TryParse(Principal?.FindFirst("institution_id")?.Value, out var institutionId) ? institutionId : null;

    public int? StaffId =>
        int.TryParse(Principal?.FindFirst("staff_id")?.Value, out var staffId) ? staffId : null;

    public StaffRole? Role =>
        Enum.TryParse(Principal?.FindFirst("role")?.Value, ignoreCase: true, out StaffRole role) ? role : null;

    public string? Phone => Principal?.FindFirst("phone")?.Value;

    public string? Name => Principal?.FindFirst("name")?.Value;

    public bool IsAuthenticated => UserType != UserType.Anonymous;
}
