import type { ReactElement } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { useAuth } from "./lib/AuthContext";
import { Sidebar } from "./components/Sidebar";
import { LoginPage } from "./pages/LoginPage";
import { HomePage } from "./pages/HomePage";
import { ControllersPage } from "./pages/ControllersPage";
import { ControllerDetailPage } from "./pages/ControllerDetailPage";
import { UsersPage } from "./pages/UsersPage";
import { UserGroupsPage } from "./pages/UserGroupsPage";
import { VisitorsPage } from "./pages/VisitorsPage";
import { HolidaysPage } from "./pages/HolidaysPage";
import { TimeGroupsPage } from "./pages/TimeGroupsPage";
import { AccessLogPage } from "./pages/AccessLogPage";
import { AlarmEventsPage } from "./pages/AlarmEventsPage";
import { SettingsPage } from "./pages/SettingsPage";

function RequireAuth({ children }: { children: ReactElement }) {
  const { isAuthenticated } = useAuth();
  if (!isAuthenticated) return <Navigate to="/login" replace />;
  return children;
}

function AppShell({ children }: { children: ReactElement }) {
  return (
    <div className="app-shell">
      <Sidebar />
      <main className="main-content">{children}</main>
    </div>
  );
}

function protect(element: ReactElement) {
  return (
    <RequireAuth>
      <AppShell>{element}</AppShell>
    </RequireAuth>
  );
}

function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/" element={protect(<HomePage />)} />
      <Route path="/controllers" element={protect(<ControllersPage />)} />
      <Route path="/controllers/:id" element={protect(<ControllerDetailPage />)} />
      <Route path="/users" element={protect(<UsersPage />)} />
      <Route path="/usergroups" element={protect(<UserGroupsPage />)} />
      <Route path="/visitors" element={protect(<VisitorsPage />)} />
      <Route path="/holidays" element={protect(<HolidaysPage />)} />
      <Route path="/timegroups" element={protect(<TimeGroupsPage />)} />
      <Route path="/accesslog" element={protect(<AccessLogPage />)} />
      <Route path="/alarmevents" element={protect(<AlarmEventsPage />)} />
      <Route path="/settings" element={protect(<SettingsPage />)} />
    </Routes>
  );
}

export default App;
