# Painel de TV dos leitos

Ver e operar a TV de um quarto sem entrar nele. O caso de uso que originou a funcionalidade é
um só: **quando a conta de um serviço de streaming cai num quarto, alguém loga de novo da mesa.**

> Isto NÃO é gestão de hóspedes. Não há limpeza de check-out, não há quiosque, e as contas dos
> serviços são do hospital — não se apaga nada por rotina.

---

## 1. Como funciona

Cada quarto tem um **stick Android** (Intelbras Izy Play / Homatics SEI300DHM) além da
controladora de porta. São aparelhos diferentes, com IPs diferentes:

| Campo do cadastro | Aparelho |
|---|---|
| `IpAddress` | controladora de porta FC-8190H |
| `TvIpAddress` | stick de TV do quarto |

O servidor fala com o stick por **ADB sobre a rede**, na porta 5555. A API executa o binário
`adb` como processo — não há biblioteca, não há agente instalado no stick.

O painel mostra uma **captura por segundo**, não vídeo: o aparelho leva cerca de meio segundo só
para comprimir o PNG. Para operar de verdade (navegar menus rápido), há um link que abre o
**scrcpy** instalado na máquina do operador.

---

## 2. Por que ADB e não um agente

Um teste de bancada em 16/09/2026 mediu as cinco capacidades que decidiriam a arquitetura. O
resultado descartou a abordagem por agente com política de dispositivo:

| Capacidade | Resultado |
|---|---|
| Device Owner (`dpm set-device-owner`) | ❌ o firmware não declara `android.software.device_admin` |
| Instalar, remover, limpar dados de app | ✅ por ADB |
| ADB sobreviver ao reboot | ✅ a porta 5555 sobe sozinha e já autorizada |
| Definir a tela inicial, bloquear apps | ✅ `cmd role` e `pm suspend` |
| Ver a tela remotamente | ✅ `screencap` (o MediaProjection nem foi necessário) |

Sem Device Owner não existe **imposição**: o sistema detecta e corrige, não proíbe. Para
manutenção, é suficiente.

---

## 3. Instalação no servidor

```bash
sudo apt-get install -y android-tools-adb
adb version
```

Se o serviço rodar com um PATH enxuto, aponte o caminho completo:

```json
"Tv": {
  "AdbPath": "/usr/bin/adb"
}
```

### Opções disponíveis

| Chave | Padrão | Para quê |
|---|---|---|
| `Tv:AdbPath` | `adb` | Caminho do executável |
| `Tv:AdbKeyDirectory` | (vazio) | HOME do adb — onde fica a chave deste servidor. Ver §3b |
| `Tv:CommandTimeoutSeconds` | `15` | Comando comum (status, tecla, texto) |
| `Tv:ScreenshotTimeoutSeconds` | `25` | Captura de tela |
| `Tv:LongCommandTimeoutSeconds` | `120` | Instalar APK, limpar dados |

---

## 3b. A chave do adb é do USUÁRIO do serviço, não do servidor

**É aqui que a instalação falha na primeira vez.** O adb guarda a chave deste servidor em
`~/.android/adbkey` — por usuário. O guia cria o serviço com `--no-create-home`, então o usuário
`hospitalaccess` não tem HOME e não consegue manter chave estável: a TV recusa a autenticação a
cada execução, e o painel reporta a TV como indisponível mesmo com o aparelho ligado.

Autorizar a chave do SEU usuário (`premier`, por exemplo) **não resolve** — é outra chave.

```bash
# 1. Casa estável para a chave do serviço
sudo mkdir -p /var/lib/hospitalaccess/.android
sudo chown -R hospitalaccess:hospitalaccess /var/lib/hospitalaccess
```

```json
"Tv": {
  "AdbKeyDirectory": "/var/lib/hospitalaccess"
}
```

```bash
# 2. Derrubar qualquer servidor adb de outro usuário — quem sobe o servidor define a chave em uso
adb kill-server 2>/dev/null; sudo pkill -f 'adb.*fork-server' 2>/dev/null

# 3. Gerar a chave DO SERVIÇO e disparar o pedido de autorização na TV
sudo -u hospitalaccess env HOME=/var/lib/hospitalaccess adb connect 192.168.19.11:5555
```

Se a TV mostrar **"Permitir depuração?"**, marque *Sempre permitir* e aceite. Se em vez disso o
comando devolver `failed to authenticate` sem diálogo nenhum, o aparelho exige o pareamento do
Android 11+: na TV, **Opções do desenvolvedor → Depuração por Wi-Fi → Parear dispositivo com
código**, e então (com a porta e o código que aparecem ali, **a porta do pareamento é diferente
da 5555**):

```bash
sudo -u hospitalaccess env HOME=/var/lib/hospitalaccess adb pair 192.168.19.11:PORTA_DO_PAREAMENTO
sudo -u hospitalaccess env HOME=/var/lib/hospitalaccess adb connect 192.168.19.11:5555
```

```bash
# 4. Conferir — tem que sair "device", nunca "unauthorized"
sudo -u hospitalaccess env HOME=/var/lib/hospitalaccess adb devices -l
sudo systemctl restart hospitalaccess-api
```

> ⚠️ O **servidor do adb** é um só na máquina, na porta 5037, e as chaves ficam com ele. Se outro
> usuário já tiver subido um servidor, o serviço vai usar a chave DELE — funciona por acidente e
> quebra no próximo reboot. Por isso o passo 2 derruba tudo antes.

---

## 4. Provisionamento de cada stick (bancada)

