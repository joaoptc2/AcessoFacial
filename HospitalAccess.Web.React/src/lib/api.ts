// Cliente HTTP tipado para a HospitalAccess.Api.
//
// Em dev, o Vite faz proxy de /api para http://localhost:5080 (ver vite.config.ts); em produção,
// a API serve os arquivos estáticos deste app no mesmo host/porta, então /api já é same-origin.
//
// Este arquivo é apenas o BARRIL: era um módulo de ~990 linhas com todos os tipos e todas as
// chamadas juntos. O conteúdo foi separado por domínio em ./api/, e a superfície pública ficou
// idêntica — a aplicação continua importando `from "../lib/api"`.
// De http só sai o que já era público antes: request/requestBlob/buildQuery seguem internos
// aos módulos de ./api/ (são detalhe de implementação do cliente, não superfície da aplicação).
export { ApiError, authHeaders, downloadBlob } from "./api/http";
export * from "./api/types.controllers";
export * from "./api/types.people";
export * from "./api/types.operations";
export * from "./api/client";
