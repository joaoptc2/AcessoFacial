import { NavLink } from "react-router-dom";
import { useAuth } from "../lib/AuthContext";
import { Icon, type IconName } from "./Icon";

function navClass({ isActive }: { isActive: boolean }) {
  return `sidebar-link${isActive ? " active" : ""}`;
}

/** Um item da navegação. `end` só no "Início", que casaria com tudo. */
interface NavItem {
  to: string;
  label: string;
  icon: IconName;
  end?: boolean;
}

/**
 * Agrupamento da navegação. É o mesmo de antes, com os rótulos de grupo agora
 * visíveis o tempo todo — eles são o que diz ao operador onde ele está no
 * sistema antes de ele clicar em qualquer coisa.
 */
const GRUPOS: { titulo: string | null; adminOnly?: boolean; itens: NavItem[] }[] = [
  {
    titulo: null,
    itens: [{ to: "/", label: "Início", icon: "dashboard", end: true }],
  },
  {
    titulo: "Dispositivos",
    itens: [
      { to: "/controllers", label: "Controladores / Portas", icon: "device" },
      { to: "/sync", label: "Sincronizações", icon: "sync" },
    ],
  },
  {
    titulo: "Acesso",
    itens: [
      { to: "/users", label: "Usuários", icon: "users" },
      { to: "/usergroups", label: "Grupos de Usuários", icon: "group" },
      { to: "/visitors", label: "Visitantes / Temporários", icon: "visitor" },
      { to: "/beds", label: "Gestão de Leitos", icon: "bed" },
    ],
  },
  {
    titulo: "Auditoria",
    itens: [
      { to: "/accesslog", label: "Log de Acessos", icon: "accessLog" },
      { to: "/alarmevents", label: "Log de Alarmes", icon: "alarm" },
    ],
  },
  {
    titulo: "Administração",
    adminOnly: true,
    itens: [
      { to: "/staff", label: "Usuários do Sistema", icon: "staff" },
      { to: "/settings", label: "Configurações", icon: "settings" },
      { to: "/devlogs", label: "Logs (Dev)", icon: "devLogs" },
    ],
  },
];

export function Sidebar() {
  const { role } = useAuth();
  const isAdmin = role === "Admin";

  return (
    <nav className="sidebar" aria-label="Navegação principal">
      {GRUPOS.filter((g) => !g.adminOnly || isAdmin).map((grupo) => (
        <div key={grupo.titulo ?? "principal"}>
          {grupo.titulo && <div className="sidebar-section">{grupo.titulo}</div>}
          {grupo.itens.map((item) => (
            <NavLink key={item.to} to={item.to} end={item.end} className={navClass}>
              <Icon name={item.icon} size={17} />
              {item.label}
            </NavLink>
          ))}
        </div>
      ))}
    </nav>
  );
}
