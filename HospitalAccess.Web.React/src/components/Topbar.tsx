import { Link } from "react-router-dom";
import { useAuth } from "../lib/AuthContext";
import { Icon } from "./Icon";

interface TopbarProps {
  /** Abre/fecha a gaveta de navegação (só aparece no celular). */
  onToggleMenu: () => void;
  menuAberto: boolean;
}

/**
 * Barra superior fixa: identidade à esquerda, quem está operando à direita.
 *
 * O "Sair" morava no rodapé da sidebar, onde ficava abaixo de 13 links e
 * sumia em telas baixas. Aqui ele está sempre visível e sempre no mesmo
 * lugar — e a sidebar fica livre para ser só navegação.
 *
 * No celular a barra encolhe por partes, na ordem do que menos custa perder: o
 * nome do usuário some (o avatar permanece), depois o texto do "Sair" (o ícone
 * permanece) e por último o nome do sistema. O botão de menu entra à esquerda.
 */
export function Topbar({ onToggleMenu, menuAberto }: TopbarProps) {
  const { username, role, signOut } = useAuth();
  const inicial = (username ?? "?").charAt(0).toUpperCase();

  return (
    <header className="topbar">
      <button
        className="topbar-menu"
        onClick={onToggleMenu}
        aria-label={menuAberto ? "Fechar o menu" : "Abrir o menu"}
        aria-expanded={menuAberto}
      >
        <Icon name={menuAberto ? "close" : "menu"} size={20} />
      </button>

      <Link to="/" className="topbar-brand">
        <span className="topbar-mark">
          <Icon name="shield" size={17} />
        </span>
        <span className="topbar-title">Controle de Acesso Hospitalar</span>
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

      <button className="topbar-action" onClick={signOut} aria-label="Sair">
        <Icon name="logout" size={15} />
        <span className="topbar-action-label">Sair</span>
      </button>
    </header>
  );
}
