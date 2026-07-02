import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";

interface AuthState {
  token: string | null;
  role: string | null;
  username: string | null;
  isAuthenticated: boolean;
  signIn: (token: string, role: string, username: string) => void;
  signOut: () => void;
}

const AuthContext = createContext<AuthState | null>(null);

// Ao contrário do Blazor Server (onde a sessão vive só na memória do circuito e some com
// um F5), aqui persistimos em localStorage — um F5 mantém o usuário logado.
export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(() => localStorage.getItem("token"));
  const [role, setRole] = useState<string | null>(() => localStorage.getItem("role"));
  const [username, setUsername] = useState<string | null>(() => localStorage.getItem("username"));

  const signIn = useCallback((newToken: string, newRole: string, newUsername: string) => {
    localStorage.setItem("token", newToken);
    localStorage.setItem("role", newRole);
    localStorage.setItem("username", newUsername);
    setToken(newToken);
    setRole(newRole);
    setUsername(newUsername);
  }, []);

  const signOut = useCallback(() => {
    localStorage.removeItem("token");
    localStorage.removeItem("role");
    localStorage.removeItem("username");
    setToken(null);
    setRole(null);
    setUsername(null);
  }, []);

  const value = useMemo(
    () => ({ token, role, username, isAuthenticated: token !== null, signIn, signOut }),
    [token, role, username, signIn, signOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth deve ser usado dentro de um AuthProvider.");
  return ctx;
}
