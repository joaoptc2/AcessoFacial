import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Em dev, o front roda em porta própria (HMR) mas fala com a API real via proxy —
    // evita CORS e mantém o código igual ao de produção (chamadas para "/api/...").
    proxy: {
      "/api": {
        target: "http://localhost:5080",
        changeOrigin: true,
      },
    },
  },
  build: {
    // Em produção, a HospitalAccess.Api serve estes arquivos estáticos direto (mesmo
    // processo/porta que a API) — ver app.UseStaticFiles() em Program.cs.
    outDir: "../HospitalAccess.Api/wwwroot",
    emptyOutDir: true,
  },
});
