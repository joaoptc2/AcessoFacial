# Revisão técnica — Sistema de Controle de Acesso Hospitalar 8190H

_Data: 2026-07-03 · Base revisada: protocolo "8190H Fingerprint&Face Communication Protocol", "8190H Software Development Kit", biblioteca `DoNetDrive.Protocol.Fingerprint` (fonte do demo oficial + XMLs de API) e todo o código do repositório._

> **Como ler este documento:** as seções 1–7 descrevem os problemas **como estavam na data da
> revisão (2026-07-03)** — em texto no presente, com os números de linha da época. O estado
> ATUAL de cada item está na tabela "Status de correção" logo abaixo (e no adendo de 2026-07-16
> no fim): item marcado ✅ já foi corrigido, mesmo que a descrição no corpo fale no presente.
> Referências a arquivos Blazor (`*.razor`, `ApiClient.cs`) apontam para o front-end antigo,
> removido do repositório (item F3).

## Status de correção (aplicado nesta branch)

As correções abaixo foram implementadas e verificadas por compilação (`dotnet build` 0 erros) e
testes (`dotnet test` verde) — os itens que dependem de hardware 8190H físico foram implementados
conforme o demo/protocolo do fabricante, mas continuam marcados como "não validados contra
hardware" (mesma ressalva já existente no README).

| Item | Status |
|------|--------|
| C1 Monitoramento (BeginWatch) | ✅ implementado + hosted service de (re)ativação; ⚠️ validar em hardware |
| C2 Alarmes via SystemTransaction + cast de evento corrigido | ✅ implementado; ⚠️ validar em hardware |
| C3 Transporte configurável (TcpClient/TcpServerClient/Udp) | ✅ knob por controlador; ⚠️ modo real a confirmar em hardware |
| C4 Verificação de status de comando | ✅ implementado (todo comando lança em falha/timeout) |
| C5 Re-sync na edição de usuário | ✅ implementado |
| C6 Job de retry de sincronização | ✅ hosted service periódico |
| C7 QR de visitante | ✅ redesenhado: visitante é cadastrado como pessoa (sem face) com validade nativa (Person.Expiry) nas portas da visita; sistema cria/remove; QR bloqueado se revogado/expirado (S3). ⚠️ validar em hardware |
| L1 Coleta de registros offline | ✅ hosted service + gateway (ReadTransactionDatabase, dedup); ⚠️ validar em hardware |
| L2 Health-check / dashboard | ✅ heartbeat + LastSeenUtc + painel (React HomePage) + GET /controllers/status |
| L3 Emergência em massa | ✅ EmergencyController (abrir todas + alarme de incêndio / encerrar), Admin, auditado + tela |
| S6 Retenção (LGPD) | ✅ tela de Configurações + SystemSettings + job de expurgo diário configurável |
| B1 RBAC (Recepção opera portas) | ✅ implementado |
| B2 Código de evento 13 concedido | ✅ corrigido + teste |
| B3 Foto de evento por PhotoFile | ✅ implementado |
| B4 Bitmap de feriado por pessoa | ✅ implementado |
| B5 UserCode por sequence | ✅ implementado (migração) |
| B6 DateTime UTC | ✅ conversor global + normalização de borda |
| B7 Delete revoga todas as portas | ✅ implementado |
| B9/B10/B11 correções de gateway | ✅ implementado |
| B12 Expiração de visitante | ✅ implementado |
| B15/B64 Edição de controlador | ✅ React e Blazor |
| F1/F2 RBAC/401 no React | ✅ implementado |
| S1 Senha de comunicação criptografada + não exposta | ✅ DataProtection + Get sem senha |
| S2 Rate limiting no login | ✅ implementado |
| S3 QR de visitante revogado/expirado | ✅ implementado |
| S4 Handler de exceção sem vazar detalhes | ✅ ID de correlação |
| S5 Concorrência otimista (xmin) | ✅ Controller e User |
| S7 CSV injection | ✅ implementado |
| S8 Validação da chave JWT no startup | ✅ implementado |
| L4 SendConnectTestResponse (keep-alive 0xA0) | ✅ implementado |
| L6 Auditoria de comandos de porta | ✅ tabela ControllerAuditLog |
| L9 Validação de JPEG na conversão de face | ✅ implementado + teste |
| R2 Testes | ✅ suíte cobrindo classifier, conversor de imagem, fila de sync, política de retry, QR, cliente HTTP e mais (hoje 51 métodos / 76 casos em 10 arquivos) |
| R3 CI | ✅ GitHub Actions (build/test .NET + build React) |
| R4/R5 Docs | ✅ README/instalação/appsettings corrigidos |
| F3 Retirar Blazor | ✅ `HospitalAccess.Web` removido do repositório; React é o único front-end (servido pela API) |
| R1 Arquivos do fabricante | ✅ movidos para `vendor/` (fora do build), com README explicativo |

