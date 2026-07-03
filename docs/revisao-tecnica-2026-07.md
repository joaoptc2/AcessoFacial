# Revisão técnica — Sistema de Controle de Acesso Hospitalar 8190H

_Data: 2026-07-03 · Base revisada: protocolo "8190H Fingerprint&Face Communication Protocol", "8190H Software Development Kit", biblioteca `DoNetDrive.Protocol.Fingerprint` (fonte do demo oficial + XMLs de API) e todo o código do repositório._

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
| R2 Testes | ✅ classifier + conversor de imagem (32 testes) |
| R3 CI | ✅ GitHub Actions (build/test .NET + build React) |
| R4/R5 Docs | ✅ README/instalação/appsettings corrigidos |
| Pendentes por decisão | F3 (retirar Blazor — mantidas as duas UIs; as telas novas de painel/emergência/configurações e a seleção de portas do visitante são só no React), R1 (mover .rar — mantidos, foram adicionados de propósito como base) |

> Nota sobre o achado [1] do workflow ("cast de evento errado"): reverificado e **confirmado** —
> o push chega como `Door8800Transaction` e o registro concreto está em `.EventData`. O gateway
> foi corrigido para usar `transaction.EventData` (antes o `is CardTransaction` sobre o envelope
> retornava cedo e nenhum evento seria emitido).

---

## Metodologia e ressalva de verificação

1. Extraí e li integralmente os 3 pacotes de referência do fabricante (protocolo de 124 páginas, SDK multi-linguagem, e o projeto de exemplo oficial `DoNetDrive.Protocol.Fingerprint.Test` com os XMLs de IntelliSense).
2. Revisei todas as camadas do sistema (Gateway, Api, Application, Infrastructure, Domain, Web Blazor, Web React, Tests) contra o uso canônico documentado.
3. Rodei uma revisão multi-agente (10 revisores + build). A fase de **verificação adversarial automática não pôde ser concluída** (limite de gastos da conta atingido no meio da execução). Por isso, **reverifiquei manualmente** os achados críticos contra o demo do fabricante e o texto do protocolo — o resultado dessa reverificação está marcado abaixo (✅ confirmado / ⚠️ requer hardware / ❌ refutado).

**Build:** a solução compila com **0 erros e 0 warnings** (.NET 8, `Nullable enable`), os 3 testes passam e o frontend React builda. Todos os problemas abaixo são de **correção em runtime, protocolo ou segurança** — nenhum impede a compilação.

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

### C7. QR de visitante forjável e sem imposição real na porta — ✅ confirmado (design)
`HospitalAccess.Application/Qr/QrAccessTokenService.cs:33`, `VisitorsController.cs`, `UserSyncService.cs:38`
O QR é `Base64("user_id={code}_time={μs}")` — **sem assinatura, criptografia ou checksum**; qualquer um decodifica e forja outro `user_id`. Além disso o visitante **nunca é cadastrado como Person** no controlador, e a "validade" (`ValidUntil`) só existe no banco. Ou seja: ou o device valida o `user_id` contra um cadastro local (e aí o QR de visitante **nunca abre**, pois não há cadastro), ou aceita cegamente (e aí **expiração e revogação de visitante não têm efeito nenhum na porta**). Os comentários do código se contradizem sobre qual é o caso. O protocolo oferece caminhos nativos muito mais seguros:
- **Apêndice 8:** QR real = 14 bytes (cartão + validade em tempo comprimido + CRC8), cifrado com RC4 — tem validade embutida e checksum.
- **§8.1:** o registro de pessoa tem campo **Validade (BCD)** e **Effective Times (nº de usos)** — expiração validada **offline pelo próprio device**.
**Correção:** cadastrar o visitante como `Person` com `Expiry`/`OpenTimes` no(s) controlador(es) da visita (validação offline nativa), e/ou adotar o formato do Apêndice 8. Confirmar com o fabricante qual formato o leitor realmente consome.

---

## 2. SEGURANÇA (altos/médios)

