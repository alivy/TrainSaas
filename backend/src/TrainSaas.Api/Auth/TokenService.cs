using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TrainSaas.Api.Domain;

namespace TrainSaas.Api.Auth;

/// <summary>
/// Issues the three kinds of JWT used by the platform (platform / staff /
/// parent). Claim names are fixed by the Global Constraints — user_type,
/// institution_id, staff_id, role, phone, name — and are written to the token
/// exactly as-is: <c>user_type</c> values are platform|staff|parent, <c>role</c>
/// values are the <see cref="StaffRole"/> enum names. Expiry is
/// <c>Jwt:ExpireHours</c> (server-local clock, 12h by default).
/// </summary>
public class TokenService(IConfiguration configuration)
{
    private readonly string _issuer =
        configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer 未配置");
    private readonly string _audience =
        configuration["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience 未配置");
    private readonly string _key =
        configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key 未配置");
    private readonly double _expireHours = configuration.GetValue("Jwt:ExpireHours", 12.0);

    public string IssuePlatformToken(PlatformUser user) =>
        Issue(
        [
            new Claim("user_type", "platform"),
            new Claim("name", user.Username),
        ]);

    public string IssueStaffToken(Staff staff, Institution institution) =>
        Issue(
        [
            new Claim("user_type", "staff"),
            new Claim("institution_id", institution.Id.ToString()),
            new Claim("staff_id", staff.Id.ToString()),
            new Claim("role", staff.Role.ToString()),
            new Claim("phone", staff.Phone),
            new Claim("name", staff.Name),
        ]);

    public string IssueParentToken(ParentAccount parent) =>
        Issue(
        [
            new Claim("user_type", "parent"),
            new Claim("phone", parent.Phone),
        ]);

    private string Issue(Claim[] claims)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = _audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.Now.AddHours(_expireHours),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key)),
                SecurityAlgorithms.HmacSha256),
        });
        return handler.WriteToken(token);
    }
}
