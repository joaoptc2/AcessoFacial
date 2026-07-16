# HospitalAccess.Web.React

**Único front-end** do sistema (o Blazor `HospitalAccess.Web` foi aposentado). Cobre todas
as telas: Controladores (+ detalhe com abas de Rede/Relógio/Alarmes/Ajustes Locais/
Auditoria/Fotos de Evento), Usuários (histórico/revogar/reativar/RBAC), Grupos de Usuários
(portas padrão), Visitantes (portas da visita + QR), Feriados, Grade Horária, Log de
Acessos e Log de Alarmes, além do **painel de status**, **emergência/evacuação** e
**configurações** (retenção de dados).

## Por que React "no mesmo servidor"

Em produção, `npm run build` gera os arquivos estáticos direto em
`../HospitalAccess.Api/wwwroot` (ver `outDir` em `vite.config.ts`), e a
`HospitalAccess.Api` os serve via `app.UseStaticFiles()` + `app.MapFallbackToFile(...)`
(ver `Program.cs`). Ou seja: **um único processo/porta** (`dotnet HospitalAccess.Api.dll`)
serve tanto `/api/*` quanto a interface.

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

- `src/lib/api.ts` — cliente HTTP tipado para a API — cobre todos os endpoints usados
  pelas telas abaixo.
- `src/lib/AuthContext.tsx` — estado de autenticação (token/role/username).
- `src/lib/controllerStatusStore.ts` — polling compartilhado do `/api/controllers/status`
  (um único intervalo de 30s por aba, consumido por `StatusBanner` e `HomePage`).
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
  - `SettingsPage` (retenção/LGPD, formato do QR — só Admin)
  - `DevLogsPage` (só Admin): logs importantes do servidor capturados em memória com o
    **modo de desenvolvimento** ativo — toggle persistido, busca incremental a cada 3s,
    filtro por nível/texto e botão de limpar. Ver `/api/devlogs` e `DevLogBuffer` na API.

## Próximos passos

- Adicionar uma suíte de teste de front-end automatizada (hoje o CI faz build + typecheck;
  a validação de fluxo é manual).
