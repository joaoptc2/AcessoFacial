# Sistema de Controle de Acesso Hospitalar — 8190H (A33_Face)

Solução para gerenciar **30 controladores faciais 8190H** conectados por **TCP/IP**,
com cadastro de face por upload, QR Code de acesso para visitantes (com gestão de
leitos/quartos), gestão de portas/permissões, sincronização multi-dispositivo e log
de acessos auditável.
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
caber no limite). Qualquer formato que o ImageSharp decodifique é aceito na entrada (JPG,
PNG, WebP...) — o conversor sempre re-encoda para **JPEG**, que é o que o aparelho exige.
Retornos tratados: `2` (feature code não identificável),
`3` (sem rosto), `4` (duplicada), `0`/outros (falha de CRC32), resultado nulo (handle 0 =
usuário inexistente).

### 4. `WaitRepeatMessage`
Configurável por controlador via `Controller.SupportsWaitRepeatMessage` (só firmware ≥ v4.28).

### 5. QR de acesso: **lido do controlador**, não gerado no servidor — resolvido em 2026-07 (validado em hardware real)
A geração do QR no servidor foi **abandonada**. Descobriu-se, com QRs reais enviados pelo
cliente, que o firmware do FC-8190H cunha o próprio QR — **texto simples**
`user_id={UserCode}_time={microssegundos desde a época Unix}` em Base64, ex.:

```
user_id=1_time=1782921297138761
```

— e valida o QR apresentado na câmera **contra esse texto exato que ele mesmo guardou**. O
campo `time` é definido pelo aparelho no instante do cadastro, em microssegundos, e é
**irreproduzível pelo servidor**: qualquer QR que gerássemos (mesmo com o formato certo) caía
como "QR inválido", porque o `time` não batia com o guardado no device.

