# Instalação em servidor Linux (produção)

Passo a passo para colocar a `HospitalAccess.Api` rodando em um servidor Linux
on-premise como serviço systemd atrás de um reverse proxy Nginx. A **API serve também
o front-end React** (SPA em `wwwroot/`) no mesmo processo/porta — há um único serviço a
gerenciar. Os comandos abaixo assumem **Ubuntu/Debian**; nas notas de cada seção há o
equivalente para RHEL/Rocky/Alma quando ele muda.

> O front-end é um SPA React estático servido pela própria API — não há um segundo
> serviço nem necessidade de WebSocket/SignalR.

---

## 1. Pré-requisitos do servidor

- Ubuntu 22.04/24.04 (ou Debian 12) com acesso root/sudo.
- Rede: o servidor precisa alcançar por TCP/IP o IP:porta de cada um dos 30
  controladores 8190H (normalmente a mesma VLAN/rede predial de controle de acesso).
- Um domínio ou IP interno para o hospital acessar o front-end (ex.: `acesso.hospital.local`).

## 2. Instalar o .NET 8 (ASP.NET Core Runtime)

Em produção só é necessário o **runtime** (não o SDK completo) — mais leve e com
menos superfície de ataque:

```bash
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-8.0
dotnet --list-runtimes   # confirme Microsoft.AspNetCore.App 8.x e Microsoft.NETCore.App 8.x
```

