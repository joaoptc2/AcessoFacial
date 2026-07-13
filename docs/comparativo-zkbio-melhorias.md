# Comparativo ZKBio CVAccess 4.0 × HospitalAccess — Propostas de Melhoria

> **Objetivo:** comparar o nosso sistema com um produto comercial maduro de controle de
> acesso (ZKTeco ZKBio CVAccess 4.0.0) e extrair um backlog de melhorias priorizado.
> **Escopo:** todo o manual (183 páginas) **exceto o módulo de câmera/vídeo (CFTV/VMS)**,
> que foi explicitamente deixado de fora por não fazer parte do nosso produto.
> **Data:** 2026-07.

---

## 1. Resumo executivo

O nosso sistema já cobre bem o **núcleo operacional** do controle de acesso hospitalar:
cadastro de pessoas com face, credencial QR do visitante (lida direto da controladora,
resolvendo a engenharia reversa do FC-8190H), gestão de leito/quarto, monitoramento em
tempo real, alarmes, evacuação em massa, retenção LGPD e integração SDK + HTTP com as ~30
controladoras. Em vários pontos temos **soluções melhores para o nosso caso** (gestão de
leito, QR real da controladora, callback phone-home) que o ZKBio não tem.

As lacunas relevantes são, quase todas, **de modelagem e de operação em escala** — não de
funcionalidade de porta:

1. **Modelo de permissão plano.** Hoje a permissão é `usuário → controladora → grupo de
   horário`, uma a uma. O ZKBio usa **Níveis de Acesso reutilizáveis** (`Nível = portas +
   faixa horária`) atribuídos a pessoa, departamento ou nível. Com 30 portas e muitos
   funcionários, esse é o maior ganho operacional disponível.
2. **RBAC raso.** Temos 3 papéis fixos (Admin/Operator/Reception) sem escopo por área. O
   ZKBio tem papéis com permissão por menu **e** recorte de dados por área/departamento.
3. **Sem áreas/zonas**, sem backup/restore, sem e-mail/SMTP, sem endurecimento de senha
   (bloqueio por tentativa, expiração, troca forçada), sem relatórios "quem acessa a porta X".
4. **Regras de porta avançadas** (intertravamento, antirretorno, regra dos dois, primeira
   pessoa abre, vinculação evento→ação) — de alto valor hospitalar, mas **dependentes do que
   o FC-8190H expõe** (ver §6). Precisam de verificação de hardware antes de virar backlog.

Recomendação: atacar primeiro os itens de **software puro e alto alavancamento** (Níveis de
Acesso, Áreas, RBAC granular, relatórios de auditoria, e-mail de alarme, endurecimento de
login, backup). As regras de porta ficam num trilho separado, condicionado à capacidade do
equipamento.

---

## 2. Comparativo por área

Legenda: ✅ paridade · 🟡 parcial · ❌ lacuna · ⭐ diferencial nosso

