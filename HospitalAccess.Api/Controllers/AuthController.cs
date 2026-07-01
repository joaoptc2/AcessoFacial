using HospitalAccess.Api.Auth;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var staffUser = await _db.StaffUsers
            .FirstOrDefaultAsync(s => s.Username == request.Username && s.Active);
        if (staffUser is null)
            return Unauthorized();

        var result = _hasher.VerifyHashedPassword(staffUser, staffUser.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
            return Unauthorized();

        var token = _tokens.IssueToken(staffUser);
        return Ok(new LoginResponse(token, staffUser.Role.ToString()));
    }
}
