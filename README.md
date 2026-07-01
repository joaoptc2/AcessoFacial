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

### 5. QR de acesso ≠ QR de consulta
Implementado apenas o **QR de abertura de porta (Appendix 8)**: 14 bytes (9 cartão + 4
validade em tempo comprimido, ano base 2018 + 1 CRC8), cifrados com RC4
(chave = ASCII de `1e30b3ec0f634874956e27627dfc2c46`). O "QR de consulta de resultado"
(`0x35`) **não foi implementado** — é outro recurso.

⚠️ **Pendente de validação com QR real do fabricante**: o polinômio/init do CRC8 e a ordem
de bits do tempo comprimido não estão totalmente especificados no `.doc` do protocolo (só
os campos e seus tamanhos em bits). A implementação atual usa a interpretação mais provável
(CRC-8 poly 0x07/init 0x00, empacotamento big-endian dos campos). Há um teste
"golden vector" preparado e marcado `Skip` em `QrAccessTokenServiceTests.cs` — assim que
houver um QR de referência gerado pelo fabricante (cartão + validade conhecidos → bytes
esperados), preencha o teste e remova o `Skip`. **Não gerar QRs de produção antes disso.**

### 6. Convenção do "cartão" do visitante (não é do protocolo, é nossa)
O `CardTransaction` do SDK (evento de autenticação em tempo real) não expõe um campo de
"número de cartão" próprio — só `UserCode`. Por isso `VisitorCardNumber` embute o `UserCode`
do visitante nos 4 bytes mais significativos do "cartão" de 9 bytes do QR (resto zerado),
para que o evento de acesso reportado em tempo real correlacione direto com `User.UserCode`
no nosso banco. Visitantes **não são cadastrados como `Person`** em nenhum controlador — o
QR é validado inteiramente offline pelo firmware (RC4 + CRC8 + expiração embutida).

### 7. Segurança física (fora do software)
A política **fail-safe vs fail-secure** das portas em queda de energia/rede (isto é: se a
fechadura trava ou libera quando falta energia ou a rede cai) é decisão de projeto físico
predial e **deve ser definida com a equipe de segurança/manutenção do hospital**, em
conjunto com o fabricante da fechadura/fecho eletromagnético de cada porta. O software não
controla nem pode controlar esse comportamento — ele só comanda abertura/fechamento em
operação normal. **Documente a decisão tomada para cada porta fisicamente**, fora deste
repositório (ex.: planilha de portas do projeto elétrico/predial).

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

- **HospitalAccess.Domain** — entidades e enums, sem dependência de framework.
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
- Build completo da solução (0 erros/warnings) e suíte de testes (10 passando, 1 golden
  vector `Skip` intencional).
- API rodando contra PostgreSQL real: login JWT, CRUD de controladores/portas/usuários/
  visitantes, geração de QR (PNG real, byte-mode), exportação CSV do log de acessos.
- Front-end Blazor Server testado num Chromium real (Playwright): login → cadastro de
  controlador/porta/usuário com foto/visitante → QR exibido na tela → log de acessos →
  refresh de página degrada graciosamente para o login (não há crash).
- Gateway: `dotnet build` compila contra as DLLs reais do SDK (todas as assinaturas
  usadas existem e batem com a engenharia reversa do IL); uma chamada de teste de conexão
  contra um IP inexistente confirmou que o timeout/retry funciona e a API não derruba —
  devolve 502 controlado. **Não foi possível testar contra um 8190H físico** (não há um na
  rede deste ambiente) — isso é o item mais importante a validar ao ligar o primeiro
  controlador real (ver caveat da seção 2 acima).

## Limitações conhecidas / próximos passos
- **Exportação em PDF** do log de acessos: não implementada (só CSV). Toda biblioteca PDF
  popular para .NET tem alguma pegada de licença para uma organização do porte de um
  hospital (QuestPDF Community tem teto de receita, iText é AGPL/comercial); ficou como
  decisão em aberto — ver `AccessLogController.Export`.
- **Sessão do front-end não sobrevive a um refresh de página** (F5): o JWT fica em memória
  por circuito Blazor Server; um refresh força um circuito novo e redireciona para o login
  (comportamento correto, sem crash — mas sem "lembrar sessão"). Persistir via
  `ProtectedSessionStorage` é a próxima melhoria natural se isso incomodar no dia a dia.
- **Troca de senha do StaffUser** e cadastro de novos operadores/recepcionistas: só via
  banco por enquanto; não há endpoint/tela dedicada.
- **Integração HIS/AD**: fora de escopo por pedido explícito — não implementada.
- **Golden vector do QR** (item 5 acima): pendente de um QR real do fabricante.
