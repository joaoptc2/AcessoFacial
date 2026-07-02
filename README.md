# Sistema de Controle de Acesso Hospitalar — 8190H (A33_Face)

Solução para gerenciar **30 controladores faciais 8190H** conectados por **TCP/IP**,
com cadastro de face por upload, QR Code de acesso para visitantes, gestão de
portas/permissões, sincronização multi-dispositivo e log de acessos auditável.
Rodando **on-premise** (.NET 8 + PostgreSQL).

---

## ⚠️ Decisões de protocolo — leia antes de mexer no Gateway

### 1. Assinaturas do SDK — confirmadas via código-fonte + engenharia reversa do IL
Os nomes de classe (`CommandDetailFactory`, `AddPeosonAndImage`, `AddPersonAndImage_Parameter`,
`IdentificationData`, `ConnectorAllocator`, `ReadSN`, `Data.Person`, `OpenDoor`, etc.) foram
extraídos do projeto de exemplo real `DoNetDrive.Protocol.Fingerprint.Test`
(`TestClass.cs`, `frmPerson.cs`, `frmDoor.cs`, `frmRecord.cs`, `ConnectorAllocatorTestClass.cs`)
e, para o ponto mais ambíguo (`ConnectType`), validados também via `monodis` no IL das DLLs
reais. Repare na grafia `AddPeosonAndImage` (sem "r" em "Peoson") — é assim no SDK.

### 2. TCP vs UDP — resolvido
`CommandDetailFactory.ConnectType` tem exatamente 3 membros (confirmado por IL):
`TCPClient = 0`, `TCPServerClient = 1`, `UDPClient = 2`. Como os 30 controladores são
on-premise com IP fixo conhecido (nosso software disca para eles), `ControllerConnectionFactory`
usa **`ConnectType.TCPClient`** — não `TCPServerClient` (esse é para quando o *controlador*
disca para um servidor nosso, modo "phone home", que não é o nosso cenário) nem `UDPClient`
(só demonstrado no exemplo `TestClass.cs`, não usado aqui).

⚠️ **Caveat não testado com hardware real**: o gateway assume que o 8190H empurra os
eventos de acesso em tempo real (`ConnectorAllocator.TransactionMessage`) pela mesma conexão
TCP que nós abrimos como cliente. Isso não foi confirmado contra um dispositivo físico (não
há um 8190H acessível neste ambiente de desenvolvimento). Validar isso é o primeiro passo ao
ligar o primeiro controlador real — se os eventos não chegarem, o fallback é configurar os
controladores em modo "phone home" (`WriteNetworkServerDetail`) para um `TCPServer` nosso, o
que muda apenas o `ConnectType` em `ControllerConnectionFactory`, não o resto do gateway.

### 3. Imagem de face
Convertida para **480×640 px, ≤120 KB (122880 bytes)** antes do upload. O helper do
fabricante (`ImageTool.ConvertImage`) usa `System.Drawing`/GDI+, que é **Windows-only** desde
o .NET 7 — inviável para uma API ASP.NET Core multiplataforma. Reimplementado em
`HospitalAccess.Gateway.Imaging.FaceImageConverter` com **ImageSharp** (mesma lógica: resize
preservando aspect ratio, canvas branco 480×640, redução iterativa de qualidade JPEG até
caber no limite). Só JPG é aceito. Retornos tratados: `2` (feature code não identificável),
`3` (sem rosto), `4` (duplicada), `0`/outros (falha de CRC32), resultado nulo (handle 0 =
usuário inexistente).

### 4. `WaitRepeatMessage`
Configurável por controlador via `Controller.SupportsWaitRepeatMessage` (só firmware ≥ v4.28).

### 5. QR de acesso: formato corrigido em 2026-07-02 (era diferente do documento de protocolo)
A implementação original seguia o Appendix 8 do documento de protocolo: 14 bytes binários
(9 cartão + 4 validade em tempo comprimido + 1 CRC8), cifrados com RC4. O cliente enviou um
QR real e funcional gerado pelo sistema oficial do fabricante e confirmou que **é esse QR
que a câmera do controlador lê para abrir a porta** — e o conteúdo decodificado não bate
nada com o formato do documento. O QR de referência (Base64 → ASCII) é:

```
user_id=1_time=1782921297138761
```

Ou seja: **texto simples** `user_id={UserCode}_time={microssegundos desde a época Unix UTC}`,
codificado em Base64. `QrAccessTokenService.BuildAccessToken(userCode, timestampUtc)` foi
reescrito para gerar exatamente esse formato — validado por um teste golden-vector contra o
QR de referência (`QrAccessTokenServiceTests.BuildAccessToken_MatchesVendorReferenceFormat`).
O código RC4/CRC8/tempo-comprimido antigo (`Rc4.cs`, `VisitorCardNumber.cs`) foi removido por
estar incorreto.

⚠️ **Ainda não confirmado**: este formato não tem nenhuma criptografia/checksum — qualquer
`user_id` pode ser forjado a partir do texto decodificado. Isso só é seguro se o controlador
validar contra um servidor (o `user_id` está autorizado agora?) em vez de confiar cegamente
no QR offline; não temos visibilidade de qual dos dois modelos o hardware realmente usa.
Também não está confirmado se `time` é o instante de geração (nossa leitura — o valor de
referência bate com "agora" no momento em que o QR de teste foi gerado) ou alguma janela de
validade/replay diferente. `Visitor.ValidUntil` continua sendo a validade que o NOSSO sistema
controla (`IVisitorExpirationJob` revoga automaticamente) — não é (e talvez nunca tenha sido)
o que o hardware usa para decidir se deixa a porta abrir.

### 6. Controlador = porta (não existe entidade `Door` separada)
O hardware 8190H tem **um único relé/porta por controlador** — não há como um controlador
comandar mais de uma porta. Por isso não existe uma entidade `Door`: `Controller` já
representa fisicamente a porta (campo `RelayIndex`, sempre `0`, mantido só por
compatibilidade futura caso surja um modelo multi-relé). `AccessPermission` liga
`User`→`Controller` diretamente. Os comandos de porta expostos pelo SDK — `OpenDoor`,
`CloseDoor`, `HoldDoor` (manter aberta), `LockDoor` (trancar mesmo para credencial válida)
e `UnlockDoor` (reverter o trancamento) — ficam em `IDeviceGateway` e são expostos por
controlador em `POST /api/controllers/{id}/{open|close|hold-open|lock|unlock}`.

### 7. Sincronização de usuário roda em segundo plano (fire-and-forget)
Criar/editar/excluir um usuário grava no banco e dispara a sincronização com os
controladores (`IUserSyncService`) **sem aguardar o resultado na requisição HTTP**: o
comando ao hardware é TCP com retries e pode levar minutos se um controlador estiver
inacessível, o que travaria a tela por tempo indefinido se fosse síncrono (bug encontrado
via teste end-to-end contra um IP inexistente). O progresso fica em `DeviceSyncStatus`
(Pending/Synced/Failed) e é reprocessado por `IUserSyncService.RetryPendingAsync` (job
periódico). Exclusão de usuário: como a linha do `User` (e o `DeviceSyncStatus` em
cascata) é apagada na hora, o `UserCode` e a lista de controladores são capturados
*antes* do delete, e a revogação no hardware roda depois, direto pelo `IDeviceGateway`
(ver `UsersController.RevokeDeletedUserInBackground`).

### 8. Segurança física (fora do software)
A política **fail-safe vs fail-secure** das portas em queda de energia/rede (isto é: se a
fechadura trava ou libera quando falta energia ou a rede cai) é decisão de projeto físico
predial e **deve ser definida com a equipe de segurança/manutenção do hospital**, em
conjunto com o fabricante da fechadura/fecho eletromagnético de cada porta. O software não
controla nem pode controlar esse comportamento — ele só comanda abertura/fechamento em
operação normal. **Documente a decisão tomada para cada porta fisicamente**, fora deste
repositório (ex.: planilha de portas do projeto elétrico/predial).