| Área | ZKBio CVAccess | HospitalAccess (nós) | Situação |
|---|---|---|---|
| **Modelo de permissão** | Nível de Acesso reutilizável = portas + faixa horária; atribuído por pessoa/departamento/nível | Permissão plana `usuário→controladora→timegroup` | ❌ |
| **Faixas horárias** | Até 255 zonas, 3 intervalos/dia + 3 por tipo de feriado | TimeGroup 1-64 com segmentos, empurrados p/ device | 🟡 |
| **Feriados** | 3 tipos, 32 datas/tipo, recorrente | Calendário de feriados | ✅ |
| **Departamentos / cargos** | Árvore de departamentos + cargos, herança de acesso | UserGroup plano (organizacional, sem herança) | 🟡 |
| **Áreas / zonas** | Áreas hierárquicas p/ filtrar dispositivos e recortar RBAC | — | ❌ |
| **Atributos por pessoa** | Superusuário, tempo de passagem estendido, validade, desativar, função no device | Validade, timegroup, cartão, foto; revogar/reativar | 🟡 |
| **Campos personalizados** | Campos custom em pessoa e visitante | — | ❌ |
| **Cadastro / biometria** | Face, digital, palma, veia; foto; duress fingerprint; import/export | Face + cartão Mifare (nº) | 🟡 (nosso hardware é face) |
| **Cartões** | Emissão em lote, perda/reativação, 9 formatos Wiegand | Número de cartão Mifare por usuário | 🟡 |
| **Dispositivos** | Add manual/auto, upgrade FW, reboot, sync, hora, substituir device | CRUD, descoberta UDP, rede, relógio, resync forçado, kiosk | ✅ |
| **Portas** | Verify mode, tempo de abertura, sensor, coação, senha emergência, cópia p/ todas | Comandos remotos + config kiosk/local | 🟡 |
| **Intertravamento** | Airlock/mantrap por controladora | — | ❌ (ver §6) |
| **Antirretorno** | In/out por leitores | — | ❌ (ver §6) |
| **Regra dos dois / multi-pessoa** | Grupos + combinações, abre só com N pessoas | — | ❌ (ver §6) |
| **Primeira pessoa abre** | Mantém porta aberta após 1ª autenticação autorizada | — | ❌ (ver §6) |
| **Vinculação (linkage)** | Evento→ação (entrada→saída/relé/e-mail) | Config de alarme + evacuação em massa | 🟡 (ver §6) |
| **Monitoramento em tempo real** | Status porta, abrir/fechar remoto, cancelar alarme, mapa | BeginWatch, push de evento/alarme, dashboard | ✅ |
| **Alarmes** | Monitor, aceitar/tratar, severidade, som por evento | Config por controladora, disparar/limpar, log, evacuação | ✅ |
| **Mapa / e-map** | Plantas com ícones de porta, ação pelo mapa | — | ❌ (baixa prioridade) |
| **Relatórios** | Transações, exceções, alarme, **acesso por porta**, **acesso por pessoa**, 1º-entra-último-sai; export Excel/PDF/CSV | Consulta+export de log de acesso; trilhas de auditoria (porta, usuário) | 🟡 |
| **Visitantes — básico** | Entrada/saída, clonar, habilitar/desativar | Criação (1 quarto), trocar quarto, revogar, expiração automática | ✅/⭐ |
| **Visitantes — reserva** | Reserva, convite por e-mail (auto-preenche), auto check-in | — | ❌ |
| **Visitantes — autoatendimento** | QR de auto-registro, declaração de saúde, foto obrigatória | — | ❌ |
| **Visitantes — watchlist** | Lista de observação por nome/doc/país/empresa | — | ❌ |
| **Visitantes — crachá** | Impressão de recibo/crachá | — | ❌ |
| **Visitantes — auto-checkout** | Expirados saem sozinhos a cada 30 min | Job de expiração automática | ✅ |
| **RBAC / papéis** | Papéis com permissão por menu + escopo por área/departamento | 3 papéis fixos (Admin/Operator/Reception) | 🟡 |
| **Endurecimento de login** | Força de senha, bloqueio por N tentativas, expiração, troca forçada, reautenticação, CAPTCHA | JWT + rate-limit simples de login | 🟡 |
| **Log de operação (auditoria)** | Toda ação: operador, IP, módulo, resultado, tempo; export | Trilha de comando de porta + trilha de admin de usuário | 🟡 |
| **Backup / restore** | Backup imediato + agendado + FTP; restore; limpeza de disco | — (só retenção/purga LGPD) | ❌ |
| **Limpeza de dados** | Retenção por tipo (acesso/visitante/logs/comandos/backups) | Política de retenção + purga diária | ✅ |
| **E-mail / SMTP** | SMTP configurável, log de envio, notificações de evento | — | ❌ |
| **Proteção de dado sensível** | Mascaramento por campo (nome, e-mail, cartão…) | Criptografia de segredos em repouso (DataProtection) | 🟡 |
| **API / integração** | Registro de cliente, códigos de autorização | Integração SDK + HTTP (bespoke FC-8190H) | ⭐ |
| **Credencial QR** | QR estático/dinâmico configurável | **QR real lido da controladora** (resolve o firmware do FC-8190H) | ⭐ |
| **Gestão de leito** | — | **Transferência de quarto do paciente** (invalida QR antigo, gera novo) | ⭐ |