> Nota sobre o achado [1] do workflow ("cast de evento errado"): reverificado e **confirmado** —
> o push chega como `Door8800Transaction` e o registro concreto está em `.EventData`. O gateway
> foi corrigido para usar `transaction.EventData` (antes o `is CardTransaction` sobre o envelope
> retornava cedo e nenhum evento seria emitido).

---

## Metodologia e ressalva de verificação

1. Extraí e li integralmente os 3 pacotes de referência do fabricante (protocolo de 124 páginas, SDK multi-linguagem, e o projeto de exemplo oficial `DoNetDrive.Protocol.Fingerprint.Test` com os XMLs de IntelliSense).
2. Revisei todas as camadas do sistema (Gateway, Api, Application, Infrastructure, Domain, Web Blazor, Web React, Tests) contra o uso canônico documentado.
3. Rodei uma revisão multi-agente (10 revisores + build). A fase de **verificação adversarial automática não pôde ser concluída** (limite de gastos da conta atingido no meio da execução). Por isso, **reverifiquei manualmente** os achados críticos contra o demo do fabricante e o texto do protocolo — o resultado dessa reverificação está marcado abaixo (✅ confirmado / ⚠️ requer hardware / ❌ refutado).

**Build:** a solução compila com **0 erros e 0 warnings** (.NET 8, `Nullable enable`), a suíte de testes passa (na data da revisão eram 3 testes; a suíte atual tem 76 casos em 10 arquivos) e o frontend React builda. Todos os problemas abaixo são de **correção em runtime, protocolo ou segurança** — nenhum impede a compilação.

---

## 1. CRÍTICOS

### C1. Monitoramento em tempo real nunca é ativado (`BeginWatch` ausente) — ✅ confirmado
`HospitalAccess.Gateway/DoNetDriveGateway.cs:54`
O gateway apenas assina `_allocator.TransactionMessage`, mas **nunca envia o comando `BeginWatch`** (Online Transaction, `0x01 0x0B`). O protocolo (§10) é explícito: o push vem **desligado de fábrica e não é persistido após reboot**; o demo oficial chama `SystemParameter.Watch.BeginWatch` antes de receber qualquer evento (`FrmMain.cs:1874`). Consequência: `AccessEventRecorder` e `AlarmEventRecorder` **nunca receberão eventos** de um 8190H real — o log de acessos e o de alarmes ficam permanentemente vazios.
**Correção:** emitir `BeginWatch` por controlador na inicialização e a cada reconexão/reboot (reagir a `ClientOnline`/keep-alive), mantendo o canal aberto com `OpenForciblyConnect`.

