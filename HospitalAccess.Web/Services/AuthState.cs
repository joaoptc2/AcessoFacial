namespace HospitalAccess.Web.Services;

/// <summary>
/// Guarda o token JWT e o papel do usuário logado para o circuito Blazor atual
/// (scoped = um por circuito/aba, adequado ao modelo do Blazor Server).
/// </summary>
public sealed class AuthState
{
    public string? Token { get; private set; }
    public string? Role { get; private set; }
    public string? Username { get; private set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(Token);

    public event Action? Changed;

    public void SignIn(string token, string role, string username)
    {
        Token = token;
        Role = role;
        Username = username;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Token = null;
        Role = null;
        Username = null;
        Changed?.Invoke();
    }
}
