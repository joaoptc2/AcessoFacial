# Instalação em servidor Linux (produção)

Passo a passo testado neste projeto para colocar a `HospitalAccess.Api` e a
`HospitalAccess.Web` rodando em um servidor Linux on-premise, como serviços systemd
atrás de um reverse proxy Nginx. Os comandos abaixo assumem **Ubuntu/Debian**; nas
notas de cada seção há o equivalente para RHEL/Rocky/Alma quando ele muda.

> Este roteiro foi executado passo a passo (publish, usuário dedicado, systemd,
> Nginx com WebSocket, teste de ponta a ponta com login e navegação) para confirmar
> que cada etapa realmente funciona antes de documentá-la.

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

Em uma máquina de build (pode ser o próprio servidor, se tiver o **SDK** completo
instalado, ou uma esteira de CI) com o código deste repositório:

```bash
git clone <url-do-repositorio> HospitalAccess
cd HospitalAccess

dotnet publish HospitalAccess.Api/HospitalAccess.Api.csproj -c Release -o /tmp/publish/api
dotnet publish HospitalAccess.Web/HospitalAccess.Web.csproj -c Release -o /tmp/publish/web
```
As DLLs do SDK do fabricante (`HospitalAccess.Gateway/lib/*.dll`) já estão versionadas
no repositório e são copiadas automaticamente para a pasta de publicação — não precisa
de nenhum passo extra para elas.

Copie os dois diretórios publicados para o servidor (`scp`/`rsync`) em, por exemplo:
```bash
sudo mkdir -p /opt/hospitalaccess/api /opt/hospitalaccess/web
sudo rsync -a /tmp/publish/api/  servidor:/opt/hospitalaccess/api/
sudo rsync -a /tmp/publish/web/  servidor:/opt/hospitalaccess/web/
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
sudo touch /etc/hospitalaccess/api.env /etc/hospitalaccess/web.env
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

`/etc/hospitalaccess/web.env`:
```
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5100
Api__BaseUrl=http://127.0.0.1:5080
```

A API e o front-end conversam entre si só em `127.0.0.1` (loopback) — o Nginx é o
único ponto exposto à rede do hospital (seção 8). Isso evita expor a API/JWT
diretamente na LAN sem necessidade.

## 7. Migrar o banco de dados

A partir da pasta publicada da API (ou de uma máquina com o SDK e o código-fonte):
```bash
# opção A: com o SDK completo e o código-fonte (gera o SQL a partir das Migrations)
dotnet tool install --global dotnet-ef
dotnet ef database update --project HospitalAccess.Infrastructure --startup-project HospitalAccess.Api \
  --connection "Host=localhost;Database=hospital_access;Username=app;Password=TROQUE_ESTA_SENHA"
```
Isso cria as tabelas (`Users`, `Controllers`, `Doors`, `Permissions`, `SyncStatuses`,
`AccessLogs`, `StaffUsers`) e índices. O primeiro `StaffUser` (admin) é criado
automaticamente no primeiro boot da API, a partir de `Seed:AdminUsername`/`Seed:AdminPassword`.

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
SyslogIdentifier=hospitalaccess-api

[Install]
WantedBy=multi-user.target
```

`/etc/systemd/system/hospitalaccess-web.service`:
```ini
[Unit]
Description=HospitalAccess Web (Blazor Server)
After=network.target hospitalaccess-api.service

[Service]
Type=simple
WorkingDirectory=/opt/hospitalaccess/web
ExecStart=/usr/bin/dotnet /opt/hospitalaccess/web/HospitalAccess.Web.dll
Restart=on-failure
RestartSec=5
EnvironmentFile=/etc/hospitalaccess/web.env
User=hospitalaccess
Group=hospitalaccess
SyslogIdentifier=hospitalaccess-web

[Install]
WantedBy=multi-user.target
```