### C2. Alarmes nunca são capturados; ramo de alarme é código morto — ✅ confirmado
`HospitalAccess.Gateway/DoNetDriveGateway.cs:563` e `:580`
`AlarmTransaction` pertence ao namespace `DoNetDrive.Protocol.Door.Door8800.Data` — o protocolo das **controladoras de cartão de 4 portas**, não do facial. No 8190H (A33_Face), os alarmes chegam como **`SystemTransaction`** (CmdIndex 3, códigos de status 14–20: verificação ilegal, sensor de porta, coação, timeout, blacklist, fogo, tamper), como comprova o próprio `frmSystem.cs` do demo. O gateway faz `if (transaction.CmdIndex != 1) return;` e **descarta** todo `SystemTransaction` e `DoorSensorTransaction`. Resultado: a captura de alarmes está duplamente quebrada — o ramo `AlarmTransaction` nunca dispara e os alarmes reais são jogados fora.
**Correção:** tratar `CmdIndex == 3` (SystemTransaction) mapeando os códigos 14–20 para `AlarmKind`, e `CmdIndex == 2` (DoorSensorTransaction) para o sensor de porta.
✅ *Corrigido:* `OnTransactionMessage` hoje trata `CmdIndex == 3` via `TransactionCodeClassifier.TryMapSystemAlarm` (códigos 14–20 disparam, 21–27 limpam).

