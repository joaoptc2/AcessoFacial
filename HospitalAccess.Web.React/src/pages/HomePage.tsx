import { useAuth } from "../lib/AuthContext";

export function HomePage() {
  const { username, role } = useAuth();

  return (
    <div>
      <h2>Controle de Acesso Hospitalar</h2>
      <p>
        Bem-vindo(a), {username} ({role}).
      </p>
      <div className="card">
        <h3 style={{ marginTop: 0 }}>Prova de conceito React</h3>
        <p className="text-muted" style={{ marginBottom: 0 }}>
          Esta interface é servida pelo mesmo processo/porta da API (<code>HospitalAccess.Api</code>), como o Blazor
          Server fazia antes — só que agora renderizada no navegador com React em vez de round-trips ao servidor a
          cada clique. A tela de Controladores foi migrada como prova de conceito; o restante continua em{" "}
          <code>HospitalAccess.Web</code> (Blazor) até validarmos essa direção.
        </p>
      </div>
    </div>
  );
}
