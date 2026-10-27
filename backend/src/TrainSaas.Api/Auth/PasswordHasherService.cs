using Microsoft.AspNetCore.Identity;

namespace TrainSaas.Api.Auth;

/// <summary>
/// Password hashing abstraction used by login/seed flows, implemented with
/// ASP.NET Core Identity's PBKDF2 <see cref="PasswordHasher{TUser}"/>.
/// </summary>
public interface IPasswordHasherService
{
    string Hash(string password);

    bool Verify(string hash, string password);
}

public class PasswordHasherService : IPasswordHasherService
{
    private static readonly object PlaceholderUser = new();

    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(PlaceholderUser, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(PlaceholderUser, hash, password) != PasswordVerificationResult.Failed;
}