### 9. Rede/descoberta, relógio, feriados, grade horária, alarmes, ajustes locais, foto de evento, leitura reversa e Mifare
Funções adicionadas depois da auditoria inicial do protocolo (ver seção "Funções do protocolo
não implementadas" mais abaixo — a maioria virou item desta lista):

- **Rede** (`ReadTCPSetting`/`WriteTCPSetting`, namespace `Door.Door8800.SystemParameter.TCPSetting`):
  ver/reconfigurar IP/máscara/gateway/DNS/portas de um controlador já cadastrado, em
  `GET/PUT /api/controllers/{id}/network` (aba "Rede" na tela de detalhes do controlador).
- **Descoberta** (`SearchControltor`, broadcast UDP com SN especial `0000000000000000` e senha
  `FFFFFFFF`): `POST /api/controllers/discover` varre a rede por alguns segundos e coleta as
  respostas via `CommandCompleteEvent`. **Não validado contra hardware real** — é o item de
  maior risco desta leva, já que broadcast UDP multi-resposta é um padrão diferente do
  request/response usual do restante do gateway.
- **Relógio** (`ReadTime`/`WriteTime`, namespace `Door.Door8800.Time`): `WriteTime` sem
  parâmetro grava o horário atual deste servidor no controlador. `GET/POST /api/controllers/{id}/clock[/sync]`,
  aba "Relógio".
- **Feriados** (Classe V, `Door.Door8800.Holiday`) e **grade horária** (Classe VI,
  `Door.Door8800.TimeGroup` + `Door.Door8800.Data.TimeGroup.WeekTimeGroup/DayTimeGroup/TimeSegment`):
  definições globais mantidas no banco (`Holiday`, `TimeGroupSchedule`/`TimeGroupSegment`) e
  empurradas para **todos** os controladores sob demanda (`POST /api/holidays/sync-all`,
  `POST /api/timegroups/sync-all` — em segundo plano, mesmo padrão fire-and-forget do item 7).
  A tela de grade horária simplifica para uma janela por dia (o dispositivo suporta até 8);
  o domínio (`TimeGroupSegment.SegmentIndex`) já comporta mais, se precisar no futuro.
  Antes disso, o campo `TimeGroup` do usuário era gravado no dispositivo mas **nada definia o
  que ele significava em termos de horário** — essa é a lacuna que este item fecha.
- **Alarmes** (Classe IIII, `Fingerprint.Alarm.*`): configuração liga/desliga por tipo
  (incêndio, lista negra, sabotagem, credencial inválida, coação, timeout de abertura,
  liberação por credencial válida) em `GET/PUT /api/controllers/{id}/alarm-settings`, mais
  `POST /api/controllers/{id}/alarm-clear` para silenciar. O alarme de sensor de porta
  (magnético) não tem tela de configuração (a escrita exige uma grade horária completa no
  protocolo — desproporcional para esta função), mas o **evento** ainda é capturado em
  tempo real. Eventos de alarme chegam pelo mesmo `TransactionMessage` do SDK, discriminados
  pelo tipo concreto `AlarmTransaction` (`DoNetDriveGateway.OnTransactionMessage`), gravados
  em `AlarmEvent` (`AlarmEventRecorder`, mesmo padrão do `AccessEventRecorder`) e consultáveis
  em `/api/alarmevents` (com CSV).
- **Ajustes locais do quiosque** (`Fingerprint.SystemParameter.*`: idioma, volume, luz de
  preenchimento, detecção de máscara, temperatura — detecção/alarme/exibição —, distância de
  reconhecimento facial, detecção de vida/liveness): `GET/PUT /api/controllers/{id}/kiosk-settings`,
  aba "Ajustes Locais". "Não perturbe"/clima/indicador de limpeza do protocolo original **não
  têm classe correspondente neste SDK** (provavelmente uma versão de firmware/SDK mais nova) —
  não implementados.
- **Foto do evento** (Classe XI, `ReadTransactionAndImageDatabase`): o SDK grava as fotos em
  disco (pasta temporária por controlador), não em memória — o gateway lê os arquivos depois
  e associa a cada registro **pela ordem cronológica** (o nome de arquivo gerado pelo SDK não
  é documentado no material de referência disponível). `POST /api/controllers/{id}/event-photos/download`
  + `GET .../event-photos` + `GET .../event-photos/{photoId}/image`, persistidas em `EventPhoto`.
- **Leitura reversa / auditoria** (`ReadPersonDataBase`, Classe VII): compara os `UserCode`
  efetivamente cadastrados no controlador com as permissões do banco, sem persistir nada —
  auditoria sob demanda em `GET /api/controllers/{id}/personnel-audit`.
- **Cartões Mifare**: o SDK **não abstrai a estrutura de setor Mifare** (Apêndices 10-13 do
  protocolo) — só expõe `Person.CardData` (um `uint`). Por isso o suporte aqui se limita a
  associar um número de cartão a um usuário (`User.CardNumber`), enviado ao controlador
  junto com o cadastro de face normal. Programar/formatar o cartão físico (senha dinâmica de
  setor, código de rolagem) exigiria um leitor/gravador de cartão dedicado, fora do alcance
  deste sistema.

⚠️ Estas 8 áreas foram implementadas contra as DLLs reais do SDK (compilação verificada,
nomes de classe/propriedade confirmados via `monodis` quando o código de exemplo não deixava
claro), mas **nenhuma delas foi testada contra um 8190H físico** — mesma limitação já
registrada nas seções 1-2 para o restante do gateway.

---

## Arquitetura

```
┌──────────────────────┐     REST      ┌──────────────────────┐
│  HospitalAccess.Api   │ ◀──────────▶ │  HospitalAccess.Web    │
│  (+ Application/      │               │  (Blazor Server)       │
│   Domain/Infra)       │               └──────────────────────┘
└─────────┬─────────────┘
          │ IUserSyncService / IDeviceGateway
┌─────────▼─────────────┐   protocolo binário DoNetDrive (TCP/IP)
│ HospitalAccess.        │ ◀──────────────────────────────────▶  30x 8190H
│ Gateway (SDK DoNetDrive)│      eventos de acesso em tempo real
└────────────────────────┘
```

- **HospitalAccess.Domain** — entidades e enums, sem dependência de framework
  (`Controller` = porta física, `UserGroup` para organização de usuários).
- **HospitalAccess.Application** — QR (protocolo puro), contratos de sincronização.
- **HospitalAccess.Infrastructure** — EF Core/PostgreSQL, encoder de QR (QRCoder).
- **HospitalAccess.Gateway** — único componente que fala com o hardware (SDK `DoNetDrive.*`).
- **HospitalAccess.Api** — API REST + JWT + hosted services (sync de visitantes, escuta de eventos).
- **HospitalAccess.Web** — front-end Blazor Server, consome a API via HTTP.
- **HospitalAccess.Tests** — testes unitários (protocolo do QR: bit-packing, CRC8, RC4).

## Setup on-premise

> Para um passo a passo completo de instalação em servidor Linux de produção
> (systemd, Nginx com WebSocket, usuário dedicado, backups), veja
> [`docs/instalacao-servidor-linux.md`](docs/instalacao-servidor-linux.md).
> A seção abaixo é o setup rápido para desenvolvimento local.

### Pré-requisitos
- .NET 8 SDK
- PostgreSQL (12+)
- As DLLs do SDK **DoNetDrive.\*** obtidas do fabricante (não há feed NuGet público):
  `DoNetDrive.Common` (1.17.0), `DoNetDrive.Core` (2.9.0), `DoNetDrive.Protocol` (2.4.0),
  `DoNetDrive.Protocol.Door` (2.8.0), `DoNetDrive.Protocol.Fingerprint` (2.20.0),
  `DoNetDrive.Protocol.Util` (1.16.0), `DotNetty.Buffers`/`DotNetty.Common`/`DotNetty.Transport`
  (0.7.0) — copie-as para `HospitalAccess.Gateway/lib/` (referenciadas via `<Reference HintPath>`
  no `.csproj`, já que não existe pacote NuGet do fabricante).

### Pacotes NuGet (públicos, restauram normalmente)
`Microsoft.EntityFrameworkCore` + `Npgsql.EntityFrameworkCore.PostgreSQL`, `QRCoder`,
`SixLabors.ImageSharp` (fixado em `2.1.x`/Apache-2.0 — **não subir para 3.x** sem revisar a
licença comercial "Six Labors Split License"), `Microsoft.AspNetCore.Authentication.JwtBearer`,
`Swashbuckle.AspNetCore`, `xunit`.

### Segredos (nunca versionar)
Em desenvolvimento, use `dotnet user-secrets` a partir de `HospitalAccess.Api/`:
```bash
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Database=hospital_access;Username=app;Password=..."
dotnet user-secrets set "Jwt:Key" "<32+ bytes aleatórios>"
dotnet user-secrets set "Seed:AdminUsername" "admin"
dotnet user-secrets set "Seed:AdminPassword" "<senha forte, trocar após o primeiro login>"
```
Em produção, use variáveis de ambiente ou um cofre de segredos (Key Vault, etc.) — nunca
`appsettings.json` versionado. Veja `appsettings.example.json` para as chaves esperadas.
Senhas de comunicação de cada controlador (`Controller.CommunicationPassword`) ficam no
banco — considere criptografia em repouso ou uma coluna protegida se o hospital exigir.

### Banco de dados
```bash
dotnet tool install --global dotnet-ef
dotnet ef database update --project HospitalAccess.Infrastructure --startup-project HospitalAccess.Api
```

### Rodando
```bash
# Terminal 1 — API (porta 5080 no exemplo)
cd HospitalAccess.Api && ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://localhost:5080

# Terminal 2 — Front-end (porta 5100 no exemplo; ajuste Api:BaseUrl no appsettings.json do Web)
cd HospitalAccess.Web && dotnet run --urls http://localhost:5100
```
No primeiro boot, se não houver nenhum `StaffUser`, a API cria um admin a partir de
`Seed:AdminUsername`/`Seed:AdminPassword` — troque a senha assim que possível (não há troca de
senha pela UI ainda; via banco/nova rota a implementar).

### Testes
```bash
dotnet test HospitalAccess.Tests/HospitalAccess.Tests.csproj
```

## O que foi verificado de ponta a ponta neste ambiente de desenvolvimento
- Build completo da solução (0 erros/warnings) e suíte de testes (3 passando, incluindo o
  golden vector do QR contra a referência real do fabricante — ver item 5).
- API rodando contra PostgreSQL real: login JWT, CRUD de controladores (com comandos de
  porta), usuários (com grupo organizacional, cartão Mifare opcional e foto), grupos de
  usuários e visitantes, geração de QR (PNG real, byte-mode), feriados e grade horária
  (com sincronização em massa para todos os controladores), exportação CSV do log de
  acessos e do log de alarmes.
- Front-end Blazor Server testado num Chromium real (Playwright): login → cadastro de
  controlador → comandos de porta (abrir/fechar/manter aberta/trancar/destrancar) → editar
  controlador → descoberta de controladores na rede → tela de detalhes do controlador (abas
  Rede/Relógio/Alarmes/Ajustes Locais/Auditoria/Fotos de Evento, todas navegáveis sem travar
  mesmo com o controlador inacessível) → cadastro de grupo de usuários → cadastro de usuário
  com foto/grupo/porta/cartão → editar/excluir usuário → visitante com QR exibido na tela →
  revogar visitante → feriado e grade horária cadastrados e sincronizados → log de acessos →
  log de alarmes → refresh de página degrada graciosamente para o login (não há crash).
- Corrigido durante o teste end-to-end: `POST/PUT/DELETE /api/users` travava a requisição
  por tempo indefinido quando um controlador associado estava inacessível (a sincronização
  era síncrona). Agora roda em segundo plano — ver item 7 acima.
- Corrigido durante o teste end-to-end (rodada das 8 áreas novas): a tela de detalhes do
  controlador (`ControllerDetail.razor`) ficava presa em "Carregando..." indefinidamente —
  não travada de verdade, mas sem re-renderizar — porque o Blazor só atualiza a UI depois que
  `OnInitializedAsync` termina por completo, e a segunda chamada (`SelectTabAsync`) podia
  levar dezenas de segundos contra um controlador inacessível. Corrigido com um
  `StateHasChanged()` explícito logo após o primeiro `await`, para mostrar cabeçalho/abas
  imediatamente enquanto os dados de cada aba carregam (ou falham) em segundo plano.
- Corrigido durante o mesmo teste: o Blazor `InputNumber<T>` **não suporta `byte`** como tipo
  genérico ("The type 'System.Byte' is not a supported numeric type") — derruba o circuito
  inteiro com exceção não tratada. Afetava os campos `Index`/`HolidayType` de `Holidays.razor`
  e `Tentativas` (config. de alarme) de `ControllerDetail.razor`; trocados para `int` no
  modelo de formulário, convertendo para `byte` só ao montar a requisição.
- Gateway: `dotnet build` compila contra as DLLs reais do SDK (todas as assinaturas
  usadas existem e batem com a engenharia reversa do IL/`monodis`); uma chamada de teste de
  conexão contra um IP inexistente confirmou que o timeout/retry funciona e a API não
  derruba — devolve 502 controlado. **Não foi possível testar contra um 8190H físico** (não
  há um na rede deste ambiente) — isso é o item mais importante a validar ao ligar o primeiro
  controlador real (ver caveat da seção 2 acima), especialmente a descoberta por broadcast
  UDP (item 9) que usa um padrão de comando diferente do resto do gateway.
- Interface modernizada (paleta indigo/slate, tipografia system-ui, cards com sombra sutil,
  tela de login em card centralizado, menu lateral escuro reorganizado em seções) e filtro de
  busca adicionado em todas as listas (Controladores, Usuários, Grupos, Visitantes, Feriados,
  Grade Horária) — client-side, já que essas listas são de porte modesto (dezenas de itens).
  Log de Acessos e Log de Alarmes ganharam os filtros adicionais que a API já suportava
  (usuário, controlador, método/tipo) mas a tela ainda não expunha.
- Corrigido nessa rodada: enums (`AccessMethod`, `AlarmKind`, `SyncState`) serializavam como
  número no JSON da API (padrão do `System.Text.Json`), mas os DTOs do front-end já assumiam
  representação em texto (`string Method`, `string Kind`) — um `JsonStringEnumConverter`
  global (`Program.cs`) resolve; validado inserindo linhas de teste direto no banco e
  conferindo que `/api/accesslog` e `/api/alarmevents` devolvem `"method": "Face"` /
  `"kind": "Fire"` (texto) e que as telas renderizam sem exceção.
- Corrigido: em produção (`ASPNETCORE_ENVIRONMENT=Production`, sem `UseExceptionHandler`), uma
  exceção não tratada em qualquer endpoint resultava em resposta 500 com corpo **vazio** — o
  front-end exibia uma caixa de erro vermelha sem nenhuma mensagem. Adicionado middleware
  global de tratamento de exceções (`Program.cs`) que sempre devolve uma mensagem legível
  (409 com mensagem específica para violação de índice único, 500 com o texto da exceção nos
  demais casos — aceitável aqui por ser uma ferramenta interna sempre atrás de autenticação).
  Validado rodando a API em modo Production e reproduzindo SN duplicado e falha de banco.
- Corrigido: **bug de dados pré-existente** encontrado durante a investigação acima — editar um
  usuário, um grupo (portas padrão) ou uma grade horária já existente para *adicionar* um item
  novo a uma coleção (`AccessPermission`, `GroupControllerDefault`, `TimeGroupSegment`) lançava
  `DbUpdateConcurrencyException` ("expected 1 row, actually affected 0") e gerava UPDATE em vez
  de INSERT. Causa: essas entidades têm PK GUID preenchida no inicializador (`Guid.NewGuid()`);
  ao adicionar via `colecaoJaRastreada.Add(new Entidade{...})` em vez de `_db.Set.Add(...)`, o
  EF Core interpreta a chave já preenchida como "entidade existente" e marca como `Modified`.
  Corrigido nos três pontos usando `_db.<DbSet>.Add(...)` com a FK setada explicitamente.
  Validado com curl (antes/depois) e Playwright.
- Implementado: grupo de usuários agora tem **portas padrão** (`GroupControllerDefault`,
  gerenciadas em `UserGroups.razor`). Ao escolher um grupo no cadastro/edição de usuário
  (`Users.razor`), as portas padrão do grupo são pré-marcadas automaticamente — o admin ainda
  pode adicionar ou remover portas individualmente antes de salvar (não é uma restrição
  imposta pelo servidor, só uma conveniência de preenchimento). Validado com Playwright.
- Migrada **toda a interface para React** (`HospitalAccess.Web.React/`), servida pela
  **própria `HospitalAccess.Api`** no mesmo processo/porta (`npm run build` gera os
  estáticos em `HospitalAccess.Api/wwwroot/`; ver `app.UseStaticFiles()` +
  `app.MapFallbackToFile()` em `Program.cs`). Todas as telas do Blazor têm equivalente:
  Controladores (+ detalhe com as 6 abas), Usuários (histórico/revogar/reativar/RBAC),
  Grupos (portas padrão), Visitantes (QR), Feriados, Grade Horária, Log de Acessos e Log
  de Alarmes (paginação/filtros/CSV). Diferença notável: a sessão (JWT em `localStorage`)
  sobrevive a um F5, ao contrário do Blazor Server. Validado com Playwright rodando contra
  a API na porta 5080 servindo os dois (API + estáticos): criação/edição/exclusão em cada
  tela, portas padrão de grupo pré-marcando no cadastro de usuário, revogar/reativar com
  histórico, geração de QR de visitante. `HospitalAccess.Web` (Blazor) continua no repo
  por enquanto — ver `HospitalAccess.Web.React/README.md` para detalhes e próximos passos.

## Limitações conhecidas / próximos passos
- **Exportação em PDF** do log de acessos: não implementada (só CSV). Toda biblioteca PDF
  popular para .NET tem alguma pegada de licença para uma organização do porte de um
  hospital (QuestPDF Community tem teto de receita, iText é AGPL/comercial); ficou como
  decisão em aberto — ver `AccessLogController.Export`.
- **Sessão do Blazor não sobrevive a um refresh de página** (F5): o JWT fica em memória
  por circuito Blazor Server; um refresh força um circuito novo e redireciona para o login
  (comportamento correto, sem crash — mas sem "lembrar sessão"). A versão em React
  (`HospitalAccess.Web.React/`) já resolve isso de graça (JWT em `localStorage`).
- **React tem paridade de telas com o Blazor, mas ainda não foi decidido remover o Blazor**:
  todas as telas foram migradas (ver item acima), mas `HospitalAccess.Web` continua no repo
  até essa direção ser validada em uso real. Nenhuma suíte de teste de front-end automatizada
  foi commitada para o React ainda (só validação manual + Playwright ad-hoc).
- **Troca de senha do StaffUser** e cadastro de novos operadores/recepcionistas: só via
  banco por enquanto; não há endpoint/tela dedicada.
- **Integração HIS/AD**: fora de escopo por pedido explícito — não implementada.
- **Modelo de validação do QR pelo hardware** (item 5 acima): o formato do QR em si já foi
  confirmado contra um QR real e funcional, mas ainda não sabemos se o controlador valida
  offline (confiando cegamente no texto do QR — inseguro, qualquer um forja um `user_id`) ou
  online (consultando um servidor). Também não confirmamos se `time` é só o instante de
  geração ou alguma janela de validade/replay. Só dá para esclarecer com o fabricante ou
  testando contra hardware real.
- **Descoberta por broadcast UDP** (item 9): implementada mas não validada contra hardware
  real — é um padrão de comando (multi-resposta) diferente do restante do gateway.
- **Ajustes locais "não perturbe", clima e indicador de limpeza**: sem classe correspondente
  no SDK disponível (item 9) — não implementados.
- **Cartões Mifare**: só o número do cartão é gravado (`Person.CardData`); a estrutura
  completa de setor (Apêndices 10-13) não é abstraída pelo SDK e exigiria um leitor/gravador
  de cartão dedicado (item 9).
- **Foto do evento**: a correlação foto↔registro é por ordem cronológica, não por um
  identificador explícito (o SDK grava os arquivos em disco com um nome não documentado no
  material de referência disponível) — ver item 9.
