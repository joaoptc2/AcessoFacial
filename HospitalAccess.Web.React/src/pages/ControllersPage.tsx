import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, ApiError, type ControllerConnectionMode, type ControllerDto, type SyncStatusDto } from "../lib/api";
import { Modal } from "../components/Modal";
import { useAuth } from "../lib/AuthContext";

interface FormModel {
  name: string;
  ipAddress: string;
  port: number;
  serialNumber: string;
  communicationPassword: string;
  supportsWaitRepeatMessage: boolean;
  connectionMode: ControllerConnectionMode;
  timeoutMs: number;
  restartCount: number;
  apiBaseUrl: string;
  apiPassword: string;
  homeAssistantRoomId: string;
  isRoom: boolean;
  useDefaultPasswords: boolean;
}

const emptyForm: FormModel = {
  name: "",
  ipAddress: "",
  port: 8000,
  serialNumber: "",
  communicationPassword: "", // vazio = usa a senha padrão dos aparelhos (Configurações)
  supportsWaitRepeatMessage: false,
  connectionMode: "TcpClient",
  timeoutMs: 3000,
  restartCount: 3,
  apiBaseUrl: "",
  apiPassword: "",
  homeAssistantRoomId: "",
  isRoom: false,
  useDefaultPasswords: false,
};

