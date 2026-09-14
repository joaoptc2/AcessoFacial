# Acesso remoto via Cloudflare Tunnel

Como expor o sistema para acesso fora do hospital **sem abrir nenhuma porta de entrada** no
firewall, com autenticação corporativa na frente da aplicação.

> Este guia pressupõe o sistema **já instalado e funcionando na rede interna** conforme
> [`instalacao-servidor-linux.md`](instalacao-servidor-linux.md): API em `127.0.0.1:5080`, Nginx
> como reverse proxy.

---

## 1. Por que Tunnel, e quando NÃO usar

O `cloudflared` abre uma conexão **de saída** para a Cloudflare e recebe as requisições por ela.
Consequências práticas:

- **Nenhuma porta de entrada** no roteador do hospital. Não precisa de IP público, DDNS ou NAT.
- O servidor continua invisível para varredura vinda da internet.
- O **Cloudflare Access** coloca um login corporativo (Google Workspace, Entra ID, ou código por
  e-mail) **antes** da aplicação. O JWT do sistema continua valendo por dentro — duas camadas.

**Quando preferir uma VPN (WireGuard):** o TLS termina na borda da Cloudflare, ou seja, nomes,
documentos e fotos de rosto passam **descriptografados** por um terceiro. Isso é tratamento de
dados pessoais sob a LGPD e precisa de decisão consciente — não é detalhe técnico. Com WireGuard
nada sai da rede, ao custo de precisar de IP público e de instalar cliente em cada máquina.

---

## 2. Pré-requisitos

- Um **domínio gerenciado pela Cloudflare** (os nameservers do domínio apontando para eles).
- Conta Cloudflare; o Tunnel é gratuito e o Access é gratuito até 50 usuários.
- Acesso `sudo` no servidor.

---

## 3. Instalar o cloudflared

```bash
curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg \
  | sudo tee /usr/share/keyrings/cloudflare-main.gpg >/dev/null
echo "deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared any main" \
  | sudo tee /etc/apt/sources.list.d/cloudflared.list
sudo apt-get update
sudo apt-get install -y cloudflared
```

---

## 4. Criar o túnel

```bash
cloudflared tunnel login          # abre o navegador para autorizar o domínio
cloudflared tunnel create hospitalaccess
cloudflared tunnel route dns hospitalaccess acesso.seuhospital.com.br
```

O `create` imprime o **UUID do túnel** e grava as credenciais em
`/root/.cloudflared/<UUID>.json`. Guarde esse arquivo: **ele é a credencial do túnel** e deve
entrar no backup e sair de qualquer repositório.

---

## 5. `/etc/cloudflared/config.yml` — os dois bloqueios que importam

```yaml
tunnel: <UUID-impresso-no-create>
credentials-file: /root/.cloudflared/<UUID>.json

ingress:
  # /welcome/* é PÚBLICO por design: o Home Assistant busca o JPG sem autenticação para exibir
  # na TV do quarto. A imagem contém o NOME DO PACIENTE — não pode sair para a internet.
  - hostname: acesso.seuhospital.com.br
    path: ^/welcome/
    service: http_status:404

  # Phone-home dos aparelhos (/note/insertNoteFace) é [AllowAnonymous] por limitação do firmware,
  # que não envia cabeçalho de autenticação. Só faz sentido dentro da VLAN dos controladores;
  # exposto, qualquer um forja registro de acesso.
  - hostname: acesso.seuhospital.com.br
    path: ^/note/
    service: http_status:404

  - hostname: acesso.seuhospital.com.br
    service: http://127.0.0.1:5080

  - service: http_status:404
```

> ⚠️ **Estes dois `path:` não são opcionais.** Tunelar a aplicação inteira publicaria nomes de
> pacientes na internet e abriria o caminho de forja de log de acesso. O `path` é uma expressão
> regular e a **primeira regra que casa vence** — por isso os bloqueios vêm antes da regra geral.

Aponte para `127.0.0.1:5080` (a API) e não para o Nginx: um proxy a menos na cadeia. O Nginx
continua servindo a rede interna normalmente.

```bash
sudo cloudflared service install
sudo systemctl enable --now cloudflared
sudo systemctl status cloudflared --no-pager
```

---

## 6. Cloudflare Access — autenticação na frente

Sem isto, a aplicação fica exposta à internet inteira, protegida apenas pelo próprio login. Faça:

