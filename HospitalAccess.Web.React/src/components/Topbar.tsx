import { Link } from "react-router-dom";
import { useAuth } from "../lib/AuthContext";
import { Icon } from "./Icon";

/**
 * Barra superior fixa: identidade à esquerda, quem está operando à direita.
 *
 * O "Sair" morava no rodapé da sidebar, onde ficava abaixo de 13 links e
 * sumia em telas baixas. Aqui ele está sempre visível e sempre no mesmo
 * lugar — e a sidebar fica livre para ser só navegação.
 */
export function Topbar() {
  const { username, role, signOut } = useAuth();
  const inicial = (username ?? "?").charAt(0).toUpperCase();

  return (
    <header className="topbar">
      <Link to="/" className="topbar-brand">
        <span className="topbar-mark">
          <Icon name="shield" size={17} />
        </span>
        Controle de Acesso Hospitalar
      </Link>

      <div className="topbar-spacer" />

      <div className="topbar-user">
        <span className="topbar-avatar" aria-hidden="true">
          {inicial}
        </span>
        <span className="topbar-name">
          {username}
          {role && <span className="topbar-role"> · {role}</span>}
        </span>
      </div>

      <button className="topbar-action" onClick={signOut}>
        <Icon name="logout" size={15} />
        Sair
      </button>
    </header>
  );
}
