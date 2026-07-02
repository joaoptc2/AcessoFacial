# HospitalAccess.Web.React

Interface em React para a `HospitalAccess.Api` — cobre **todas as telas** que o
`HospitalAccess.Web` (Blazor Server) tinha: Controladores (+ detalhe com abas de
Rede/Relógio/Alarmes/Ajustes Locais/Auditoria/Fotos de Evento), Usuários (com
histórico/revogar/reativar/RBAC), Grupos de Usuários (com portas padrão), Visitantes
(com QR), Feriados, Grade Horária, Log de Acessos e Log de Alarmes. O Blazor continua no
repo por enquanto (mudança grande, mantida reversível) — ver "Próximos passos" abaixo.

## Por que React "no mesmo servidor"

Em produção, `npm run build` gera os arquivos estáticos direto em
`../HospitalAccess.Api/wwwroot` (ver `outDir` em `vite.config.ts`), e a
`HospitalAccess.Api` os serve via `app.UseStaticFiles()` + `app.MapFallbackToFile(...)`
(ver `Program.cs`). Ou seja: **um único processo/porta** (`dotnet HospitalAccess.Api.dll`)
serve tanto `/api/*` quanto a interface — não precisa rodar o `HospitalAccess.Web` (Blazor)
em paralelo para usar esta versão.

## Rodando em desenvolvimento

```bash
npm install
npm run dev
```

O servidor de dev do Vite (porta própria, com hot-reload) faz proxy de `/api/*` para
`http://localhost:5080` (ver `server.proxy` em `vite.config.ts`) — suba a
`HospitalAccess.Api` normalmente (`dotnet run` dentro de `HospitalAccess.Api/`) antes.

## Gerando o build de produção

```bash
npm run build
```

Isso já deixa a `HospitalAccess.Api` pronta para servir a nova interface no próximo
`dotnet run`/deploy — não precisa copiar nada manualmente. `wwwroot/` não é versionado
(ver `.gitignore` na raiz do repo); rode o build como parte do processo de deploy.

## Autenticação

Diferente do Blazor Server (sessão só na memória do circuito, perdida em qualquer F5),
aqui o JWT fica em `localStorage` (`src/lib/AuthContext.tsx`) — um refresh de página
mantém a sessão.

## Estrutura

- `src/lib/api.ts` — cliente HTTP tipado para a API (mesma responsabilidade do
  `ApiClient.cs` do lado Blazor) — cobre todos os endpoints usados pelas telas abaixo.
- `src/lib/AuthContext.tsx` — estado de autenticação (token/role/username).
- `src/components/` — `Sidebar` (navegação) e `Modal` (usado no controle de dispositivo).
- `src/pages/`:
  - `LoginPage`, `HomePage`
  - `ControllersPage` + `ControllerDetailPage` (abas: Rede, Relógio, Alarmes, Ajustes
    Locais, Auditoria, Fotos de Evento)
  - `UsersPage` (histórico, revogar/reativar, RBAC — Recepção só lê)
  - `UserGroupsPage` (portas padrão do grupo)
  - `VisitorsPage` (cadastro + QR + revogar)
  - `HolidaysPage`, `TimeGroupsPage` (grade de dias/horários por grupo)
  - `AccessLogPage`, `AlarmEventsPage` (paginação, filtros, exportação CSV)

## Próximos passos

- Validar visualmente todas as telas (rodar `npm run dev` + navegar) antes de decidir se
  o `HospitalAccess.Web` (Blazor) pode ser removido do repo.
- Nenhum teste automatizado de front-end ainda (a validação até agora foi manual +
  Playwright ad-hoc durante o desenvolvimento, não commitado como suíte).