A solução é **ler o QR real direto do controlador** pela API HTTP do painel web (ver
[Integração HTTP com o painel web](#integração-http-com-o-painel-web-do-controlador-qr-real--provisionamento--eventos)):
`POST /api/People/GetDetail` devolve o campo `QRCode` já cunhado; se a pessoa ainda não existe
no aparelho, o sistema a provisiona via `POST /api/People/New` (o que faz o firmware cunhar o
QR) e relê. Tudo isso fica em `DeviceQrService` (Api), consumido por
`POST /api/visitors/{id}/qrcode`.

✅ **Validado contra hardware real**: com a URL e a senha do painel configuradas no controlador,
o QR baixado abre a porta normalmente (confirmação do cliente).

O download do QR do visitante (`POST /api/visitors/{id}/qrcode`) passa **exclusivamente** pelo
`DeviceQrService` (lê do aparelho) — não usa mais a geração de texto. O código antigo de geração
(`QrAccessTokenService`, `Rc4.cs`, `VisitorCardNumber.cs` em `HospitalAccess.Application/Qr/`)
**continua no repositório** (ainda referenciado/registrado, com seus testes), mas ficou **fora do
caminho de download do QR do visitante**. Há ainda um endpoint de **fallback manual** para colar
um QR já existente do painel e só re-renderizar o PNG (`POST /api/visitors/qrcode/render`).
`Visitor.ValidUntil` continua sendo a validade que o NOSSO sistema controla
(`IVisitorExpirationJob` revoga automaticamente) e é enviada ao aparelho como `ExpirationDate`
(unix seconds, clampada à faixa aceita pelo firmware) no provisionamento.

### 6. Controlador = porta (não existe entidade `Door` separada)
O hardware 8190H tem **um único relé/porta por controlador** — não há como um controlador
comandar mais de uma porta. Por isso não existe uma entidade `Door`: `Controller` já
representa fisicamente a porta (o antigo campo `RelayIndex`, sempre `0` e ignorado pelo
gateway, foi removido). `AccessPermission` liga
`User`→`Controller` diretamente. Os comandos de porta expostos pelo SDK — `OpenDoor`,
`CloseDoor`, `HoldDoor` (manter aberta), `LockDoor` (trancar mesmo para credencial válida)
e `UnlockDoor` (reverter o trancamento) — ficam em `IDeviceGateway` e são expostos por
controlador em `POST /api/controllers/{id}/{open|close|hold-open|lock|unlock}`.

### 7. Sincronização em segundo plano: fila **paralela entre controladores, serial em cada um**
Criar/editar/excluir um usuário grava no banco e **enfileira** a sincronização com os
controladores **sem aguardar o resultado na requisição HTTP** (o comando ao hardware é TCP
com retries e pode levar minutos se um controlador estiver inacessível — travaria a tela se
fosse síncrono). O modelo de concorrência é a parte mais importante a entender antes de mexer
no gateway/sync:

- **Lock por controlador no gateway** (`DoNetDriveGateway.RunAsync`, um `SemaphoreSlim` por
  `Controller.Id`): **nunca dois comandos no MESMO aparelho ao mesmo tempo**. Sem isso, comandos
  concorrentes no mesmo controlador estouravam com `CommandStatus_Timeout` (ex.: um
  `AddPersonAndImage` de face colidindo com o heartbeat do health-check). Aparelhos DIFERENTES
  rodam em paralelo (locks distintos). Isso vale para TODO comando (sync, porta, health, leitura).
- **Fila de sincronização** (`UserSyncQueue` + `UserSyncQueueWorker`, ~8 workers paralelos):
  substituiu o antigo `Task.Run` fire-and-forget, que gerava N `AddPersonAndImage` concorrentes
  num mesmo controlador durante um lote de cadastros. Agora vários usuários sincronizam em
  paralelo **em controladores diferentes**; um controlador lento bloqueia só o próprio worker,
  não os demais. Coordenação por usuário: no máximo 1 processamento por usuário ao mesmo tempo
  (dedup), com flag de **rerun** para não perder uma edição feita durante o processamento nem
  empilhar duplicatas do job de retry.
- **Progresso e retry**: fica em `DeviceSyncStatus` (Pending/Synced/Failed +
  `RetryCount`/`NextRetryAtUtc`). O `SyncRetryBackgroundService` (varredura `Sync:ScanIntervalSeconds`,
  default 2 min) **reenfileira só os elegíveis** (`SyncRetryPolicy`): `Pending` sempre; `Failed`
  quando o **backoff exponencial** venceu (2→4→8→16→32→60 min, teto 1 h — configurável). Falha
  **permanente** (foto sem rosto, feature ilegível, duplicidade) entra em **quarentena**
  (`NextRetryAtUtc = null`): nunca re-tenta sozinha — destrave com `POST /api/users/sync-failed`
  (botão "Sincronizar com erro"), `POST /api/users/{id}/resync`, reativação ou upload de foto
  nova (conflito de duplicidade tem fluxo próprio substituir/manter). Sem isso, todo usuário
  falho re-enviava a face completa (~120 KB) a cada 2 min, para sempre — a principal fonte de
  sobrecarga de rede nos controladores.
- **Revogação ao editar/remover porta**: ao remover uma permissão, o `DeviceSyncStatus` daquela
  porta permanece `Synced` de propósito — assim o `SyncUserAsync` o **revoga** no hardware (a
  revogação só parte de um status `Synced`). Exclusão de usuário: a linha do `User` (e o
  `DeviceSyncStatus` em cascata) é apagada na hora, então o `UserCode` + controladores são
  capturados *antes* do delete e a revogação é **enfileirada** (`EnqueueRevokeDeleted`).
- **Health-check em paralelo** (`DeviceHealthBackgroundService`): a verificação de "online"
  também é paralela entre controladores. Era em série, então um aparelho lento (~30 s de timeout)
  atrasava a checagem dos seguintes e o `LastSeenUtc` deles envelhecia além do limiar de 3 min —
  fazendo controladores **saudáveis aparecerem "offline"** (test-connection passava, mas o painel
  mostrava offline). Ordem da sonda, da mais barata à mais cara: **push recente** (aparelho
  falou conosco → online sem sonda alguma) → ICMP ping → TCP na porta do painel HTTP
  (`ApiBaseUrl`) → TCP na porta do SDK só como último recurso (`HealthCheck:AllowSdkPortFallback`).
  Regra geral: **paralelizar entre controladores, serializar dentro de cada um.**

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
  `POST /api/timegroups/sync-all` — em segundo plano, sem bloquear a resposta, como no item 7).
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
  tempo real. Eventos de alarme chegam pelo mesmo `TransactionMessage` do SDK como **log de
  sistema** (`SystemTransaction`, envelope com `CmdIndex == 3` — protocolo Classe IX §9.4;
  códigos 14–20 disparam e 21–27 limpam), classificados por `TransactionCodeClassifier.TryMapSystemAlarm`
  em `DoNetDriveGateway.OnTransactionMessage`, gravados em `AlarmEvent` (`AlarmEventRecorder`,
  mesmo padrão do `AccessEventRecorder`) e consultáveis em `/api/alarmevents` (com CSV).
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

⚠️ Estas áreas foram implementadas contra as DLLs reais do SDK (compilação verificada,
nomes de classe/propriedade confirmados via `monodis` quando o código de exemplo não deixava
claro), mas **nenhuma delas foi testada contra um 8190H físico** — mesma limitação já
registrada nas seções 1-2 para o restante do gateway.

---

## Integração HTTP com o painel web do controlador (QR real + provisionamento + eventos)

Além do protocolo binário DoNetDrive por TCP (o Gateway/SDK, seções 1-9), o sistema fala com o
controlador por um **segundo canal: a API REST HTTP do painel web** do próprio FC-8190H. Esse
canal foi introduzido para resolver o QR (seção 5) e reaproveitado para provisionar visitantes
e receber eventos. Todo esse código vive em **`HospitalAccess.Infrastructure/Devices/`**
(`DeviceHttpClient`, `DeviceHttpClientFactory`, `DeviceHttpOptions`, `DeviceErrorCodes`,
`DeviceHttpException`) e é consumido pela camada Api (`DeviceQrService`).

- **Login** — `POST /api/User/Login` com corpo `{"password": MD5(Hash+senha+Hash) em maiúsculas,
  "rememberMe":"true", "Hash": <GUID gerado pelo cliente>}` (não há campo de usuário). O painel
  responde **HTTP 200 mesmo em erro** — o sucesso é lido pelo envelope `{result, content, errCode,
  error}` (`result:false` = falha); o token vem em `content.token`. O token é cacheado em
  `Controller.ApiToken` e revalidado (`CheckToken`); em 401 o cliente refaz o login
  automaticamente.
- **Ler / provisionar pessoa** — `POST /api/People/GetDetail` (lê `QRCode`, `ExpirationDate` etc.),
  `POST /api/People/New` (cadastro/edição — multipart) e `POST /api/People/Delete`. O multipart é
  **byte-exato**: a parte `PeopleJson` vai **sem `Content-Type`** (o firmware derruba a conexão se
  tiver), a foto vai como `image/jpg` (não `image/jpeg`), boundary com prefixo `----facial`.
- **Senha do painel** — global por padrão (`Device:DefaultApiPassword`), com **override por
  controlador** (`Controller.ApiPassword`). A senha do controlador é **criptografada em repouso**
  (mesma `ValueConverter` de DataProtection usada em `CommunicationPassword`), nunca é devolvida
  pelo GET (a API expõe só `HasApiPassword`) e nunca é versionada. `Controller.ApiBaseUrl` vazio =
  controlador só-SDK (sem QR HTTP).
- **TLS** — o painel usa HTTPS com certificado autoassinado. O `DeviceHttpClientFactory` usa um
  `SocketsHttpHandler` compartilhado com bypass de validação de certificado **restrito a esse
  cliente** (o bypass **não** é global — ver a política de segurança do resto do sistema).
- **Eventos (phone-home)** — endpoint `POST /note/insertNoteFace` (`DeviceCallbackController`,
  `[AllowAnonymous]`): o controlador, quando configurado em modo "phone home", empurra os
  reconhecimentos como multipart com `recordJson` **comprimido em gzip** (detectado pelo magic
  `1F 8B`). O endpoint descomprime, faz o parse tolerando **aliases de campo** (`deviceKey`/
  `deviceId`, `employeeId`/`employeeNoString`, etc.), casa o controlador por `SerialNumber` e grava
  um `AccessLog`. É um **canal alternativo** ao `TransactionMessage` do SDK (seção 2) — útil se o
  push por TCP não funcionar no hardware real. Responde `{"success":0,"msg":"OK"}` (0 = OK).
- **Mapa de erros** — `DeviceErrorCodes` traduz os `errCode` do painel (ex.: `11` =
  `ExpirationDate` fora da faixa) para mensagens legíveis; as falhas viram `DeviceHttpException`
  e o endpoint devolve 502/422 com a causa.

## Modo de desenvolvimento (tela "Logs (Dev)", só Admin)

Ferramenta de diagnóstico em produção sem acesso ao servidor: com o **modo de desenvolvimento**
ativo (toggle persistido em `SystemSettings`, sobrevive a restart), os logs importantes do
processo — Information+ das categorias `HospitalAccess.*` (sincronização, monitoramento,
health-check, comandos aos controladores) e Warning+ do framework — são espelhados num **ring
buffer em memória** (últimas 2000 linhas, `DevLogBuffer`) e exibidos na tela **Logs (Dev)** com
filtro por nível/texto, botão de ativar/desativar e botão de limpar. API: `GET /api/devlogs`
(busca incremental por id), `POST /api/devlogs/mode`, `DELETE /api/devlogs`. Desligado o custo é
zero; nada vai para disco (o log completo do serviço continua no journal/console) e o conteúdo se
perde no restart — é diagnóstico, não auditoria.

## Gestão de leitos (visitantes temporários)

O visitante temporário representa um **acompanhante/paciente hospedado num quarto**. Como o QR é
cunhado **por aparelho** (seção 5), a regra de negócio é: **cada visitante fica em exatamente uma
porta/quarto**.

- **Cadastro** (`POST /api/visitors`) exige **exatamente 1 controlador** (o quarto). A tela
  (`VisitorsPage.tsx`) troca a lista de checkboxes por um único `<select>` de quarto.
- **Trocar de quarto** (`PUT /api/visitors/{id}/room`): substitui a permissão pelo novo quarto,
  **remove o cadastro do controlador antigo via HTTP `People/Delete`** (o que **invalida o QR
  antigo**) e dispara a sincronização para o novo — o próximo download de QR já vem cunhado pelo
  novo aparelho. Roda em segundo plano, sem bloquear a resposta (como no item 7).
- **Revogar vs. excluir** — `DELETE /api/visitors/{id}` revoga (mantém histórico);
  `DELETE /api/visitors/{id}/permanent` apaga de vez. A expiração automática
  (`IVisitorExpirationJob`) continua revogando ao passar de `ValidUntil`.

> Um comparativo detalhado com um produto comercial de mercado (ZKTeco ZKBio CVAccess 4.0) e o
> backlog de melhorias priorizado estão em
> [`docs/comparativo-zkbio-melhorias.md`](docs/comparativo-zkbio-melhorias.md).

---

## Arquitetura

```
┌──────────────────────────────────────┐
│  HospitalAccess.Api                    │  ← serve também o front-end React
│  (+ Application/Domain/Infra)          │     (SPA em wwwroot/, mesmo processo/porta)
└───┬───────────────────────────────┬──┘
    │ IUserSyncService/IDeviceGateway│ DeviceHttpClient (Infrastructure/Devices)
    │                                │
┌───▼────────────────────┐   binário │ REST HTTP (painel web): QR, cadastro,
│ HospitalAccess.Gateway  │ ◀────────┼────────────────────────▶  30x 8190H
│ (SDK DoNetDrive, TCP/IP)│  DoNetDrive│  + phone-home /note/insertNoteFace ▲
└────────────────────────┘   eventos  └──────────────────────────────────┘
```

Dois canais falam com o mesmo aparelho: o **Gateway/SDK** (protocolo binário DoNetDrive por
TCP, seções 1-9) e o **cliente HTTP** do painel web (`Infrastructure/Devices/`, seção
"Integração HTTP") usado para o QR real, provisionamento de visitante e o callback phone-home.

- **HospitalAccess.Domain** — entidades e enums, sem dependência de framework
  (`Controller` = porta física, com os campos da API HTTP `ApiBaseUrl`/`ApiPassword`/`ApiToken`;
  `UserGroup` para organização de usuários).
- **HospitalAccess.Application** — QR (protocolo puro), contratos de sincronização.
- **HospitalAccess.Infrastructure** — EF Core/PostgreSQL, encoder de QR (QRCoder),
  **cliente HTTP do painel web** (`Devices/`: `DeviceHttpClient` etc.) e criptografia de
  segredos em repouso (DataProtection: `CommunicationPassword`, `ApiPassword`).
- **HospitalAccess.Gateway** — único componente que fala o **protocolo binário** do hardware
  (SDK `DoNetDrive.*`).
- **HospitalAccess.Api** — API REST + JWT + hosted services (sync/expiração de usuários,
  monitoramento em tempo real, coleta offline, health-check, retenção de dados), o
  `DeviceQrService` (QR via HTTP) e o `DeviceCallbackController` (phone-home). Serve
  também o SPA React em `wwwroot/`.
- **HospitalAccess.Web.React** — front-end React (Vite/TS), único front-end. O build gera
  os estáticos em `HospitalAccess.Api/wwwroot/`.
- **HospitalAccess.Tests** — testes unitários (QR, classificador de eventos, conversor de imagem).

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
  (0.7.0). **Já estão versionadas em `HospitalAccess.Gateway/lib/`** (referenciadas via
  `<Reference HintPath>` no `.csproj`, pois não há pacote NuGet do fabricante) — não é preciso
  obtê-las de novo para compilar. Ao atualizar a versão do SDK, substitua os arquivos em `lib/`
  e registre as novas versões aqui.

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
`appsettings.json` versionado. Veja `appsettings.example.json` para as chaves esperadas,
incluindo a seção **`Device`** (`DefaultApiPassword` — senha global do painel web usada quando
o controlador não tem override —, `LoginPath`, `TimeoutMs`, `TimeZone`).
Senhas por controlador — de comunicação (`Controller.CommunicationPassword`, SDK) e do painel
web (`Controller.ApiPassword`, HTTP) — ficam no banco **criptografadas em repouso** via
`ValueConverter` de ASP.NET DataProtection; nunca são devolvidas pelas APIs de leitura nem
versionadas.

> ⚠️ **O chaveiro da DataProtection PRECISA ser persistido** num diretório fixo, gravável pelo
> usuário do serviço e **incluído no backup** — padrão `/var/lib/hospitalaccess/dpkeys`,
> configurável por `DataProtection__KeysPath` (ver `Program.cs` e o doc de instalação). Sem isso,
> um restart/redeploy gera chaves novas e as senhas já cifradas ficam **indecifráveis**: o SDK
> passa a rejeitar com `Password Is Error` e é preciso reentrar as senhas. Se o diretório não for
> gravável, **salvar uma senha falha com 500** — o boot loga um aviso `[DataProtection] ATENÇÃO...`.

### Banco de dados
```bash
dotnet tool install --global dotnet-ef
dotnet ef database update --project HospitalAccess.Infrastructure --startup-project HospitalAccess.Api
```

### Rodando
```bash
# Build do front-end React → gera os estáticos em HospitalAccess.Api/wwwroot/
cd HospitalAccess.Web.React && npm ci && npm run build && cd ..

# API (serve a API + o SPA React no mesmo host/porta)
cd HospitalAccess.Api && ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://localhost:5080
```
Em desenvolvimento do front-end, você também pode rodar o Vite com hot-reload
(`cd HospitalAccess.Web.React && npm run dev`) — ele faz proxy de `/api` para a porta 5080
(ver `vite.config.ts`).
No primeiro boot, se não houver nenhum `StaffUser`, a API cria um admin a partir de
`Seed:AdminUsername`/`Seed:AdminPassword` — troque a senha assim que possível (não há troca de
senha pela UI ainda; via banco/nova rota a implementar).

### Testes
```bash
dotnet test HospitalAccess.Tests/HospitalAccess.Tests.csproj
```

## O que foi verificado de ponta a ponta neste ambiente de desenvolvimento

> Registro **cronológico** das rodadas de verificação. Os itens que citam telas Blazor
> (`*.razor`, `ApiClient.cs`) são **históricos**: descrevem o front-end antigo, aposentado e
> removido do repositório na migração para React (último item da lista).

- Build completo da solução (0 erros/warnings) e suíte de testes (xunit; hoje 10 arquivos)
  cobrindo QR, classificador de eventos, conversor de imagem, fila de sincronização
  (dedup/rerun/troca de quarto), política de retry/backoff (`SyncRetryPolicyTests`), diff de
  campos de dispositivo (`UserDeviceFieldsTests`), guarda de reentrância (`SingleFlightTests`),
  acesso por grupo, número de cartão de visitante e o **cliente HTTP do painel**
  (`DeviceHttpClientTests`: o hash de login MD5 contra o vetor do sistema de referência, o
  multipart byte-exato — `PeopleJson` sem `Content-Type` e foto `image/jpg` — e a extração do
  `QRCode`).
- **QR do visitante lido do controlador — validado contra hardware real** (seção 5): com a URL e a
  senha do painel web configuradas, o QR baixado abre a porta (confirmação do cliente). A
  integração HTTP em si (login → `content.token` → `GetDetail` vazio → provisiona via multipart
  `People/New` → relê `QRCode` → decodifica `user_id=…_time=…`) também foi exercitada de ponta a
  ponta contra um **servidor FC-8190H simulado**, confirmando que o multipart byte-exato é aceito.
- API rodando contra PostgreSQL real: login JWT, CRUD de controladores (com comandos de
  porta), usuários (com grupo organizacional, cartão Mifare opcional e foto), grupos de
  usuários e visitantes, geração de QR (PNG real, byte-mode), feriados e grade horária
  (com sincronização em massa para todos os controladores), exportação CSV do log de
  acessos e do log de alarmes.
- (Histórico — front-end Blazor, hoje aposentado) Front-end Blazor Server testado num Chromium
  real (Playwright): login → cadastro de
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
  de Alarmes (paginação/filtros/CSV), além do painel de status, emergência e configurações.
  A sessão (JWT em `localStorage`) sobrevive a um F5. **O front-end Blazor (`HospitalAccess.Web`)
  foi aposentado e removido do repositório** — o React é o único front-end.

## Troubleshooting: `CommandStatus_Timeout` com o aparelho "online"

O ping/painel responder **não** garante que o protocolo binário responda. O 8190H **descarta em
silêncio** qualquer quadro cujo SN ou senha de comunicação não batam com os dele (não devolve
erro — vira timeout do nosso lado). Cheque nesta ordem:

1. **IP certo?** Aparelho em DHCP pode ter trocado de IP (fixe IP/reserva no roteador). O ping
   "online" pode estar vindo de OUTRA máquina que herdou o IP antigo.
2. **Porta** do cadastro = porta do protocolo configurada no aparelho (tela de rede dele).
3. **SN (16 dígitos) e senha de comunicação** exatamente iguais aos do aparelho — errados =
   timeout silencioso, não mensagem de erro.
4. **"Testar conexão"** na tela do controlador faz um `ReadSN` real (e ignora o disjuntor).
5. **Aparelho degradado** (lento a ponto de nem o painel web local abrir): reboot/firmware — é
   problema do dispositivo. Enquanto isso, o **disjuntor por controlador** abre após 3 falhas
   consecutivas (cooldown exponencial 1→15 min; badge "protocolo em espera" no painel) para não
   martelar o aparelho doente; um "Testar conexão" bem-sucedido fecha o circuito na hora. Para
   aparelhos cronicamente lentos, suba o `TimeoutMs` do cadastro (o upload de face já usa piso
   de 15 s).
6. **Divergência de cadastro** (auditoria acusa usuários faltando apesar de "Synced"): use o
   botão **"Reparar divergências"** na aba Auditoria — os faltantes voltam a Pending e são
   re-enviados pela fila.
7. **Aviso "Health-check caiu no TCP connect da porta do SDK"**: a sonda de presença esgotou os
   caminhos neutros (ping ICMP falhou e não há painel HTTP para testar) e está usando a porta
   do protocolo como último recurso — funciona, mas abre/derruba uma conexão no canal do
   protocolo a cada ciclo. Saída: liberar ICMP até o aparelho **ou** preencher o `ApiBaseUrl`
   dele (ex.: `http://192.168.19.197`); depois, desligue `HealthCheck:AllowSdkPortFallback`
   no appsettings. O aviso sai uma vez por controlador a cada subida do serviço.

## Limitações conhecidas / próximos passos
- **Exportação em PDF** do log de acessos: não implementada (só CSV). Toda biblioteca PDF
  popular para .NET tem alguma pegada de licença para uma organização do porte de um
  hospital (QuestPDF Community tem teto de receita, iText é AGPL/comercial); ficou como
  decisão em aberto — ver `AccessLogController.Export`.
- **Front-end único (React)**: o Blazor foi aposentado. Ainda não há suíte de teste de
  front-end automatizada para o React (o CI faz build + typecheck; a validação de fluxo é
  manual). Os arquivos de referência do fabricante ficam em `vendor/` (fora do build).
- **Troca de senha do StaffUser** e cadastro de novos operadores/recepcionistas: só via
  banco por enquanto; não há endpoint/tela dedicada.
- **Integração HIS/AD**: fora de escopo por pedido explícito — não implementada.
- **Modelo de validação do QR pelo hardware** (item 5): **resolvido na prática** — o QR é
  cunhado e validado pelo próprio controlador (nós só lemos/provisionamos via HTTP) e abre a porta
  em hardware real. Como o `time` (microssegundos) é definido pelo aparelho, um `user_id` não é
  forjável sem esse valor exato. Fica em aberto apenas o detalhe de qual janela de validade/replay
  o firmware aplica ao `time` — irrelevante para o fluxo atual, já que a validade operacional é
  controlada pelo nosso `ValidUntil` (enviado como `ExpirationDate`).
- **Dependência da API HTTP do painel** para o QR: só funciona em controladores com
  `ApiBaseUrl` + senha do painel configuradas. Sem isso, o download do QR responde 422 e a
  recepção cai no fallback manual (`qrcode/render`). O phone-home (`/note/insertNoteFace`) exige
  configurar o controlador em modo "phone home" apontando para a URL da API — ainda não validado
  contra hardware físico.
- **Melhorias de produto** mapeadas contra o ZKBio CVAccess 4.0 em
  [`docs/comparativo-zkbio-melhorias.md`](docs/comparativo-zkbio-melhorias.md) — próxima onda
  sugerida: Áreas/zonas → Níveis de Acesso reutilizáveis → relatórios de auditoria de permissão.
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
