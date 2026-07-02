import { NavLink } from "react-router-dom";
import { useAuth } from "../lib/AuthContext";

export function Sidebar() {
  const { username, role, signOut } = useAuth();

  return (
    <nav className="sidebar">
      <div className="sidebar-brand">Controle de Acesso Hospitalar</div>

      <NavLink to="/" end className="sidebar-link">
        Início
      </NavLink>

      <div className="sidebar-section">Dispositivos</div>
      <NavLink to="/controllers" className={({ isActive }) => `sidebar-link${isActive ? " active" : ""}`}>
        Controladores / Portas
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
