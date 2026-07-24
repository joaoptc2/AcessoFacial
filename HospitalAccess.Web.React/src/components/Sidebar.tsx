import { NavLink } from "react-router-dom";
import { useAuth } from "../lib/AuthContext";

function navClass({ isActive }: { isActive: boolean }) {
  return `sidebar-link${isActive ? " active" : ""}`;
}

export function Sidebar() {
  const { username, role, signOut } = useAuth();
  const isAdmin = role === "Admin";

  return (
    <nav className="sidebar">
      <div className="sidebar-brand">Controle de Acesso Hospitalar</div>

      <NavLink to="/" end className={navClass}>
        Início
      </NavLink>

      <div className="sidebar-section">Dispositivos</div>
      <NavLink to="/controllers" className={navClass}>
        Controladores / Portas
      </NavLink>
      <NavLink to="/sync" className={navClass}>
        Sincronizações
      </NavLink>

      <div className="sidebar-section">Acesso</div>
      <NavLink to="/users" className={navClass}>
        Usuários
      </NavLink>
      <NavLink to="/usergroups" className={navClass}>
        Grupos de Usuários
      </NavLink>
      <NavLink to="/visitors" className={navClass}>
        Visitantes / Temporários
      </NavLink>
      <NavLink to="/beds" className={navClass}>
        Gestão de Leitos
      </NavLink>
      <NavLink to="/timegroups" className={navClass}>
        Grade Horária
      </NavLink>

      <div className="sidebar-section">Auditoria</div>
      <NavLink to="/accesslog" className={navClass}>
        Log de Acessos
      </NavLink>
      <NavLink to="/alarmevents" className={navClass}>
        Log de Alarmes
      </NavLink>

      {isAdmin && (
        <>
          <div className="sidebar-section">Administração</div>
          <NavLink to="/staff" className={navClass}>
            Usuários do Sistema
          </NavLink>
          <NavLink to="/settings" className={navClass}>
            Configurações
          </NavLink>
          <NavLink to="/devlogs" className={navClass}>
            Logs (Dev)
          </NavLink>
        </>
      )}

      <div className="sidebar-footer">
        <span>
          {username} <span className="badge-role">{role}</span>
        </span>
        <button className="btn btn-sm btn-outline" onClick={signOut} style={{ color: "#fff", borderColor: "rgba(255,255,255,0.3)" }}>
          Sair
        </button>
      </div>
    </nav>
  );
}