---

## 3. Onde já somos melhores (para o nosso contexto)

- **QR real da controladora.** O firmware do FC-8190H valida o QR contra um texto que ele
  mesmo cunha (timestamp em microssegundos, irreproduzível). Nós lemos/provisionamos via HTTP
  — o ZKBio gera QR próprio, mas não resolve esse acoplamento específico do device.
- **Gestão de leito/quarto do paciente.** Modelo de "1 quarto por visitante + trocar quarto"
  (deleta o QR antigo, gera o novo) é domínio hospitalar puro; o ZKBio não tem o conceito.
- **Callback phone-home** como canal alternativo de eventos, além do BeginWatch do SDK.
- **Integração enxuta e específica** com o parque FC-8190H, em vez de um produto genérico.

---

## 4. Backlog priorizado de melhorias

Esforço: **P** pequeno · **M** médio · **G** grande. Cada item traz o valor hospitalar.

### P0 — Fazer (software puro, alto alavancamento, alinhado ao uso atual)

| # | Melhoria | Esforço | Por que (hospital) |
|---|---|---|---|
| 1 | **Níveis de Acesso reutilizáveis** (`Nível = portas + faixa horária`, atribuído a usuário/grupo). Substitui/encapsula a permissão plana atual. | G | Com 30 portas e dezenas de funcionários, gerir permissão porta-a-porta não escala. "Enfermagem Ala B = {5 portas}+{turno}" atribuído de uma vez. Base para quase tudo abaixo. |
| 2 | **Áreas / zonas** (agrupar controladoras por prédio/andar/ala). | M | Filtrar monitoramento e **recortar RBAC** por ala; pré-requisito prático de operação em escala. |
| 3 | **Relatórios de auditoria de permissão**: "quem acessa a porta X" e "quais portas a pessoa Y abre", + export Excel/PDF (hoje só CSV do log). | M | Exigência de auditoria/compliance hospitalar; responde à pergunta que a diretoria e a CCIH sempre fazem. |
| 4 | **E-mail/SMTP + notificação de alarme** (SMTP configurável, envio em alarme e lista diária de visitantes). | M | Segurança precisa ser avisada de coação/porta forçada/desconexão sem estar na tela. |

### P1 — Deveria (valor claro, esforço médio)

| # | Melhoria | Esforço | Por que (hospital) |
|---|---|---|---|
| 5 | **RBAC granular**: papéis com permissão por menu **e** escopo por área/departamento. | M-G | Recepção da Ala A só enxerga/opera a Ala A; separação de funções exigida por LGPD/segurança. |
| 6 | **Endurecimento de login**: política de força de senha, bloqueio após N tentativas, expiração, troca forçada no 1º acesso, reautenticação em ação sensível. | M | Estação de trabalho hospitalar é compartilhada; hoje só há rate-limit. Baixo custo, alto ganho de segurança. |
| 7 | **Log de operação unificado** (toda ação de admin: operador, IP, módulo, resultado, tempo) + export. | M | Consolida as trilhas hoje espalhadas; auditoria única para investigação de incidente. |
| 8 | **Backup/restore de banco** agendado (pg_dump + retenção), com restore documentado. | M | Continuidade de dados de acesso é crítica; hoje não há backup automatizado. |
| 9 | **Visitante — convite por e-mail com QR** (envia o QR à família antes da chegada) + **reserva/pré-cadastro**. | M | Fluxo natural de acompanhante hospitalar: familiar recebe o QR antes, agiliza a portaria. |
| 10 | **Visitante — watchlist/lista de observação** (barrar por nome/documento). | M | Bloquear indivíduos com medida protetiva/restrição de visita — necessidade real em hospital. |
| 11 | **Departamentos hierárquicos + herança de acesso** (integra ao item 1). | M | "Radiologia inteira ganha o nível X"; novo funcionário do setor herda o acesso. |

