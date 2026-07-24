import type { ReactElement } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { useAuth } from "./lib/AuthContext";
import { Sidebar } from "./components/Sidebar";
import { StatusBanner } from "./components/StatusBanner";
import { LoginPage } from "./pages/LoginPage";
import { HomePage } from "./pages/HomePage";
import { ControllersPage } from "./pages/ControllersPage";
import { ControllerDetailPage } from "./pages/ControllerDetailPage";
import { SyncPage } from "./pages/SyncPage";
import { UsersPage } from "./pages/UsersPage";
import { UserProfilePage } from "./pages/UserProfilePage";
import { UserGroupsPage } from "./pages/UserGroupsPage";
import { VisitorsPage } from "./pages/VisitorsPage";
import { BedsPage } from "./pages/BedsPage";
import { TimeGroupsPage } from "./pages/TimeGroupsPage";
import { SystemUsersPage } from "./pages/SystemUsersPage";
import { AccessLogPage } from "./pages/AccessLogPage";
import { AlarmEventsPage } from "./pages/AlarmEventsPage";
import { SettingsPage } from "./pages/SettingsPage";
import { DevLogsPage } from "./pages/DevLogsPage";

function RequireAuth({ children }: { children: ReactElement }) {
  const { isAuthenticated } = useAuth();
  if (!isAuthenticated) return <Navigate to="/login" replace />;
  return children;
}

function AppShell({ children }: { children: ReactElement }) {
  return (
    <div className="app-shell">
      <Sidebar />
      <main className="main-content">
        <StatusBanner />
        {children}
      </main>
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
      <Route path="/sync" element={protect(<SyncPage />)} />
      <Route path="/users" element={protect(<UsersPage />)} />
      <Route path="/users/:id" element={protect(<UserProfilePage />)} />
      <Route path="/usergroups" element={protect(<UserGroupsPage />)} />
      <Route path="/visitors" element={protect(<VisitorsPage />)} />
      <Route path="/beds" element={protect(<BedsPage />)} />
      <Route path="/timegroups" element={protect(<TimeGroupsPage />)} />
      <Route path="/staff" element={protect(<SystemUsersPage />)} />
      <Route path="/accesslog" element={protect(<AccessLogPage />)} />
      <Route path="/alarmevents" element={protect(<AlarmEventsPage />)} />
      <Route path="/settings" element={protect(<SettingsPage />)} />
      <Route path="/devlogs" element={protect(<DevLogsPage />)} />
    </Routes>
  );
}

export default App;
