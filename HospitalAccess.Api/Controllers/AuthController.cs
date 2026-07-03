using HospitalAccess.Api.Auth;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token, string Role);

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly JwtTokenService _tokens;
    private readonly PasswordHasher<Domain.Entities.StaffUser> _hasher = new();

    public AuthController(AccessDbContext db, JwtTokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var staffUser = await _db.StaffUsers
            .FirstOrDefaultAsync(s => s.Username == request.Username && s.Active);
        if (staffUser is null)
        {
            // Verifica um hash dummy mesmo sem usuário, para não vazar por timing se o username existe.
            _hasher.VerifyHashedPassword(new Domain.Entities.StaffUser(), DummyHash, request.Password);
            return Unauthorized();
        }

        var result = _hasher.VerifyHashedPassword(staffUser, staffUser.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
            return Unauthorized();

        var token = _tokens.IssueToken(staffUser);
        return Ok(new LoginResponse(token, staffUser.Role.ToString()));
    }

    // Hash fixo de uma senha aleatória, só para gastar o mesmo tempo de verificação quando o
    // usuário não existe (mitiga enumeração de usuários por timing).
    private static readonly string DummyHash =
        new PasswordHasher<Domain.Entities.StaffUser>().HashPassword(new Domain.Entities.StaffUser(), "dummy-timing-guard");
}