Ativar e subir:
```bash
sudo systemctl daemon-reload
sudo systemctl enable --now hospitalaccess-api hospitalaccess-web
sudo systemctl status hospitalaccess-api hospitalaccess-web --no-pager
```

Ver logs em tempo real (`journalctl` — não há arquivo de log separado, o systemd
já centraliza tudo via `SyslogIdentifier`):
```bash
journalctl -u hospitalaccess-api -f
journalctl -u hospitalaccess-web -f
```

Teste local antes de mexer no Nginx:
```bash
curl -s -X POST http://127.0.0.1:5080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<a senha do Seed:AdminPassword>"}'
# deve devolver {"token":"...","role":"Admin"}
```

## 9. Nginx como reverse proxy

Só o front-end (`HospitalAccess.Web`) precisa ficar exposto — ele fala com a API
internamente. **O WebSocket é obrigatório**: o Blazor Server usa SignalR para o
circuito interativo; sem `Upgrade`/`Connection: upgrade`, a UI carrega mas nenhum
botão/formulário funciona.

```bash
sudo apt-get install -y nginx
```

`/etc/nginx/sites-available/hospitalaccess`:
```nginx
server {
    listen 80;
    server_name acesso.hospital.local;

    location / {
        proxy_pass         http://127.0.0.1:5100;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;
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
As portas 5080 (API) e 5100 (Web direto, sem Nginx) devem ficar **fechadas** para
fora do próprio servidor — só o Nginx (porta 80/443) deve ser alcançável pela rede
do hospital. Não abra a porta da API para a LAN geral: o front-end já fala com ela
via loopback.

## 11. Cadastrar os controladores

Com tudo no ar, acesse `http://acesso.hospital.local/`, faça login com o admin do
seed, e cadastre os 30 controladores em **Controladores** (IP, porta, SN de 16
dígitos, senha de comunicação), depois as portas em **Portas**. Use o botão
**"Testar conexão"** de cada controlador para confirmar que o servidor alcança o
dispositivo por TCP antes de cadastrar usuários/portas nele — isso chama
`ReadSN` de verdade no controlador (prova de conceito do Gateway).

⚠️ Ao ligar o **primeiro** controlador real, valide também o caveat descrito no
`README.md` sobre o `TransactionMessage` (eventos em tempo real) chegando pela
mesma conexão TCP client — é o item mais importante a confirmar com hardware físico.

## 12. Backups

```bash
# cron diário, por exemplo em /etc/cron.d/hospitalaccess-backup
0 3 * * * postgres pg_dump hospital_access | gzip > /var/backups/hospital_access-$(date +\%F).sql.gz
```
Guarde os backups fora do próprio servidor (a política de retenção fica a critério
do hospital — é um sistema de controle de acesso físico, dado sensível).

## 13. Atualizando uma versão nova

```bash
sudo systemctl stop hospitalaccess-api hospitalaccess-web
# publique de novo (seção 4) e sincronize os arquivos para /opt/hospitalaccess/{api,web}
dotnet ef database update --project HospitalAccess.Infrastructure --startup-project HospitalAccess.Api  # se houver migration nova
sudo systemctl start hospitalaccess-api hospitalaccess-web
```

## Checklist rápido de verificação pós-instalação
- [ ] `systemctl status hospitalaccess-api hospitalaccess-web` — `active (running)`.
- [ ] `curl -s http://127.0.0.1:5080/api/auth/login -X POST -H "Content-Type: application/json" -d '{"username":"admin","password":"..."}'` devolve um token.
- [ ] `curl -I http://localhost/login` através do Nginx devolve `200`.
- [ ] Login pela UI funciona e a navegação entre páginas não trava (confirma que o
      WebSocket do Nginx está passando).
- [ ] "Testar conexão" em um controlador cadastrado com IP real devolve o SN do
      dispositivo.
- [ ] Trocar a senha do admin do seed (via banco, por ora — não há tela para isso ainda).
