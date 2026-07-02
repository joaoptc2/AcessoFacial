import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { api, ApiError } from "../lib/api";
import { useAuth } from "../lib/AuthContext";

export function LoginPage() {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const { signIn } = useAuth();
  const navigate = useNavigate();

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = await api.login(username, password);
      signIn(result.token, result.role, username);
      navigate("/");
    } catch (err) {
      setError(err instanceof ApiError ? "Usuário ou senha inválidos." : "Falha ao conectar com o servidor.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login-wrapper">
      <div className="card login-card">
        <h3 style={{ marginTop: 0, marginBottom: "0.25rem" }}>Controle de Acesso Hospitalar</h3>
        <p className="text-muted" style={{ margin: 0 }}>
          Entre com suas credenciais para continuar.
        </p>
        <form onSubmit={handleSubmit}>
          <div className="form-field">
            <label htmlFor="username">Usuário</label>
            <input id="username" value={username} onChange={(e) => setUsername(e.target.value)} autoFocus />
          </div>
          <div className="form-field">
            <label htmlFor="password">Senha</label>
            <input id="password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </div>
          {error && <div className="alert alert-danger">{error}</div>}
          <button type="submit" className="btn btn-primary" disabled={busy}>
            {busy && <span className="spinner" />}
            {busy ? "Entrando..." : "Entrar"}
          </button>
        </form>
      </div>
    </div>
  );
}