### P2 — Bom ter (incremental)

| # | Melhoria | Esforço | Por que (hospital) |
|---|---|---|---|
| 12 | **Campos personalizados** em pessoa/visitante (ex.: questionário de saúde, matrícula). | P-M | Flexibiliza cadastro sem alterar schema. |
| 13 | **Atributos por pessoa**: validade de acesso explícita, desativar temporário (além de revogar). | P | Suspender crachá sem apagar histórico. |
| 14 | **Mapa/e-map** de portas com ação pelo ícone. | M-G | Bom para plantão de segurança; ganho estético/operacional, não essencial. |
| 15 | **Impressão de crachá/recibo de visitante.** | M | Depende de haver impressora de crachá na portaria. |
| 16 | **Mascaramento de dado sensível** na UI (nome/documento) por perfil. | P-M | Reforço LGPD além da criptografia em repouso que já temos. |

### Trilho separado — Regras de porta (ver §6: dependem do hardware)

| # | Melhoria | Valor (hospital) |
|---|---|---|
| 17 | **Regra dos dois / multi-pessoa** | Cofre de psicotrópicos/controlados (ANVISA), banco de sangue, necrotério — ninguém entra sozinho. **Maior valor de compliance** se o device suportar. |
| 18 | **Intertravamento (airlock)** | Isolamento, farmácia, laboratório, sala limpa — as duas portas nunca abrem juntas. |
| 19 | **Antirretorno** | Farmácia de controlados, arquivo, área biológica — evita "carona"/reuso e dá ocupação real. |
| 20 | **Vinculação evento→ação** | Incêndio libera rota de fuga; coação → alarme silencioso; porta forçada → sirene + e-mail. |
| 21 | **Primeira pessoa abre** | Ambulatório abre ao público após o 1º funcionário autorizado bater ponto e re-tranca sozinho. |
| 22 | **Senha de coação + tempo de passagem estendido** | Coação dispara alarme silencioso; passagem estendida para cadeirante/mobilidade reduzida. |

---

## 5. Roadmap sugerido

- **Onda 1 (fundação):** Áreas (2) → Níveis de Acesso (1) → Relatórios de auditoria (3).
  Reorganiza a base de permissão e entrega valor visível de auditoria.
- **Onda 2 (segurança operacional):** RBAC granular (5) + endurecimento de login (6) + log de
  operação unificado (7) + backup (8).
- **Onda 3 (visitantes):** convite por e-mail com QR + reserva (9) + watchlist (10),
  reaproveitando o e-mail/SMTP (4).
- **Trilho paralelo (hardware):** levantar com um FC-8190H o que o SDK/firmware expõe de
  regra dos dois, intertravamento, antirretorno, vinculação e senha de coação (§6) e só então
  priorizar os itens 17-22.

---

## 6. Nota importante sobre o hardware (FC-8190H)

Boa parte das "regras de porta" do ZKBio (intertravamento, antirretorno, regra dos dois,
primeira-pessoa-abre) é **executada na controladora**, não no servidor, e o ZKBio as
condiciona ao **tipo de painel** (1, 2 ou 4 portas):

- **Intertravamento** exige ≥2 portas no mesmo painel e sensor de porta cabeado.
- **Antirretorno** exige leitor de entrada **e** de saída na mesma porta.
- **Regra dos dois / multi-pessoa** e **primeira-pessoa-abre** exigem suporte de firmware.

O nosso parque é de terminais **de face, tipicamente 1 porta por controladora**. Antes de
colocar os itens 17-22 no backlog é preciso **confirmar com um FC-8190H real** o que o SDK e o
firmware realmente expõem — pode ser um subconjunto, ou nada. Por isso eles ficam num trilho
separado, condicionado a essa verificação, e não competem com os ganhos de software (§4) que
independem do equipamento.

---

*Base: manual "ZKBio CVAccess 4.0.0 User Manual EN v1.0 (2023-11-29)", 183 páginas, módulo de
vídeo/câmera excluído. Comparação feita contra o código-fonte atual do HospitalAccess.*
