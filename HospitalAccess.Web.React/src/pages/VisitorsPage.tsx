import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, ApiError, downloadBlob, type ControllerDto, type UserGroupDto, type VisitorListItemDto } from "../lib/api";
import { useFeedback } from "../lib/feedback";

function toLocalInputValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function statusLabel(v: VisitorListItemDto): string {
  return v.isRevoked ? "Revogado" : v.isExpired ? "Expirado" : "Ativo";
}

function statusClass(v: VisitorListItemDto): string {
  return v.isRevoked ? "pill" : v.isExpired ? "pill pill-danger" : "pill pill-success";
}

export function VisitorsPage() {
  const { confirm, toastSuccess } = useFeedback();
  const [visitors, setVisitors] = useState<VisitorListItemDto[]>([]);
  const [controllers, setControllers] = useState<ControllerDto[]>([]);
  const [name, setName] = useState("");
  const [validUntil, setValidUntil] = useState(() => toLocalInputValue(new Date(Date.now() + 24 * 60 * 60 * 1000)));
  const [selectedRoom, setSelectedRoom] = useState<string>("");
  const [changingRoomId, setChangingRoomId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [qrImage, setQrImage] = useState<string | null>(null);
  const [qrBlob, setQrBlob] = useState<Blob | null>(null);
  const [qrVisitorName, setQrVisitorName] = useState("");
  const [search, setSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState("");

  // Visitante de grupo: recebe as portas padrão do grupo e NÃO tem QR.
  const [groups, setGroups] = useState<UserGroupDto[]>([]);
  const [groupId, setGroupId] = useState("");
  const [groupName, setGroupName] = useState("");
  const [groupValidUntil, setGroupValidUntil] = useState(() =>
    toLocalInputValue(new Date(Date.now() + 7 * 24 * 60 * 60 * 1000)),
  );
  const [groupCard, setGroupCard] = useState("");
  const [groupBusy, setGroupBusy] = useState(false);

  const load = async () => setVisitors(await api.getVisitors());

  useEffect(() => {
    load();
    api.getControllers().then(setControllers).catch(() => setControllers([]));
    api.getUserGroups().then(setGroups).catch(() => setGroups([]));
  }, []);


  const filtered = useMemo(
    () =>
      visitors.filter(
        (v) =>
          (!search || v.name.toLowerCase().includes(search.toLowerCase())) &&
          (!statusFilter || statusLabel(v).toLowerCase() === statusFilter.toLowerCase()),
      ),
    [visitors, search, statusFilter],
  );

  async function showQr(id: string, visitorName: string) {
    try {
      const blob = await api.generateVisitorQr(id);
      setQrVisitorName(visitorName);
      setQrBlob(blob);
      setQrImage(URL.createObjectURL(blob));
    } catch (err) {
      // O QR agora é LIDO da controladora (via API HTTP). Mensagens 422/502 explicam se falta
      // configurar a API do controlador ou se ele está offline — surface para o operador.
      setError(err instanceof ApiError ? err.message : "Falha ao obter o QR da controladora.");
    }
  }

  function downloadQr() {
    if (!qrBlob) return;
    const safeName = qrVisitorName.replace(/[^\w.-]+/g, "_") || "visitante";
    downloadBlob(qrBlob, `qr-${safeName}.png`);
  }

  async function handleCreateGroup(e: FormEvent) {
    e.preventDefault();
    setGroupBusy(true);
    setError(null);
    try {
      const r = await api.createGroupVisitor({
        name: groupName,
        validUntil: new Date(groupValidUntil).toISOString(),
        groupId,
        cardNumber: groupCard ? Number(groupCard) : undefined,
      });
      toastSuccess(
        `${groupName} criado no grupo ${r.groupName} com ${r.doorCount} porta(s).` +
          (r.hasCard ? "" : " Sem cartão: a pessoa ainda não consegue abrir porta nenhuma."),
      );
      setGroupName("");
      setGroupCard("");
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao criar o visitante de grupo.");
    } finally {
      setGroupBusy(false);
    }
  }

  async function handleCreate(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setQrImage(null);

    const validUntilDate = new Date(validUntil);
    if (validUntilDate <= new Date()) {
      setError("Validade deve ser no futuro.");
      return;
    }
    if (!selectedRoom) {
      setError("Selecione o quarto (porta) do visitante.");
      return;
    }

    setBusy(true);
    try {
      const created = await api.createVisitor({
        name,
        validUntil: validUntilDate.toISOString(),
        controllerIds: [selectedRoom],
      });
      await showQr(created.id, name);
      setName("");
      setSelectedRoom("");
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao cadastrar.");
    } finally {
      setBusy(false);
    }
  }

  async function handleChangeRoom(visitor: VisitorListItemDto, controllerId: string) {
    setError(null);
    setChangingRoomId(null);
    try {
      await api.changeVisitorRoom(visitor.id, controllerId);
      await load();
      // Regera o QR já apontando para o novo quarto (o antigo é invalidado no controlador).
      await showQr(visitor.id, visitor.name);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao trocar o quarto.");
    }
  }

  async function handleRevoke(id: string) {
    setError(null);
    try {
      await api.revokeVisitor(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao revogar.");
    }
  }

  async function handleDelete(id: string, visitorName: string) {
    setError(null);
    if (!(await confirm({
      title: `Excluir o visitante "${visitorName}"?`,
      text: "Remove a pessoa dos controladores e apaga o cadastro junto com o QR. Não é reversível.",
      confirmLabel: "Excluir",
      danger: true,
    })))
      return;
    try {
      await api.deleteVisitor(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao excluir.");
    }
  }

  return (
    <div>
      <h2>Visitantes / temporários</h2>
      <p className="text-muted" style={{ marginTop: 0 }}>
        São dois tipos, e o que os separa é como a pessoa se identifica no leitor.
      </p>

      <form className="card" style={{ marginBottom: "1.25rem", maxWidth: 480 }} onSubmit={handleCreate}>
        <h3 style={{ marginTop: 0, fontSize: "1.02rem" }}>Visitante com QR — uma porta</h3>
        <p className="text-muted" style={{ marginTop: 0, fontSize: "0.85rem" }}>
          Abre por QR, que é cunhado pela controladora e só vale nela. Por isso fica em{" "}
          <strong>uma porta por vez</strong> — use “Trocar quarto” para mudá-la.
        </p>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label>Nome</label>
          <input value={name} onChange={(e) => setName(e.target.value)} required />
        </div>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label>Válido até</label>
          <input type="datetime-local" value={validUntil} onChange={(e) => setValidUntil(e.target.value)} required />
        </div>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label>Quarto (porta)</label>
          {controllers.length === 0 ? (
            <span className="text-muted">Nenhum controlador cadastrado.</span>
          ) : (
            <select value={selectedRoom} onChange={(e) => setSelectedRoom(e.target.value)}>
              <option value="">(selecione o quarto)</option>
              {controllers.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          )}
        </div>
        {error && <div className="alert alert-danger">{error}</div>}
        <button type="submit" className="btn btn-primary" disabled={busy || !selectedRoom}>
          Cadastrar e gerar QR
        </button>
        {!selectedRoom && (
          <p className="text-muted" style={{ fontSize: "0.8rem", marginBottom: 0 }}>
            Selecione o quarto para habilitar o cadastro.
          </p>
        )}
      </form>

      <form className="card" style={{ marginBottom: "1.25rem", maxWidth: 480 }} onSubmit={handleCreateGroup}>
        <h3 style={{ marginTop: 0, fontSize: "1.02rem" }}>Visitante com cartão — várias portas</h3>
        <p className="text-muted" style={{ marginTop: 0, fontSize: "0.85rem" }}>
          Recebe de uma vez todas as portas padrão de um grupo — um prestador de serviço, por
          exemplo. <strong>Não tem QR</strong>, porque um QR só vale na porta que o cunhou; a
          identificação é pelo <strong>cartão</strong>.
        </p>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label htmlFor="gvName">Nome</label>
          <input id="gvName" value={groupName} onChange={(e) => setGroupName(e.target.value)} required />
        </div>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label htmlFor="gvGroup">Grupo</label>
          {groups.length === 0 ? (
            <span className="text-muted">Nenhum grupo cadastrado.</span>
          ) : (
            <select id="gvGroup" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
              <option value="">(selecione o grupo)</option>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.name}
                </option>
              ))}
            </select>
          )}
        </div>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label htmlFor="gvUntil">Válido até</label>
          <input
            id="gvUntil"
            type="datetime-local"
            value={groupValidUntil}
            onChange={(e) => setGroupValidUntil(e.target.value)}
            required
          />
        </div>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label htmlFor="gvCard">Número do cartão</label>
          <input
            id="gvCard"
            type="number"
            min={0}
            value={groupCard}
            onChange={(e) => setGroupCard(e.target.value)}
            placeholder="crachá do visitante"
          />
          <span className="text-muted" style={{ fontSize: "0.8rem" }}>
            Sem cartão, a pessoa fica cadastrada mas não consegue abrir nenhuma porta.
          </span>
        </div>
        <button type="submit" className={`btn btn-primary${groupBusy ? " is-busy" : ""}`} disabled={groupBusy || !groupId}>
          Criar visitante de grupo
        </button>
      </form>

      {qrImage && (
        <div className="card" style={{ marginBottom: "1.25rem", maxWidth: 360 }}>
          <h4 style={{ marginTop: 0 }}>QR de acesso — {qrVisitorName}</h4>
          {/* Fundo branco com folga: a "zona de silêncio" (moldura branca) é obrigatória para o
              leitor localizar o QR. image-rendering: pixelated evita que o navegador borre as
              bordas dos módulos ao redimensionar (QR borrado = leitura falha). */}
          <div style={{ background: "#fff", padding: 16, borderRadius: 8, display: "inline-block" }}>
            <img
              src={qrImage}
              alt="QR de acesso"
              style={{ display: "block", width: 260, height: 260, imageRendering: "pixelated" }}
            />
          </div>
          <div className="btn-group" style={{ marginTop: "0.6rem" }}>
            <button type="button" className="btn btn-primary btn-sm" onClick={downloadQr}>
              Baixar PNG
            </button>
          </div>
          <p className="text-muted" style={{ fontSize: "0.8rem", marginTop: "0.6rem", marginBottom: 0 }}>
            Imprima/envie a imagem inteira (com a moldura branca). O QR abre a porta assim que a
            sincronização do visitante concluir.
          </p>
        </div>
      )}

      <div className="card form-row" style={{ marginBottom: "1rem" }}>
        <div className="form-field" style={{ minWidth: 260 }}>
          <label>Buscar</label>
          <input placeholder="Nome..." value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
        <div className="form-field">
          <label>Status</label>
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">(todos)</option>
            <option value="ativo">Ativo</option>
            <option value="expirado">Expirado</option>
            <option value="revogado">Revogado</option>
          </select>
        </div>
      </div>

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Nome</th>
              <th>Código</th>
              <th>Quarto</th>
              <th>Válido até</th>
              <th>Status</th>
              <th>Sincronização (QR)</th>
              <th>Ações</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map((v) => (
              <tr key={v.id}>
                <td>{v.name}</td>
                <td>{v.userCode}</td>
                <td>
                  {changingRoomId === v.id ? (
                    <select
                      autoFocus
                      defaultValue={v.controllers[0]?.controllerId ?? ""}
                      onChange={(e) => e.target.value && handleChangeRoom(v, e.target.value)}
                      onBlur={() => setChangingRoomId(null)}
                    >
                      <option value="">(selecione)</option>
                      {controllers.map((c) => (
                        <option key={c.id} value={c.id}>
                          {c.name}
                        </option>
                      ))}
                    </select>
                  ) : (
                    <span>{v.controllers[0]?.controllerName ?? <span className="text-muted">—</span>}</span>
                  )}
                </td>
                <td>{v.validUntil ? new Date(v.validUntil).toLocaleString() : "—"}</td>
                <td>
                  <span className={statusClass(v)}>{statusLabel(v)}</span>
                </td>
                <td>
                  {v.syncTotal === 0 ? (
                    <span className="pill pill-danger" title="Sem porta — o QR não abre nada">sem porta</span>
                  ) : v.syncSynced > 0 ? (
                    <span className="pill pill-success" title="QR ativo nas portas sincronizadas">
                      {v.syncSynced}/{v.syncTotal} porta(s)
                    </span>
                  ) : (
                    <span className="pill pill-warning" title="Ainda não enviado ao(s) controlador(es)">
                      pendente {v.syncPending}/{v.syncTotal}
                    </span>
                  )}
                </td>
                <td>
                  <div className="btn-group">
                    {!v.isRevoked && (
                      <>
                        <button className="btn btn-outline btn-sm" onClick={() => showQr(v.id, v.name)}>
                          Ver QR
                        </button>
                        <button
                          className="btn btn-outline btn-sm"
                          onClick={() => setChangingRoomId(changingRoomId === v.id ? null : v.id)}
                          title="Mudar o visitante de quarto: invalida o QR atual e gera um novo"
                        >
                          Trocar quarto
                        </button>
                        <button className="btn btn-danger-outline btn-sm" onClick={() => handleRevoke(v.id)}>
                          Revogar
                        </button>
                      </>
                    )}
                    <button className="btn btn-danger btn-sm" onClick={() => handleDelete(v.id, v.name)}>
                      Excluir
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
