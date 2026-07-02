import type { ReactElement } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { useAuth } from "./lib/AuthContext";
import { Sidebar } from "./components/Sidebar";
import { LoginPage } from "./pages/LoginPage";
import { HomePage } from "./pages/HomePage";
import { ControllersPage } from "./pages/ControllersPage";

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

function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        path="/"
        element={
          <RequireAuth>
            <AppShell>
              <HomePage />
            </AppShell>
          </RequireAuth>
        }
      />
      <Route
        path="/controllers"
        element={
          <RequireAuth>
            <AppShell>
              <ControllersPage />
            </AppShell>
          </RequireAuth>
        }
      />
    </Routes>
  );
}

export default App;