### C3. Transporte TCPClient direto pode não funcionar contra o hardware — ⚠️ requer validação com hardware
`HospitalAccess.Gateway/Connections/ControllerConnectionFactory.cs:26`
O gateway abre conexão TCP **ativa** para o 8190H (`ConnectType.TCPClient`). O protocolo (§1.2) descreve os modos do dispositivo como COM/UDP/**TCP Client** — onde "TCP Client" significa que o **dispositivo** disca para um servidor. O demo oficial só demonstra UDP e TCPServerClient (dispositivo conectando ao nosso TCP Server), nunca TCP client direto ao facial. O próprio README já registra este caveat. É plausível que os comandos falhem por timeout em todos os 30 controladores e/ou que o push exija modo "phone-home". **Este é o item nº 1 a validar ao ligar o primeiro controlador físico.**
**Correção provável:** operar o servidor como `TCPServerClient` (dispositivo conecta a nós) — muda só o `ConnectType`, não o resto do gateway.

### C4. Escritas no dispositivo "sucedem" silenciosamente em caso de timeout/falha — ✅ confirmado
`HospitalAccess.Gateway/DoNetDriveGateway.cs` (todos os métodos de escrita)
Após `AddCommandAsync`, o gateway nunca consulta `cmd.GetStatus()` (`INCommandStatus.IsFaulted`/`IsCanceled`, existente na API). O demo assina `CommandErrorEvent`/`CommandTimeout`/`AuthenticationErrorEvent`. Para **leituras**, o `getResult() == null → throw` salva; para **escritas** (`DeletePerson`, `OpenDoor/Close/Lock/Unlock`, `WriteTime`, `SyncHolidays`, `SyncTimeGroups`, alarmes, kiosk, `WriteTCPSetting`) o método retorna sucesso mesmo em timeout. Impactos graves:
- `RevokeFromControllerAsync` marca `SyncState.Revoked` **sem o device ter removido a pessoa** → revogação falsa: pessoa demitida continua abrindo a porta, e o sistema diz que está revogada.
- Comandos de porta devolvem HTTP 204 sem a porta ter obedecido.
**Correção:** após cada comando, checar `GetStatus().IsFaulted/IsCanceled` (ou tratar `CommandError/Timeout`) e propagar exceção; só marcar `Revoked` após confirmação real.

### C5. Edição de usuário já sincronizado nunca chega aos controladores — ✅ confirmado
`HospitalAccess.Api/Services/UserSyncService.cs:71`
`SyncUserAsync` faz `if (status is { State: SyncState.Synced }) continue;` e **nada** volta o estado para `Pending` ao editar (confirmei por busca: não há atribuição de `Pending` fora do inicializador). Trocar a foto de face, o nome ou o `TimeGroup` de um usuário já sincronizado **não propaga** para os dispositivos — a mudança fica só no banco. Numa troca de foto por questão de segurança, o device continua reconhecendo a foto antiga.
**Correção:** ao editar, marcar os `DeviceSyncStatus` afetados como `Pending` (ou versionar o usuário e comparar hash antes de pular).

### C6. Fila de retry documentada nunca executa — ✅ confirmado
`HospitalAccess.Api/Services/UserSyncService.cs:100`
`RetryPendingAsync` está implementado e o README o descreve como "job periódico", mas **nenhum** serviço o invoca (só há a definição e a interface). Se um controlador estava offline no cadastro/revogação, o usuário fica `Pending`/`Failed` **para sempre**. Combinado com C4, uma revogação que falhou nunca é reprocessada.
**Correção:** criar um `BackgroundService` periódico chamando `RetryPendingAsync` (mesmo padrão do `VisitorExpirationBackgroundService`).
✅ *Corrigido:* o `SyncRetryBackgroundService` reenfileira os elegíveis na fila serial (o `RetryPendingAsync` original virou código morto e foi removido); desde 2026-07-16 a varredura respeita backoff exponencial e quarentena de falha permanente (`SyncRetryPolicy`).

### C7. QR de visitante forjável e sem imposição real na porta — ✅ confirmado (design)
`HospitalAccess.Application/Qr/QrAccessTokenService.cs:33`, `VisitorsController.cs`, `UserSyncService.cs:38`
O QR é `Base64("user_id={code}_time={μs}")` — **sem assinatura, criptografia ou checksum**; qualquer um decodifica e forja outro `user_id`. Além disso o visitante **nunca é cadastrado como Person** no controlador, e a "validade" (`ValidUntil`) só existe no banco. Ou seja: ou o device valida o `user_id` contra um cadastro local (e aí o QR de visitante **nunca abre**, pois não há cadastro), ou aceita cegamente (e aí **expiração e revogação de visitante não têm efeito nenhum na porta**). Os comentários do código se contradizem sobre qual é o caso. O protocolo oferece caminhos nativos muito mais seguros:
- **Apêndice 8:** QR real = 14 bytes (cartão + validade em tempo comprimido + CRC8), cifrado com RC4 — tem validade embutida e checksum.
- **§8.1:** o registro de pessoa tem campo **Validade (BCD)** e **Effective Times (nº de usos)** — expiração validada **offline pelo próprio device**.
**Correção:** cadastrar o visitante como `Person` com `Expiry`/`OpenTimes` no(s) controlador(es) da visita (validação offline nativa), e/ou adotar o formato do Apêndice 8. Confirmar com o fabricante qual formato o leitor realmente consome.

---

## 2. SEGURANÇA (altos/médios)

- **S1 (alto)** — `GET /api/controllers/{id}` devolvia `CommunicationPassword` em **texto claro** (`ControllersController.cs:64`), e ela era persistida em claro no banco (TODO reconhecido em `AccessDbContext.cs:75`). ✅ resolvido: criptografada em repouso via DataProtection e nunca devolvida pela API (só `HasCommunicationPassword`).
- **S2 (alto)** — Login **sem rate limiting nem lockout** (`AuthController.cs:26`): brute force livre de senha de staff, sem auditoria de tentativas falhas. Usar o RateLimiter do .NET 8 + lockout + log.
- **S3 (alto)** — `GET /api/visitors/{id}/qrcode` gera QR para visitante **revogado ou expirado** (`VisitorsController.cs:151` só checa `ValidUntil != null`). Bloquear se `RevokedAtUtc != null` ou `ValidUntil < now`.
- **S4 (médio)** — Handler global de exceção em produção devolve **a mensagem da exceção** no corpo da resposta (`Program.cs:104`). Mesmo sendo ferramenta interna, vaza detalhes de stack/infra; preferir um ID de correlação + log.
- **S5 (médio)** — Sem token de **concorrência otimista** em nenhuma entidade (`AccessDbContext.cs`): duas edições simultâneas → last-write-wins silencioso. Adicionar `xmin`/rowversion nas entidades editáveis.
- **S6 (médio)** — Dado biométrico (LGPD): `FacePhoto` e `EventPhotos` são `bytea` sem política de retenção nem criptografia em repouso. Definir minimização/retenção e proteger.
- **S7 (médio)** — Exportação CSV sem proteção contra **CSV injection** (`AccessLogController.cs`): células iniciando com `= + - @` devem ser neutralizadas.
- **S8 (baixo)** — `JwtOptions.Key` default `""`; chave ausente/curta só falha no primeiro login. Fazer fail-fast no startup exigindo ≥32 bytes.

---

## 3. BUGS DE CORREÇÃO (altos/médios)

- **B1 (alto)** — **Recepção não consegue operar portas.** `ControllersController` tem `[Authorize(Roles="Admin,Operator")]` na classe e `[Authorize(...,"Reception")]` nos métodos de porta. No ASP.NET Core, múltiplos `[Authorize]` são combinados por **AND** → Reception é barrada pela regra da classe. Além disso `GET /api/controllers` exige Admin/Operator, então a tela de controladores fica vazia para Reception. (`ControllersController.cs:28,183`)
- **B2 (alto)** — **Código de evento 13 tratado como negado.** `GrantedTransactionCodes` excluía o 13, mas o protocolo (§9.2) define 13 = *Face+Fingerprint+Password* como verificação **válida** (faixa 1–14). O código que significa "não abre" é o **22**. Um acesso legítimo por face+digital+senha era registrado como negado. (`DoNetDriveGateway.cs:48`) ✅ resolvido: o `TransactionCodeClassifier` concede 1–14 e nega 22, com teste.
- **B3 (alto)** — **Fotos de evento pareadas por ordem de arquivo.** `ReadRecentEventPhotosAsync` associa foto↔registro por ordem cronológica de criação de arquivo, mas a entidade `CardAndImageTransaction` expõe `PhotoFile`/`PhotoDataBuf`. Registros sem foto (Photo==0) desalinham a lista e **atribuem a foto de uma pessoa ao código de outra** — grave em auditoria hospitalar. Também consome o ponteiro de "não lidos" (`AutoWriteReadIndex`), roubando eventos do coletor principal. (`DoNetDriveGateway.cs:430`)
- **B4 (alto)** — **Feriado bloqueia todo mundo.** O registro de pessoa tem um bitmap de 32 grupos de feriado (§8.1 campo 14); a pessoa só passa no feriado se o bit correspondente estiver ligado. O gateway sincroniza feriados mas **nunca chama `SetHolidayValue`** em nenhum `Person` (bitmap fica zerado). Ao cadastrar qualquer feriado, todos os funcionários passam a ser negados (evento 18) nesse dia. (`DoNetDriveGateway.cs:277`)
- **B5 (alto)** — **UserCode por `MAX+1`.** Corrida em cadastros concorrentes (índice único → 409 genérico, sem retry) e **reuso de código** de usuário excluído — como `AccessLog`/`UserAuditLog`/`EventPhoto` guardam `UserCode` como snapshot sem FK, o histórico do usuário antigo passa a apontar para o novo. Usar uma **sequence** do PostgreSQL. (`UsersController.cs:106`, `VisitorsController.cs:113`)
- **B6 (alto)** — **DateTimeKind vs `timestamptz`.** As colunas são `timestamp with time zone` e o Npgsql rejeita `DateTime` com `Kind=Unspecified/Local`. Um `ValidUntil`/`from`/`to` sem offset (caso natural de um `<input datetime-local>` no Brasil) → validação compara hora local como UTC **e** o `SaveChanges` pode estourar 500. Normalizar para UTC na borda + `ValueConverter` global; `Holiday.Date` deveria ser `DateOnly`. (`VisitorsController.cs:108,121`)
- **B7 (alto)** — **Delete de usuário só revoga as portas com `AccessPermission` atual** (`UsersController.cs:229`): controladores com revogação pendente/falha (não mais na lista de permissões) ficam com a credencial ativa. Revogar em todos os `DeviceSyncStatus`, não só nas permissões vigentes.
- **B8 (alto)** — **Descoberta UDP tende a não achar nada** (`DoNetDriveGateway.cs:246`): `SearchControltor_Parameter` sem `UDPBroadcast=true` (default false) e sem bind UDP local prévio (`OpenForciblyConnect`). O demo seta o flag e faz o bind. (não validável sem hardware, mas o desvio do padrão é claro)
- **B9 (médio)** — `MapAddFaceResult` (`DoNetDriveGateway.cs:117`) indexa `IdDataUploadStatus[0]` sem checar tamanho (risco de `IndexOutOfRange`) e ignora `UserUploadStatus` — falha no cadastro **da pessoa** é reportada como problema **da foto**.
- **B10 (médio)** — Timeout do alarme de porta era **clampado a 255 s** (`Math.Clamp(...,0,255)`, `:392`), mas o protocolo aceita até 65535 s → a escrita degradava silenciosamente o valor lido. ✅ resolvido: o clamp hoje usa a faixa completa do `ushort` (65535).
- **B11 (médio)** — Modo da senha de coação fixado em `1` na escrita e descartado na leitura (`:387`) → roundtrip reconfigura silenciosamente o comportamento de coação (trava vs destrava).
- **B12 (médio)** — `VisitorExpirationJob` (`:32`) **não marca** o visitante como expirado; reprocessa todos os vencidos (inclusive já revogados) a cada 5 min, indefinidamente. Filtrar por `RevokedAtUtc == null` e marcar após revogar.
- **B13 (médio)** — Leituras de config convertem falha em defaults silenciosos (`?? 0`/`?? false`, `:354`) — um roundtrip "ler e salvar" pode gravar defaults por cima da config real do device.
- **B14 (médio, Blazor)** — `ApiClient.GetJsonOrDefaultAsync` engole 401/403/500 e mostra **lista vazia como se fosse dado real** (`ApiClient.cs:390`).
- **B15 (médio, Blazor/React)** — Editar um controlador **zera** `RelayIndex`/`TimeoutMs`/`RestartCount` para `0/3000/3` (o form de edição não carrega esses campos): `Controllers.razor:210` e `ControllersPage.tsx:67`. Para controladoras em rede lenta, isso apaga o tuning e volta a dar timeout. ✅ confirmado no React.

---

## 4. FRONTEND

- **F1 (alto, React)** — Sem guarda de papel (RBAC): `RequireAuth` só checa `isAuthenticated` (`App.tsx:17`); Reception/Operator veem todos os menus e páginas cujas ações falham no servidor. Alinhar a UI ao `[Authorize]` do backend. ✅ confirmado
- **F2 (alto, React)** — Expiração do JWT não tratada: `isAuthenticated = token !== null` (`AuthContext.tsx:40`); token expirado continua "logado" e as chamadas falham com 401 sem redirecionar para login. Adicionar handler global de 401 → signOut. ✅ confirmado
- **F3 (médio)** — Duas UIs completas coexistiam (Blazor + React) com paridade de telas, dobrando o custo de manutenção e o risco de divergência (ex.: B15 existia nas duas). ✅ resolvido: o Blazor (`HospitalAccess.Web`) foi removido do repositório; o React é o único front-end.

---

## 5. LACUNAS DE IMPLEMENTAÇÃO (o hardware suporta, o sistema não usa)

- **L1 (alto)** — **Coleta de registros offline.** O 8190H mantém 4 bancos circulares com ponteiro de "upload breakpoint" (§9.6) justamente para o software drenar o que aconteceu enquanto esteve desconectado. Não há nenhum job de coleta (`ReadTransactionDatabase` com breakpoint) — todo evento ocorrido com o servidor fora do ar (ou, hoje, sempre, por causa de C1) é **perdido**. Crítico para auditoria hospitalar.
- **L2 (alto)** — **Health-check/heartbeat dos 30 controladores.** Existe `ReadSystemStatus` (§2.1: relé, NA, sensor, bitmask de alarmes, trava, monitor) e keep-alive periódico. O sistema não tem `LastSeenUtc`, estado online/offline, nem dashboard de "porta online/em alarme". A HomePage é vazia.
- **L3 (alto)** — **Controles de emergência em massa.** Num hospital, evacuação exige liberar todas as portas com um clique (`HoldDoor` em massa) e/ou disparar o alarme de incêndio (`SendFireAlarm`, §5). Hoje só há comando individual por porta. Adicionar endpoints de emergência (Admin, auditados).
- **L4 (médio)** — Keep-alive (`0x22`) e teste de conexão (`0xA0`) do device não são respondidos com `SendConnectTestResponse` (o demo marca isso como obrigatório) — o controlador pode considerar o servidor offline.
- **L5 (médio)** — `TimeGroup = 0` (acesso **ilimitado**, §8.1 campo 5) é impossível na API: validação exige `1..64`. E `SyncTimeGroupsAsync` grava os 64 grupos de uma vez; grupos sem definição no banco viram "sempre fechado" — se o grupo default não estiver definido, o sync-all bloqueia usuários.
- **L6 (médio)** — Sem trilha de auditoria dos **comandos de porta** (quem abriu/trancou qual porta, quando). `UserAuditLogger` cobre só CRUD de usuário. Requisito básico de auditoria hospitalar.
- **L7 (médio)** — Feriados/grade horária: `sync-all` sem estado por dispositivo (falha vira só log; operador não vê qual dos 30 falhou), e o CRUD não dispara sync (esquecer = devices desatualizados). Reaproveitar o padrão `DeviceSyncStatus`.
- **L8 (médio)** — Sem enrollment on-device (`0x07 0x20`), digitais (o hardware suporta 10/pessoa) nem cadastro de face capturada no próprio terminal — só upload de JPG. Avaliar se é escopo.
- **L9 (baixo)** — `FaceImageConverter` devolve os bytes originais sem garantir que são JPEG: um PNG/WebP ≤480×640 e ≤120KB passa direto e o device recebe formato não suportado. README afirma "só JPG" mas nada valida. Checar o formato decodificado e reencodar.

---

## 6. REPOSITÓRIO / PROCESSO

- **R1 (alto)** — ~51 MB de binários do fabricante (`.rar` + `.doc`) commitados na **raiz** do repositório. Mover para release/artefato ou Git LFS.
- **R2 (alto)** — Cobertura de testes quase nula na data da revisão: só o QR (3 testes), sem teste em Gateway, Sync, Auth ou conversão de imagem — as áreas onde estavam os bugs C4–C6/B-series. ✅ resolvido em rodadas sucessivas: a suíte atual tem 76 casos em 10 arquivos (classifier, conversor, fila de sync, política de retry/backoff, diff de campos, single-flight, grupos, cartão de visitante, cliente HTTP).
- **R2b (baixo)** — 9 DLLs do SDK versionadas em `lib/` sem hash/assinatura verificável. Documentar versões e checksums.
- **R3 (médio)** — Sem CI (build/test/`dotnet list package --vulnerable`/npm audit em pipeline).
- **R4 (baixo)** — README contradiz o estado real: manda "obter as DLLs do fabricante e copiar para `lib/`", mas elas já estão commitadas (`README.md:204`).
- **R5 (baixo)** — `docs/instalacao-servidor-linux.md:282`: comando de atualização de banco não funcionava como escrito no servidor de produção. ✅ resolvido: a seção 13 do guia hoje instrui explicitamente a NÃO rodar `dotnet ef` no servidor e documenta os caminhos `psql -f migracao.sql`/bundle.
- **R6 (info)** — Troca de senha de `StaffUser` e cadastro de operadores/recepcionistas só via banco (reconhecido no README).

---

## 7. Achado do workflow que reverifiquei e **refutei**

- ❌ **"OnTransactionMessage faz o cast errado (INData bruto → CardTransaction)"** — está **correto**: o cast direto sobre o `EventData` bruto quando `CmdIndex == 1` é **exatamente** o padrão do demo oficial (`ConnectorAllocatorTestClass.cs:91`). Não é bug. (Registrado para evitar retrabalho.)

---

## Prioridade sugerida de correção

1. **Antes de qualquer piloto com hardware:** validar C3 (transporte) — define se o resto do gateway funciona.
2. **Bloqueadores de funcionalidade:** C1 (BeginWatch), C2 (alarmes), C4 (status de comando), C5 (re-sync na edição), C6 (retry job).
3. **Segurança/integridade:** C7 (visitante/QR), B2, B4, B7, S1–S3.
4. **Robustez de dados:** B5, B6, B12, S5.
5. **Operacional hospitalar:** L1 (coleta offline), L2 (health-check), L3 (emergência), L6 (auditoria de portas).
6. **Processo:** R1–R3.

---

## Adendo 2026-07-16 — Revisão de tráfego de rede (sobrecarga dos controladores)

Nova rodada de revisão, motivada por **tráfego excessivo sobrecarregando os aparelhos**, com o
diagnóstico verificado contra o protocolo oficial em `vendor/`. Constatação central: todo o
tráfego que atinge os dispositivos é gerado no servidor (o front-end só consulta o banco) e as
fontes de amplificação eram, em ordem de impacto:

| # | Fonte de tráfego | Correção aplicada |
|---|------------------|-------------------|
| T1 | Retry de sync a cada 2 min **sem backoff/teto**: re-upload da face (~120 KB) de todo Pending/Failed para sempre — inclusive falhas permanentes (foto sem rosto, duplicidade) que nunca teriam sucesso; o SDK ainda re-enviava cada comando 3× (`RestartCount`) | Backoff exponencial 2→60 min + **quarentena** de falha permanente (`SyncRetryPolicy`, `DeviceSyncStatus.NextRetryAtUtc`); ações manuais destravam; upload de face com `RestartCount=1` no fio (`Sync:*`) |
| T2 | Qualquer edição de usuário (até só telefone) re-enviava a face para **todas** as portas | Diff dos campos que vão ao aparelho (`UserDeviceFields`): edição administrativa = zero tráfego |
| T3 | Visitantes fora da fila serial (`Task.Run` soltos): concorrência ilimitada + corrida com o retry; `ResyncAll`/sync-all sem guarda de reentrância | Visitantes na `UserSyncQueue` (incl. novo `ChangeVisitorRoomWork`); `SingleFlight` com 409 nas operações pesadas. Bônus: corrigido o ChangeRoom que apagava os `SyncStatuses` antigos e deixava a pessoa cadastrada via SDK no quarto antigo |
| T4 | Re-arme cego do monitoramento a cada 5 min (`OpenForciblyConnect` + `BeginWatch` em todos) | Condicionado: push recente → zero comandos; senão `ReadWatchState` (0x01 0x0B 0x02) e `BeginWatch` só em quem está desligado |
| T5 | Coletor offline re-lendo registros já entregues pelo push, a cada 15 min em todos | Intervalo configurável + flag opt-in `SkipWhenPushHealthy` (default OFF até validar em hardware que o push avança o ponteiro) |
| T6 | Health-check com TCP connect na **porta do SDK** a cada 1 min (com ICMP bloqueado) | Ordem: push recente (sem sonda) → ICMP → porta do painel HTTP → porta SDK só como último recurso (`HealthCheck:AllowSdkPortFallback`) |
| T7 | Recorders de evento em fire-and-forget por evento (escopos DI/DbContext sem limite numa rajada de push) | `Channel` limitado + consumidor único em micro-lote (dedup em query única) |
| T8 | Nenhum intervalo configurável (constantes hardcoded) | Todas as cadências em `appsettings` (`Sync`, `SyncQueue`, `Monitoring`, `OfflineCollection`, `HealthCheck`), defaults = comportamento anterior |

Limpezas da mesma rodada: `RetryPendingAsync` (código morto) removido; `Controller.RelayIndex`
(ignorado pelo gateway) removido de entidade/API/React + migration; `ForgetController` limpa o
estado local do gateway ao excluir um controlador; endpoint `/controllers/status` com agregados
em lote; polling do dashboard unificado num único intervalo por aba
(`controllerStatusStore.ts`); documentação corrigida (alarmes via `SystemTransaction`, "só JPG",
comentário do Qr no appsettings, citações "§10"→Classe I/IX, porta 8101 do firewall, resíduos
Blazor).

**Pendências de validação em hardware desta rodada** (flags conservadoras por default):
decodificação do `ReadWatchState` no aparelho real; cadência do keep-alive (thresholds de
push-idle); se o push avança o ponteiro de leitura (antes de ligar `SkipWhenPushHealthy`);
taxa de sucesso do upload de face com `RestartCount=1`; revogação via SDK do quarto antigo no
ChangeRoom.