> **RHEL/Rocky/Alma**: `sudo dnf install aspnetcore-runtime-8.0` (repositório da Microsoft
> precisa estar habilitado — veja https://learn.microsoft.com/dotnet/core/install/linux).

Se o pacote não existir no mirror da distribuição, use o script oficial da Microsoft:
```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
sudo bash dotnet-install.sh --channel 8.0 --runtime aspnetcore --install-dir /usr/share/dotnet
sudo ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
```

## 3. Instalar e preparar o PostgreSQL

```bash
sudo apt-get install -y postgresql
sudo systemctl enable --now postgresql

sudo -u postgres psql -c "CREATE ROLE app WITH LOGIN PASSWORD 'TROQUE_ESTA_SENHA';"
sudo -u postgres psql -c "CREATE DATABASE hospital_access OWNER app;"
```
Anote a senha — ela vai para o arquivo de ambiente da API na seção 6, nunca em código.

## 4. Publicar a aplicação (build de Release)

Em uma máquina de build (pode ser o próprio servidor, se tiver o **SDK** .NET e o
**Node 22** instalados, ou uma esteira de CI) com o código deste repositório:

```bash
git clone <url-do-repositorio> HospitalAccess
cd HospitalAccess

# 1) Build do front-end React → gera os estáticos em HospitalAccess.Api/wwwroot/
cd HospitalAccess.Web.React && npm ci && npm run build && cd ..

# 2) Publish da API (empacota também o wwwroot/ com o SPA já buildado)
dotnet publish HospitalAccess.Api/HospitalAccess.Api.csproj -c Release -o /tmp/publish/api
```
As DLLs do SDK do fabricante (`HospitalAccess.Gateway/lib/*.dll`) já estão versionadas
no repositório e são copiadas automaticamente para a pasta de publicação — não precisa
de nenhum passo extra para elas.

Copie o diretório publicado para o servidor (`scp`/`rsync`) em, por exemplo:
```bash
sudo mkdir -p /opt/hospitalaccess/api
sudo rsync -a /tmp/publish/api/  servidor:/opt/hospitalaccess/api/
```

## 5. Criar um usuário de sistema dedicado

Nunca rode os serviços como root:
```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin hospitalaccess
sudo chown -R hospitalaccess:hospitalaccess /opt/hospitalaccess
```

## 6. Configurar os segredos (fora do código, nunca versionados)

```bash
sudo mkdir -p /etc/hospitalaccess
sudo touch /etc/hospitalaccess/api.env
sudo chown root:hospitalaccess /etc/hospitalaccess/*.env
sudo chmod 640 /etc/hospitalaccess/*.env
```

`/etc/hospitalaccess/api.env`:
```
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5080
ConnectionStrings__Postgres=Host=localhost;Database=hospital_access;Username=app;Password=TROQUE_ESTA_SENHA
Jwt__Key=<gere com: openssl rand -base64 48>
Jwt__Issuer=HospitalAccess
Jwt__Audience=HospitalAccess
Seed__AdminUsername=admin
Seed__AdminPassword=<senha forte só para o primeiro login>
```

> `Jwt__Key` deve ter **pelo menos 32 bytes** — a API recusa subir com uma chave menor.
>
> ⚠️ **DataProtection (crítico):** as senhas dos controladores (comunicação e painel web) são
> criptografadas em repouso via DataProtection. O **chaveiro precisa ser persistido num diretório
> fixo** — o padrão é `/var/lib/hospitalaccess/dpkeys` (configurável por `DataProtection__KeysPath`).
> Como o usuário do serviço é `--no-create-home`, **não** dá para confiar no `$HOME`: sem o
> diretório persistido, todo reinício/atualização gera chaves novas e as senhas já cifradas ficam
> **indecifráveis** (o SDK passa a rejeitar com "Password Is Error" e é preciso reentrar as senhas).
> A unidade systemd abaixo cria e mantém esse diretório via `StateDirectory=hospitalaccess`.
> **Inclua `/var/lib/hospitalaccess` no backup.** Em cluster/múltiplas instâncias, aponte o
> `DataProtection__KeysPath` para um store compartilhado.

A API escuta só em `127.0.0.1:5080` (loopback) e serve tanto os endpoints quanto o SPA
React — o Nginx é o único ponto exposto à rede do hospital (seção 8).

## 7. Migrar o banco de dados

A partir da pasta publicada da API (ou de uma máquina com o SDK e o código-fonte):
```bash
# opção A: com o SDK completo e o código-fonte (gera o SQL a partir das Migrations)
dotnet tool install --global dotnet-ef
dotnet ef database update --project HospitalAccess.Infrastructure --startup-project HospitalAccess.Api \
  --connection "Host=localhost;Database=hospital_access;Username=app;Password=TROQUE_ESTA_SENHA"
```
Isso cria as tabelas (`Users`, `Controllers`, `Permissions`, `SyncStatuses`,
`AccessLogs`, `AlarmEvents`, `StaffUsers`, `SystemSettings` etc.) e índices. O primeiro
`StaffUser` (admin) é criado automaticamente no primeiro boot da API, a partir de
`Seed:AdminUsername`/`Seed:AdminPassword`.

> Em produção sem SDK/código-fonte no servidor, prefira aplicar um **script SQL idempotente**
> ou um **bundle de migração** gerados na máquina de build — ver a seção "Atualizando uma
> versão nova" mais abaixo.

## 8. Criar os serviços systemd

`/etc/systemd/system/hospitalaccess-api.service`:
```ini
[Unit]
Description=HospitalAccess API
After=network.target postgresql.service

[Service]
Type=simple
WorkingDirectory=/opt/hospitalaccess/api
ExecStart=/usr/bin/dotnet /opt/hospitalaccess/api/HospitalAccess.Api.dll
Restart=on-failure
RestartSec=5
EnvironmentFile=/etc/hospitalaccess/api.env
User=hospitalaccess
Group=hospitalaccess
# Cria/mantém /var/lib/hospitalaccess (dono = usuário do serviço) para o chaveiro da DataProtection
# persistir entre reinícios/atualizações. Sem isto, as senhas cifradas dos controladores quebram.
StateDirectory=hospitalaccess
SyslogIdentifier=hospitalaccess-api

[Install]
WantedBy=multi-user.target
```

Há **um único serviço** — a API já serve o front-end React. Ativar e subir:
```bash
sudo systemctl daemon-reload
sudo systemctl enable --now hospitalaccess-api
sudo systemctl status hospitalaccess-api --no-pager
```

Ver logs em tempo real (`journalctl` — não há arquivo de log separado, o systemd
já centraliza tudo via `SyslogIdentifier`):
```bash
journalctl -u hospitalaccess-api -f
```

Teste local antes de mexer no Nginx:
```bash
curl -s -X POST http://127.0.0.1:5080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<a senha do Seed:AdminPassword>"}'
# deve devolver {"token":"...","role":"Admin"}
```

## 9. Nginx como reverse proxy

A API (que serve tanto os endpoints quanto o SPA React) fica só em `127.0.0.1:5080`; o
Nginx é o único ponto exposto à rede do hospital e encaminha tudo para ela.

```bash
sudo apt-get install -y nginx
```

`/etc/nginx/sites-available/hospitalaccess`:
```nginx
server {
    listen 80;
    server_name acesso.hospital.local;

    # O cadastro de usuário envia a foto de face (JPEG bruto do celular pode ter vários MB;
    # a API reduz para <=120 KB no servidor). O padrão do Nginx é só 1 MB — sem esta linha,
    # o upload é rejeitado com "413 Request Entity Too Large" antes de chegar na API.
    # A API aceita até 10 MB ([RequestSizeLimit] no UsersController); 12m deixa uma folga.
    client_max_body_size 12m;

    location / {
        proxy_pass         http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 100s;
    }
}
```

```bash
sudo rm -f /etc/nginx/sites-enabled/default
sudo ln -s /etc/nginx/sites-available/hospitalaccess /etc/nginx/sites-enabled/hospitalaccess
sudo nginx -t
sudo systemctl enable --now nginx
sudo systemctl reload nginx
```

### HTTPS (recomendado sempre, mesmo dentro da rede do hospital)
Com domínio interno resolvendo e porta 443 liberada, o jeito mais simples é o
Certbot (Let's Encrypt, se o servidor tiver saída para a internet) ou, mais comum em
rede hospitalar fechada, um certificado emitido pela CA interna do hospital instalado
manualmente com um bloco `listen 443 ssl;` + `ssl_certificate`/`ssl_certificate_key`
apontando para os arquivos da CA interna. Fale com o time de TI/segurança do hospital
sobre qual CA usar antes de gerar os certificados.

## 10. Firewall

Libere só o necessário:
```bash
sudo ufw allow 80/tcp     # ou 443/tcp se já configurou HTTPS
sudo ufw allow from <faixa-de-IP-da-VLAN-de-acesso> to any port 8101 proto tcp  # ida até os controladores, se houver firewall entre eles
sudo ufw enable
```
A porta 5080 (API) deve ficar **fechada** para fora do próprio servidor — só o Nginx
(porta 80/443) deve ser alcançável pela rede do hospital. Não abra a porta da API para
a LAN geral.

## 11. Cadastrar os controladores

Com tudo no ar, acesse `http://acesso.hospital.local/`, faça login com o admin do
seed, e cadastre os 30 controladores em **Controladores** (IP, porta, SN de 16
dígitos, senha de comunicação, modo de conexão). Cada controlador **é** a porta. Use o
botão **"Testar conexão"** para confirmar que o servidor alcança o dispositivo por TCP
antes de cadastrar usuários nele — isso chama `ReadSN` de verdade no controlador.

⚠️ Ao ligar o **primeiro** controlador real, valide os caveats do `README.md`: o modo
de conexão (TCP client vs phone-home), a chegada do push em tempo real (`BeginWatch`/
`TransactionMessage`) e o cadastro de visitante com validade nativa — os itens mais
importantes a confirmar com hardware físico.

## 12. Backups

```bash
# cron diário, por exemplo em /etc/cron.d/hospitalaccess-backup
0 3 * * * postgres pg_dump hospital_access | gzip > /var/backups/hospital_access-$(date +\%F).sql.gz
```
Guarde os backups fora do próprio servidor (a política de retenção fica a critério
do hospital — é um sistema de controle de acesso físico, dado sensível).

## 13. Atualizando uma versão nova

O servidor de produção só tem os binários publicados, não o código-fonte nem o `dotnet-ef`,
então **não** rode `dotnet ef database update` lá. Gere o artefato de migração na máquina de
build e aplique-o no servidor. Duas opções:

**a) Script SQL idempotente** (aplicável com `psql`, sem .NET no servidor):
```bash
# Na máquina de build (com o SDK e o dotnet-ef):
dotnet ef migrations script --idempotent \
  --project HospitalAccess.Infrastructure --startup-project HospitalAccess.Api \
  -o migracao.sql
# No servidor, dentro de uma janela de manutenção e após backup:
psql "$CONNECTION_STRING" -f migracao.sql
```

**b) Bundle de migração** (executável autônomo, não precisa do SDK no servidor):
```bash
# Na máquina de build:
dotnet ef migrations bundle --self-contained -r linux-x64 \
  --project HospitalAccess.Infrastructure --startup-project HospitalAccess.Api \
  -o efbundle
# No servidor:
./efbundle --connection "$CONNECTION_STRING"
```

Fluxo completo da atualização:
```bash
sudo systemctl stop hospitalaccess-api
# rebuild do React (seção 4), publique de novo e sincronize para /opt/hospitalaccess/api
# aplique a migração pelo método (a) ou (b) acima, se houver migration nova
sudo systemctl start hospitalaccess-api
```

## Checklist rápido de verificação pós-instalação
- [ ] `systemctl status hospitalaccess-api` — `active (running)`.
- [ ] `curl -s http://127.0.0.1:5080/api/auth/login -X POST -H "Content-Type: application/json" -d '{"username":"admin","password":"..."}'` devolve um token.
- [ ] `curl -I http://localhost/` através do Nginx devolve `200` (o SPA React é servido pela API).
- [ ] Login pela UI funciona e a navegação entre páginas funciona.
- [ ] "Testar conexão" em um controlador cadastrado com IP real devolve o SN do
      dispositivo.
- [ ] Trocar a senha do admin do seed (via banco, por ora — não há tela para isso ainda).