/** Aceita "192.168.19.20", "http://192.168.19.20/" ou a URL completa do painel — extrai só o host. */
function normalizeIp(input: string): string {
  let value = input.trim();
  value = value.replace(/^https?:\/\//i, "");
  const slash = value.indexOf("/");
  if (slash >= 0) value = value.slice(0, slash);
  const colon = value.indexOf(":");
  if (colon >= 0) value = value.slice(0, colon);
  return value;
}

export function ControllersPage() {
  const { role } = useAuth();
  const isAdminOrOperator = role === "Admin" || role === "Operator";

  const [controllers, setControllers] = useState<ControllerDto[]>([]);
  const [search, setSearch] = useState("");
  const [form, setForm] = useState<FormModel>(emptyForm);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [editingOriginalIp, setEditingOriginalIp] = useState("");
  const [detectingSn, setDetectingSn] = useState(false);
  const [discovering, setDiscovering] = useState(false);
  const [discovered, setDiscovered] = useState<{ serialNumber: string; ipAddress: string }[] | null>(null);

  const [modalController, setModalController] = useState<ControllerDto | null>(null);
  const [doorResult, setDoorResult] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<string | null>(null);
  const [syncStatuses, setSyncStatuses] = useState<SyncStatusDto[]>([]);

  const load = async () => setControllers(await api.getControllers());

  useEffect(() => {
    load();
  }, []);

  const filtered = useMemo(
    () =>
      controllers.filter(
        (c) =>
          !search ||
          c.name.toLowerCase().includes(search.toLowerCase()) ||
          c.ipAddress.includes(search) ||
          c.serialNumber.includes(search),
      ),
    [controllers, search],
  );

  async function handleSave(e: FormEvent) {
    e.preventDefault();
    setFormError(null);
    const ip = normalizeIp(form.ipAddress);
    try {
      if (editingId === null) {
        // Cadastro enxuto: porta/painel/senhas/modo têm defaults no servidor — o avançado só
        // vai junto se foi mexido (senha vazia = usar a padrão global das Configurações).
        await api.createController({
          name: form.name,
          ipAddress: ip,
          serialNumber: form.serialNumber,
          port: form.port,
          communicationPassword: form.communicationPassword || undefined,
          supportsWaitRepeatMessage: form.supportsWaitRepeatMessage,
          connectionMode: form.connectionMode,
          apiBaseUrl: form.apiBaseUrl || undefined,
          apiPassword: form.apiPassword || undefined,
          homeAssistantRoomId: form.homeAssistantRoomId || undefined,
          isRoom: form.isRoom,
        });
      } else {
        // Se a URL do painel era a derivada do IP antigo (http://IP) e o IP mudou, acompanha —
        // senão o QR/painel continuaria apontando para o endereço velho.
        const apiBaseUrl =
          editingOriginalIp && ip !== editingOriginalIp && form.apiBaseUrl === `http://${editingOriginalIp}`
            ? `http://${ip}`
            : form.apiBaseUrl;
        // Senha em branco = mantém a atual (a API não a devolve).
        await api.updateController(editingId, {
          name: form.name,
          ipAddress: ip,
          port: form.port,
          serialNumber: form.serialNumber,
          communicationPassword: form.communicationPassword ? form.communicationPassword : undefined,
          supportsWaitRepeatMessage: form.supportsWaitRepeatMessage,
          connectionMode: form.connectionMode,
          timeoutMs: form.timeoutMs,
          restartCount: form.restartCount,
          apiBaseUrl: apiBaseUrl || undefined,
          apiPassword: form.apiPassword ? form.apiPassword : undefined,
          homeAssistantRoomId: form.homeAssistantRoomId || undefined,
          isRoom: form.isRoom,
          useDefaultPasswords: form.useDefaultPasswords,
        });
      }
      cancelEdit();
      await load();
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : "Falha inesperada ao salvar.");
    }
  }

  /** Preenche o SN via varredura UDP, casando pelo IP digitado. */
  async function detectSerialNumber() {
    const ip = normalizeIp(form.ipAddress);
    if (!ip) {
      setFormError("Informe o IP antes de detectar o SN.");
      return;
    }
    setDetectingSn(true);
    setFormError(null);
    try {
      const found = await api.discoverControllers();
      const match = found.find((d) => d.ipAddress === ip);
      if (match) setForm((f) => ({ ...f, serialNumber: match.serialNumber }));
      else
        setFormError(
          found.length === 0
            ? "Nenhum controlador respondeu à varredura — digite o SN manualmente (está na etiqueta/painel do aparelho)."
            : `Nenhum aparelho com IP ${ip} respondeu (${found.length} outro(s) encontrado(s)) — confira o IP ou digite o SN manualmente.`,
        );
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : "Falha na varredura de rede.");
    } finally {
      setDetectingSn(false);
    }
  }

  async function startEdit(id: string) {
    const detail = await api.getController(id);
    setEditingId(id);
    setEditingOriginalIp(detail.ipAddress);
    setShowAdvanced(false);
    setForm({
      name: detail.name,
      ipAddress: detail.ipAddress,
      port: detail.port,
      serialNumber: detail.serialNumber,
      communicationPassword: "", // não retornada pela API; em branco = manter a atual
      supportsWaitRepeatMessage: detail.supportsWaitRepeatMessage,
      connectionMode: detail.connectionMode,
      timeoutMs: detail.timeoutMs,
      restartCount: detail.restartCount,
      apiBaseUrl: detail.apiBaseUrl ?? "",
      apiPassword: "", // não retornada pela API; em branco = manter a atual
      homeAssistantRoomId: detail.homeAssistantRoomId ?? "",
      isRoom: detail.isRoom,
      useDefaultPasswords: false,
    });
  }

  function cancelEdit() {
    setEditingId(null);
    setEditingOriginalIp("");
    setShowAdvanced(false);
    setForm(emptyForm);
  }

  async function handleDelete(c: ControllerDto) {
    if (!window.confirm(`Excluir o controlador "${c.name}" (${c.ipAddress})? Esta ação não pode ser desfeita.`)) return;
    setFormError(null);
    try {
      await api.deleteController(c.id);
      await load();
    } catch (err) {
      setFormError(err instanceof ApiError ? err.message : "Falha inesperada ao excluir.");
    }
  }

  async function handleDiscover() {
    setDiscovering(true);
    setDiscovered(null);
    try {
      setDiscovered(await api.discoverControllers());
    } finally {
      setDiscovering(false);
    }
  }

  function openModal(controller: ControllerDto) {
    setModalController(controller);
    setDoorResult(null);
    setTestResult(null);
    setSyncStatuses([]);
  }

  function closeModal() {
    setModalController(null);
    setDoorResult(null);
    setTestResult(null);
    setSyncStatuses([]);
  }

  async function runDoorCommand(command: (id: string) => Promise<void>) {
    if (!modalController) return;
    setTestResult(null);
    setSyncStatuses([]);
    try {
      await command(modalController.id);
      setDoorResult("Comando enviado com sucesso.");
    } catch (err) {
      setDoorResult(`Falha: ${err instanceof ApiError ? err.message : "erro inesperado"}`);
    }
  }

  async function runTestConnection() {
    if (!modalController) return;
    setSyncStatuses([]);
    setDoorResult(null);
    try {
      const result = await api.testConnection(modalController.id);
      setTestResult(
        result.matchesRegistered
          ? `OK: SN reportado ${result.reportedSerialNumber} confere.`
          : `Divergência: SN reportado ${result.reportedSerialNumber}.`,
      );
    } catch (err) {
      setTestResult(`Falha: ${err instanceof ApiError ? err.message : "erro inesperado"}`);
    }
  }

  async function runSyncStatus() {
    if (!modalController) return;
    setTestResult(null);
    setDoorResult(null);
    setSyncStatuses(await api.getSyncStatus(modalController.id));
  }

  async function resolveConflict(userId: string, action: "replace" | "keep") {
    if (!modalController) return;
    try {
      if (action === "replace") {
        await api.resolveConflictReplace(userId, modalController.id);
        setDoorResult("Substituição iniciada — a sincronização será refeita.");
      } else {
        await api.resolveConflictKeepExisting(userId, modalController.id);
        setDoorResult("Mantido o usuário existente; este envio foi cancelado.");
      }
      setSyncStatuses(await api.getSyncStatus(modalController.id));
    } catch (err) {
      setDoorResult(`Falha: ${err instanceof ApiError ? err.message : "erro inesperado"}`);
    }
  }

  function syncBadgeClass(state: string) {
    switch (state) {
      case "Synced":
        return "pill pill-success";
      case "Failed":
        return "pill pill-danger";
      case "Revoked":
        return "pill";
      default:
        return "pill pill-warning";
    }
  }

  return (
    <div>
      <h2>Controladores / Portas</h2>

      {isAdminOrOperator && (
        <>
          <button className="btn btn-outline btn-sm" onClick={handleDiscover} disabled={discovering}>
            {discovering ? "Procurando..." : "Descobrir controladores na rede"}
          </button>
          {discovered && (
            <p className="text-muted" style={{ marginTop: "0.5rem" }}>
              {discovered.length === 0
                ? "Nenhum controlador respondeu à varredura (esperado sem hardware real acessível)."
                : discovered.map((d) => `${d.serialNumber} — ${d.ipAddress}`).join(", ")}
            </p>
          )}
        </>
      )}

      {isAdminOrOperator && (
        <form className="card" style={{ marginTop: "1rem", marginBottom: "1.25rem" }} onSubmit={handleSave}>
          <div className="form-row">
            <div className="form-field">
              <label>Nome</label>
              <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="Ex.: Quarto 101" required />
            </div>
            <div className="form-field" style={{ minWidth: 180 }}>
              <label>IP (o mesmo do painel web)</label>
              <input
                value={form.ipAddress}
                onChange={(e) => setForm({ ...form, ipAddress: e.target.value })}
                placeholder="192.168.19.20"
                required
              />
            </div>
            <div className="form-field" style={{ minWidth: 220 }}>
              <label>SN (16 dígitos)</label>
              <div style={{ display: "flex", gap: "0.4rem" }}>
                <input
                  value={form.serialNumber}
                  onChange={(e) => setForm({ ...form, serialNumber: e.target.value })}
                  required
                  style={{ flex: 1 }}
                />
                <button type="button" className="btn btn-outline btn-sm" onClick={detectSerialNumber} disabled={detectingSn}>
                  {detectingSn ? "Procurando…" : "Detectar"}
                </button>
              </div>
            </div>
            <div className="form-field" style={{ flexDirection: "row", alignItems: "center", gap: "0.4rem" }}>
              <input
                type="checkbox"
                id="isRoom"
                checked={form.isRoom}
                onChange={(e) => setForm({ ...form, isRoom: e.target.checked })}
              />
              <label htmlFor="isRoom" style={{ margin: 0 }}>
                É quarto/leito
              </label>
            </div>
            {form.isRoom && (
              <div className="form-field" style={{ minWidth: 160 }}>
                <label>Quarto no Home Assistant</label>
                <input
                  placeholder="quarto_101"
                  value={form.homeAssistantRoomId}
                  onChange={(e) => setForm({ ...form, homeAssistantRoomId: e.target.value })}
                />
              </div>
            )}
            <div className="form-field">
              <button type="submit" className="btn btn-primary">
                {editingId === null ? "Adicionar" : "Salvar"}
              </button>
            </div>
            {editingId !== null && (
              <div className="form-field">
                <button type="button" className="btn btn-outline" onClick={cancelEdit}>
                  Cancelar
                </button>
              </div>
            )}
            <div className="form-field">
              <button type="button" className="btn btn-outline btn-sm" onClick={() => setShowAdvanced(!showAdvanced)}>
                {showAdvanced ? "Ocultar avançado" : "Avançado…"}
              </button>
            </div>
          </div>
          {editingId === null && !showAdvanced && (
            <p className="text-muted" style={{ margin: "0.5rem 0 0" }}>
              Porta 8000, painel web em http://IP e as senhas padrão dos aparelhos (Configurações;
              fábrica FFFFFFFF/1409) são assumidas automaticamente — use "Avançado…" só para exceções.
            </p>
          )}
          {showAdvanced && (
            <div className="form-row" style={{ marginTop: "0.75rem", paddingTop: "0.75rem", borderTop: "1px solid var(--border, #e2e8f0)" }}>
              <div className="form-field" style={{ minWidth: 80 }}>
                <label>Porta</label>
                <input type="number" value={form.port} onChange={(e) => setForm({ ...form, port: Number(e.target.value) })} required />
              </div>
              <div className="form-field">
                <label>Senha própria (8 hex)</label>
                <input
                  value={form.communicationPassword}
                  disabled={form.useDefaultPasswords}
                  onChange={(e) => setForm({ ...form, communicationPassword: e.target.value })}
                  placeholder={editingId !== null ? "(manter atual)" : "(usar a padrão global)"}
                />
              </div>
              <div className="form-field" style={{ minWidth: 150 }}>
                <label>Modo de conexão</label>
                <select
                  value={form.connectionMode}
                  onChange={(e) => setForm({ ...form, connectionMode: e.target.value as ControllerConnectionMode })}
                >
                  <option value="TcpClient">TCP (servidor disca)</option>
                  <option value="TcpServerClient">TCP phone-home</option>
                  <option value="Udp">UDP</option>
                </select>
              </div>
              {editingId !== null && (
                <>
                  <div className="form-field" style={{ minWidth: 100 }}>
                    <label>Timeout (ms)</label>
                    <input type="number" value={form.timeoutMs} onChange={(e) => setForm({ ...form, timeoutMs: Number(e.target.value) })} required />
                  </div>
                  <div className="form-field" style={{ minWidth: 80 }}>
                    <label>Retries</label>
                    <input type="number" value={form.restartCount} onChange={(e) => setForm({ ...form, restartCount: Number(e.target.value) })} required />
                  </div>
                </>
              )}
              <div className="form-field" style={{ flexDirection: "row", alignItems: "center", gap: "0.4rem" }}>
                <input
                  type="checkbox"
                  id="waitRepeat"
                  checked={form.supportsWaitRepeatMessage}
                  onChange={(e) => setForm({ ...form, supportsWaitRepeatMessage: e.target.checked })}
                />
                <label htmlFor="waitRepeat" style={{ margin: 0 }}>
                  Firmware &gt;= v4.28
                </label>
              </div>
              <div className="form-field" style={{ minWidth: 200 }}>
                <label>URL do painel web (QR)</label>
                <input
                  placeholder="(automático: http://IP)"
                  value={form.apiBaseUrl}
                  onChange={(e) => setForm({ ...form, apiBaseUrl: e.target.value })}
                />
              </div>
              <div className="form-field" style={{ minWidth: 150 }}>
                <label>Senha própria do painel</label>
                <input
                  type="password"
                  placeholder={editingId !== null ? "(manter atual)" : "(usar a padrão global)"}
                  value={form.apiPassword}
                  disabled={form.useDefaultPasswords}
                  onChange={(e) => setForm({ ...form, apiPassword: e.target.value })}
                />
              </div>
              {editingId !== null && (
                <div className="form-field" style={{ flexDirection: "row", alignItems: "center", gap: "0.4rem" }}>
                  <input
                    type="checkbox"
                    id="useDefaultPw"
                    checked={form.useDefaultPasswords}
                    onChange={(e) =>
                      // Marcar limpa as senhas digitadas — evita "voltar à padrão" e trocar a
                      // senha própria no MESMO salvar (uma anularia a outra em silêncio).
                      setForm({
                        ...form,
                        useDefaultPasswords: e.target.checked,
                        communicationPassword: e.target.checked ? "" : form.communicationPassword,
                        apiPassword: e.target.checked ? "" : form.apiPassword,
                      })
                    }
                  />
                  <label htmlFor="useDefaultPw" style={{ margin: 0 }} title="Limpa as senhas próprias deste aparelho — ele volta a usar a senha padrão das Configurações.">
                    Voltar à senha padrão global
                  </label>
                </div>
              )}
            </div>
          )}
        </form>
      )}

      {formError && <div className="alert alert-danger">{formError}</div>}

      <div className="card" style={{ marginBottom: "1rem" }}>
        <div className="form-field">
          <label>Buscar</label>
          <input placeholder="Nome, IP ou SN..." value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
      </div>

      <table>
        <thead>
          <tr>
            <th>Nome</th>
            <th>IP:Porta</th>
            <th>SN</th>
            <th>Usuários</th>
            <th>Ações</th>
          </tr>
        </thead>
        <tbody>
          {filtered.map((c) => (
            <tr key={c.id}>
              <td>
                {c.name} {c.isRoom && <span className="pill">Quarto</span>}
              </td>
              <td>
                {c.ipAddress}:{c.port}
              </td>
              <td>
                <code>{c.serialNumber}</code>
              </td>
              <td>
                <span className="pill">{c.userCount}</span>
              </td>
              <td>
                <div className="btn-group">
                  <button className="btn btn-primary btn-sm" onClick={() => openModal(c)}>
                    Controlar
                  </button>
                  <Link to={`/controllers/${c.id}`} className="btn btn-outline btn-sm">
                    Detalhes
                  </Link>
                  {isAdminOrOperator && (
                    <>
                      <button className="btn btn-outline btn-sm" onClick={() => startEdit(c.id)}>
                        Editar
                      </button>
                      <button className="btn btn-danger-outline btn-sm" onClick={() => handleDelete(c)}>
                        Excluir
                      </button>
                    </>
                  )}
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {modalController && (
        <Modal title={modalController.name} onClose={closeModal}>
          <p className="text-muted" style={{ marginTop: 0 }}>
            <code>
              {modalController.ipAddress}:{modalController.port}
            </code>{" "}
            — SN <code>{modalController.serialNumber}</code>
          </p>

          <h4>Comandos de porta</h4>
          <div className="btn-group" style={{ marginBottom: "0.75rem" }}>
            <button className="btn btn-outline btn-sm" style={{ borderColor: "#bbf7d0", color: "var(--success)" }} onClick={() => runDoorCommand(api.openDoor)}>
              Abrir
            </button>
            <button className="btn btn-outline btn-sm" onClick={() => runDoorCommand(api.closeDoor)}>
              Fechar
            </button>
            {isAdminOrOperator && (
              <>
                <button className="btn btn-outline btn-sm" style={{ borderColor: "#fde68a", color: "var(--warning)" }} onClick={() => runDoorCommand(api.holdDoorOpen)}>
                  Manter aberta
                </button>
                <button className="btn btn-danger-outline btn-sm" onClick={() => runDoorCommand(api.lockDoor)}>
                  Trancar
                </button>
                <button className="btn btn-outline btn-sm" onClick={() => runDoorCommand(api.unlockDoor)}>
                  Destrancar
                </button>
              </>
            )}
          </div>
          {doorResult && <p>{doorResult}</p>}

          <hr className="divider" />

          <h4>Diagnóstico</h4>
          <div className="btn-group" style={{ marginBottom: "0.5rem" }}>
            <button className="btn btn-outline btn-sm" onClick={runTestConnection}>
              Testar conexão
            </button>
            <button className="btn btn-outline btn-sm" onClick={runSyncStatus}>
              Status de sincronização
            </button>
          </div>
          {testResult && <p>{testResult}</p>}
          {syncStatuses.length > 0 && (
            <ul style={{ listStyle: "none", paddingLeft: 0, margin: 0 }}>
              {syncStatuses.map((s) => (
                <li key={s.userId} style={{ marginBottom: "0.4rem" }}>
                  {s.userName} — <span className={syncBadgeClass(s.state)}>{s.state}</span> (retries: {s.retryCount})
                  {s.lastError ? ` — ${s.lastError}` : ""}
                  {isAdminOrOperator && s.conflictUserCode != null && (
                    <div className="btn-group" style={{ marginTop: "0.3rem" }}>
                      <button className="btn btn-danger-outline btn-sm" onClick={() => resolveConflict(s.userId, "replace")}>
                        Substituir (excluir o existente e enviar este)
                      </button>
                      <button className="btn btn-outline btn-sm" onClick={() => resolveConflict(s.userId, "keep")}>
                        Manter o existente
                      </button>
                    </div>
                  )}
                </li>
              ))}
            </ul>
          )}
        </Modal>
      )}
    </div>
  );
}
