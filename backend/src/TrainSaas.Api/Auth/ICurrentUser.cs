using TrainSaas.Api.Domain;

namespace TrainSaas.Api.Auth;

/// <summary>
/// The identity of the caller for the current request. Before authentication
/// is wired up there are no claims and the user is <see cref="UserType.Anonymous"/>.
/// </summary>
public interface ICurrentUser
{
    UserType UserType { get; }

    /// <summary>Current institution for staff users; null for platform/parent/anonymous.</summary>
    int? InstitutionId { get; }

    int? StaffId { get; }

    StaffRole? Role { get; }

    string? Phone { get; }

    string? Name { get; }

    bool IsAuthenticated { get; }
}