1. Painel **Zero Trust** → **Access** → **Applications** → **Add an application** → *Self-hosted*.
2. Domínio: `acesso.seuhospital.com.br`.
3. Política mínima recomendada: **Emails ending in** `@seuhospital.com.br` **e exigir MFA**.
4. Para acesso pontual de fornecedor, crie uma política separada com e-mails específicos e
   **prazo de expiração**, em vez de afrouxar a política principal.

Revogar o acesso de alguém passa a ser desativar a conta no provedor de identidade — não há
credencial espalhada para caçar.

---

## 7. Configurar o IP real do cliente

**Este passo é obrigatório.** Atrás de proxy, a aplicação enxerga `127.0.0.1` como origem de toda
requisição. Três proteções dependem do IP verdadeiro:

| O que depende | O que acontece sem a configuração |
|---|---|
| Rate limit do login (10/min por IP) | Vira 10/min para o hospital inteiro — um atacante tranca o login de todos |
| Rate limit do phone-home (120/min por IP) | Vira 120/min para os 30 aparelhos somados: em pico, **registro de acesso legítimo é recusado** |
| Conferência de procedência do aparelho | Nunca casa com o IP cadastrado do controlador |

A aplicação resolve isso sozinha lendo `CF-Connecting-IP` e `X-Forwarded-For`, **mas só de
origens confiáveis**. O padrão (loopback) já cobre Nginx e cloudflared na mesma máquina, que é
esta topologia — então normalmente **não é preciso configurar nada**.

Só declare explicitamente se algum proxy estiver em **outro host**:

```json
"Network": {
  "TrustedProxies": ["10.20.0.5"]
}
```

> ⚠️ Colocar aqui um endereço alcançável por terceiros permite que eles **forjem** o IP de origem
> — e com ele escapem do rate limit ou se passem por um controlador. Declare o mínimo.

---

## 8. Verificação

- [ ] `sudo systemctl status cloudflared` — ativo, sem erro de credencial.
- [ ] `https://acesso.seuhospital.com.br` pede o login do **Cloudflare Access** antes de mostrar
      qualquer tela.
- [ ] Depois do Access, a tela de login do sistema aparece e o login funciona.
- [ ] `curl -I https://acesso.seuhospital.com.br/welcome/leito-x.jpg` devolve **404**
      (não a imagem).
- [ ] `curl -X POST https://acesso.seuhospital.com.br/note/insertNoteFace` devolve **404**.
- [ ] A rede interna continua funcionando pelo Nginx, sem passar pela Cloudflare.
- [ ] Os aparelhos continuam empurrando eventos (a tela de acessos segue recebendo registros).
- [ ] Erre a senha 11 vezes: a 11ª devolve **429**. De outra máquina/rede, o login ainda
      funciona — prova de que o rate limit voltou a ser por cliente, não global.

---

## 9. Operação

**Ver o que está passando:** `sudo journalctl -u cloudflared -f`, ou o painel Zero Trust →
Logs → Access, que mostra quem entrou e quando.

**Derrubar o acesso remoto na emergência** (a rede interna segue intacta):

```bash
sudo systemctl stop cloudflared
```

**Ao trocar o domínio ou recriar o túnel**, refaça o passo 4 e atualize o `config.yml` — o UUID
antigo deixa de valer.

**Backup:** inclua `/etc/cloudflared/config.yml` e `/root/.cloudflared/<UUID>.json`. A cópia de
segurança automática do sistema (§12 do guia de instalação) **não** cobre esses arquivos — ela
cuida do banco e do chaveiro da aplicação.

---

## 10. Duas decisões que continuam em aberto

**Abertura remota de porta.** Com acesso remoto, `POST /api/controllers/{id}/open` abre uma porta
física do hospital de qualquer lugar do mundo, para quem tiver uma sessão válida. Vale decidir
com a segurança se esse endpoint deve existir remotamente — e, se não, bloquear
`^/api/controllers/[^/]+/(open|close|hold-open|lock|unlock)$` no `ingress` pelo mesmo mecanismo
dos passos acima.

**TLS terminado por terceiro.** Já mencionado na §1: a Cloudflare enxerga o tráfego em claro.
Registre essa decisão junto ao encarregado de dados do hospital — é o tipo de coisa que uma
auditoria de LGPD pergunta.