**Passo obrigatório, uma vez por aparelho, antes de subir ao quarto.** A chave do ADB fica em
`/data/misc/adb/adb_keys` no stick — um reset de fábrica a apaga, e todo aparelho que voltar da
assistência técnica precisa passar por aqui de novo.

1. No stick: **Configurações → Sobre → Versão** → clicar 7 vezes até virar desenvolvedor.
2. **Opções do desenvolvedor** → ligar **Depuração USB** e **Depuração por Wi-Fi**.
3. Anotar o IP e reservá-lo por MAC no roteador (o cadastro guarda um IP fixo).
4. Do servidor:

```bash
adb connect 192.168.19.11:5555
# aceitar o diálogo "Sempre permitir" que aparece NA TV
adb -s 192.168.19.11:5555 shell getprop ro.product.model
```

5. Reiniciar o stick e conferir que a 5555 volta **sozinha**:

```bash
adb disconnect 192.168.19.11:5555
adb connect 192.168.19.11:5555   # tem que voltar como "device", não "unauthorized"
```

6. Cadastrar o IP em **Controladores → (o leito) → IP da TV do quarto**.

> A **depuração por Wi-Fi** sorteia uma porta nova a cada ativação e se desliga no reboot —
> não serve como canal. Só a 5555 é estável.

---

## 5. Segurança

### A captura de tela é dado pessoal

A TV do quarto exibe a imagem de boas-vindas **com o nome do paciente**. Consequências, todas já
implementadas:

- O PNG sai com `Cache-Control: no-store` e **nunca** é gravado em disco.
- A **Recepção não tem acesso** a nenhum endpoint `/api/tv` — ver a TV é manutenção, não
  atendimento.
- Abrir o painel registra `AbrirPainelTV` na trilha de auditoria, com usuário, quarto e horário.
- O **conteúdo digitado nunca é registrado** — costuma ser senha. A auditoria guarda só
  `DigitarTextoTV`, o fato.

### Isto não sai pelo túnel

`docs/acesso-remoto-cloudflare.md` bloqueia `^/api/tv/` no `ingress`, junto de `^/welcome/` e
`^/note/`. Ver a TV de um quarto pela internet é uma decisão separada desta.

### ⚠️ A porta 5555 exige VLAN separada

Esta é a pendência de rede mais importante do projeto.

A 5555 aberta dá **controle total do stick** a qualquer máquina que consiga autorizar uma chave —
e é a mesma porta que faz o painel funcionar. Não há como ter um sem o outro.

Hoje os sticks e as controladoras dividem a faixa `192.168.19.x`. **Se a rede de visitantes
alcança essa faixa, qualquer pessoa com um notebook no hospital controla as 30 TVs.** A correção
é segregar a VLAN dos quartos; é trabalho de rede, não de código, e não é opcional.

---

## 6. Como os comandos são construídos

Tudo que passa de `adb shell` em diante é executado pelo **shell do aparelho**. O servidor não
usa shell nenhum (os argumentos vão por `ArgumentList`), mas o adb junta os argumentos numa
linha só no destino. Por isso:

| Entrada do operador | Tratamento |
|---|---|
| Nome de pacote | Regex fechada: letras, dígitos, `_` e pontos. Nada mais chega ao aparelho |
| Tecla | Lista fechada (`AdbCommandRules.AllowedKeys`), sensível a caixa |
| Texto digitado | Aspas simples com `'\''` para apóstrofo; espaço vira `%s` |

As regras estão em `HospitalAccess.Api/Services/AdbCommandRules.cs`, separadas do I/O justamente
para serem testáveis — ver `HospitalAccess.Tests/Devices/AdbCommandRulesTests.cs`.

**Detalhe não óbvio do Android:** o `input text` troca toda ocorrência de `%s` por espaço. Um
`%s` literal numa senha seria corrompido em silêncio, então o texto é quebrado em pedaços de
forma que nenhum contenha a sequência inteira.

---

## 7. Comandos medidos no aparelho

Medidos em 16/09/2026 num Homatics SEI300DHM (Android 14, série 3U6M1907130Y3).

**Funcionam:** `connect`, `getprop`, `cat /proc/uptime`, `screencap`, `pm list packages -3`,
`dumpsys window`, `install`, `uninstall`, `pm clear`, `pm suspend`/`unsuspend`,
`cmd role add-role-holder`, `settings put`, `monkey -c LEANBACK_LAUNCHER`, `reboot`,
`input keyevent`, `input text`, `am force-stop`.

**Recusados por este firmware:**

| Comando | Resposta |
|---|---|
| `dpm set-device-owner` | sem `android.software.device_admin` |
| `pm disable-user` | `Warning! This command is illegal!` (alteração da fabricante) |
| `pm hide` | `SecurityException: MANAGE_USERS` |

O `pm suspend` cobre o que os dois últimos fariam — e melhor: o app suspenso continua visível e
avisa que está indisponível, em vez de sumir da tela e confundir quem está no quarto. Funciona
inclusive com `com.android.tv.settings`, o que tira do alcance do quarto quase tudo que
desfaria a configuração.

---

## 8. O que ficou de fora

- **Relogin automático.** Nenhum app de streaming registra a sessão no `AccountManager` do
  Android (medido: só `type=com.google` aparece), então não há como saber que uma conta caiu sem
  olhar a tela. O painel resolve de outro jeito: alguém loga em dois minutos, da mesa.
- **Vídeo ao vivo no navegador.** Exigiria um segundo runtime no servidor. O link para o scrcpy
  local cobre quem precisa de velocidade.
- **Quiosque de verdade.** Impossível neste hardware, e desnecessário sob este escopo.
