import { NavLink } from "react-router-dom";
import { useAuth } from "../lib/AuthContext";

function navClass({ isActive }: { isActive: boolean }) {
  return `sidebar-link${isActive ? " active" : ""}`;
}

export function Sidebar() {
  const { username, role, signOut } = useAuth();

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
      <NavLink to="/timegroups" className={navClass}>
        Grade Horária
      </NavLink>
      <NavLink to="/holidays" className={navClass}>
        Feriados
      </NavLink>

      <div className="sidebar-section">Auditoria</div>
      <NavLink to="/accesslog" className={navClass}>
        Log de Acessos
      </NavLink>
      <NavLink to="/alarmevents" className={navClass}>
        Log de Alarmes
      </NavLink>

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