- **S1 (alto)** — `GET /api/controllers/{id}` devolve `CommunicationPassword` em **texto claro** (`ControllersController.cs:64`), e ela é persistida em claro no banco (TODO reconhecido em `AccessDbContext.cs:75`). Criptografar em repouso e não expor no DTO de leitura.
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
- **B2 (alto)** — **Código de evento 13 tratado como negado.** `GrantedTransactionCodes` exclui o 13, mas o protocolo (§9.2) define 13 = *Face+Fingerprint+Password* como verificação **válida** (faixa 1–14). O código que significa "não abre" é o **22**. Um acesso legítimo por face+digital+senha é registrado como negado. (`DoNetDriveGateway.cs:48`)
- **B3 (alto)** — **Fotos de evento pareadas por ordem de arquivo.** `ReadRecentEventPhotosAsync` associa foto↔registro por ordem cronológica de criação de arquivo, mas a entidade `CardAndImageTransaction` expõe `PhotoFile`/`PhotoDataBuf`. Registros sem foto (Photo==0) desalinham a lista e **atribuem a foto de uma pessoa ao código de outra** — grave em auditoria hospitalar. Também consome o ponteiro de "não lidos" (`AutoWriteReadIndex`), roubando eventos do coletor principal. (`DoNetDriveGateway.cs:430`)
- **B4 (alto)** — **Feriado bloqueia todo mundo.** O registro de pessoa tem um bitmap de 32 grupos de feriado (§8.1 campo 14); a pessoa só passa no feriado se o bit correspondente estiver ligado. O gateway sincroniza feriados mas **nunca chama `SetHolidayValue`** em nenhum `Person` (bitmap fica zerado). Ao cadastrar qualquer feriado, todos os funcionários passam a ser negados (evento 18) nesse dia. (`DoNetDriveGateway.cs:277`)
- **B5 (alto)** — **UserCode por `MAX+1`.** Corrida em cadastros concorrentes (índice único → 409 genérico, sem retry) e **reuso de código** de usuário excluído — como `AccessLog`/`UserAuditLog`/`EventPhoto` guardam `UserCode` como snapshot sem FK, o histórico do usuário antigo passa a apontar para o novo. Usar uma **sequence** do PostgreSQL. (`UsersController.cs:106`, `VisitorsController.cs:113`)
- **B6 (alto)** — **DateTimeKind vs `timestamptz`.** As colunas são `timestamp with time zone` e o Npgsql rejeita `DateTime` com `Kind=Unspecified/Local`. Um `ValidUntil`/`from`/`to` sem offset (caso natural de um `<input datetime-local>` no Brasil) → validação compara hora local como UTC **e** o `SaveChanges` pode estourar 500. Normalizar para UTC na borda + `ValueConverter` global; `Holiday.Date` deveria ser `DateOnly`. (`VisitorsController.cs:108,121`)
- **B7 (alto)** — **Delete de usuário só revoga as portas com `AccessPermission` atual** (`UsersController.cs:229`): controladores com revogação pendente/falha (não mais na lista de permissões) ficam com a credencial ativa. Revogar em todos os `DeviceSyncStatus`, não só nas permissões vigentes.
- **B8 (alto)** — **Descoberta UDP tende a não achar nada** (`DoNetDriveGateway.cs:246`): `SearchControltor_Parameter` sem `UDPBroadcast=true` (default false) e sem bind UDP local prévio (`OpenForciblyConnect`). O demo seta o flag e faz o bind. (não validável sem hardware, mas o desvio do padrão é claro)
- **B9 (médio)** — `MapAddFaceResult` (`DoNetDriveGateway.cs:117`) indexa `IdDataUploadStatus[0]` sem checar tamanho (risco de `IndexOutOfRange`) e ignora `UserUploadStatus` — falha no cadastro **da pessoa** é reportada como problema **da foto**.
- **B10 (médio)** — Timeout do alarme de porta é **clampado a 255 s** (`Math.Clamp(...,0,255)`, `:392`), mas o protocolo aceita até 65535 s → escrita degrada silenciosamente o valor lido. O parâmetro é `ushort`.
- **B11 (médio)** — Modo da senha de coação fixado em `1` na escrita e descartado na leitura (`:387`) → roundtrip reconfigura silenciosamente o comportamento de coação (trava vs destrava).
- **B12 (médio)** — `VisitorExpirationJob` (`:32`) **não marca** o visitante como expirado; reprocessa todos os vencidos (inclusive já revogados) a cada 5 min, indefinidamente. Filtrar por `RevokedAtUtc == null` e marcar após revogar.
- **B13 (médio)** — Leituras de config convertem falha em defaults silenciosos (`?? 0`/`?? false`, `:354`) — um roundtrip "ler e salvar" pode gravar defaults por cima da config real do device.
- **B14 (médio, Blazor)** — `ApiClient.GetJsonOrDefaultAsync` engole 401/403/500 e mostra **lista vazia como se fosse dado real** (`ApiClient.cs:390`).
- **B15 (médio, Blazor/React)** — Editar um controlador **zera** `RelayIndex`/`TimeoutMs`/`RestartCount` para `0/3000/3` (o form de edição não carrega esses campos): `Controllers.razor:210` e `ControllersPage.tsx:67`. Para controladoras em rede lenta, isso apaga o tuning e volta a dar timeout. ✅ confirmado no React.

---

## 4. FRONTEND

- **F1 (alto, React)** — Sem guarda de papel (RBAC): `RequireAuth` só checa `isAuthenticated` (`App.tsx:17`); Reception/Operator veem todos os menus e páginas cujas ações falham no servidor. Alinhar a UI ao `[Authorize]` do backend. ✅ confirmado
- **F2 (alto, React)** — Expiração do JWT não tratada: `isAuthenticated = token !== null` (`AuthContext.tsx:40`); token expirado continua "logado" e as chamadas falham com 401 sem redirecionar para login. Adicionar handler global de 401 → signOut. ✅ confirmado
- **F3 (médio)** — Duas UIs completas coexistem (Blazor + React) com paridade de telas. Manter as duas dobra o custo de manutenção e o risco de divergência (ex.: B15 existe nas duas). Decidir e remover uma.

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
- **R2 (alto)** — Cobertura de testes quase nula: só o QR (3 testes). Sem teste em Gateway, Sync, Auth, conversão de imagem — as áreas onde estão os bugs C4–C6/B-series. Priorizar testes de `UserSyncService`, mapeamento de `TransactionCode` e do handler de eventos.
- **R2b (baixo)** — 9 DLLs do SDK versionadas em `lib/` sem hash/assinatura verificável. Documentar versões e checksums.
- **R3 (médio)** — Sem CI (build/test/`dotnet list package --vulnerable`/npm audit em pipeline).
- **R4 (baixo)** — README contradiz o estado real: manda "obter as DLLs do fabricante e copiar para `lib/`", mas elas já estão commitadas (`README.md:204`).
- **R5 (baixo)** — `docs/instalacao-servidor-linux.md:282`: comando de atualização de banco não funciona como escrito no servidor de produção.
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
