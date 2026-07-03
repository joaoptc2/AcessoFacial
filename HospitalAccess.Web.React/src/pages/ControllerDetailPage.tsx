import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  api,
  ApiError,
  authHeaders,
  type AccessLogItem,
  type AlarmSettings,
  type ControllerDetailDto,
  type ControllerNetworkInfo,
  type EventPhotoListItem,
  type KioskSettings,
  type PersonnelAudit,
} from "../lib/api";

const TABS = ["Rede", "Relógio", "Alarmes", "Ajustes Locais", "Auditoria", "Fotos de Evento", "Log de Acessos", "Manutenção"] as const;
type Tab = (typeof TABS)[number];

export function ControllerDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [controller, setController] = useState<ControllerDetailDto | null>(null);
  const [activeTab, setActiveTab] = useState<Tab>("Rede");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [network, setNetwork] = useState<ControllerNetworkInfo | null>(null);
  const [clock, setClock] = useState<string | null>(null);
  const [alarmSettings, setAlarmSettings] = useState<AlarmSettings | null>(null);
  const [kiosk, setKiosk] = useState<KioskSettings | null>(null);
  const [audit, setAudit] = useState<PersonnelAudit | null>(null);
  const [photos, setPhotos] = useState<EventPhotoListItem[]>([]);
  const [photoImages, setPhotoImages] = useState<Record<string, string>>({});
  const [accessLog, setAccessLog] = useState<AccessLogItem[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  // Feedback local da aba Manutenção (fica ADJACENTE aos botões, não no topo da página).
  const [maintResult, setMaintResult] = useState<{ ok: boolean; message: string } | null>(null);

  useEffect(() => {
    if (!id) return;
    api.getController(id).then(setController);
    selectTab("Rede");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  async function selectTab(tab: Tab) {
    if (!id) return;
    setActiveTab(tab);
    setError(null);

    try {
      if (tab === "Rede" && !network) setNetwork(await api.getNetwork(id));
      else if (tab === "Relógio") setClock(await api.getClock(id));
      else if (tab === "Alarmes" && !alarmSettings) setAlarmSettings(await api.getAlarmSettings(id));
      else if (tab === "Ajustes Locais" && !kiosk) setKiosk(await api.getKioskSettings(id));
      else if (tab === "Log de Acessos") {
        const page = await api.queryAccessLog({ controllerId: id, page: 1, pageSize: 50 });
        setAccessLog(page.items);
      } else if (tab === "Fotos de Evento") {
        const list = await api.getEventPhotos(id);
        setPhotos(list);
        await loadPhotoImages(list);
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao carregar.");
    }
  }

  async function loadPhotoImages(list: EventPhotoListItem[]) {
    const updates: Record<string, string> = {};
    for (const p of list) {
      if (photoImages[p.id]) continue;
      try {
        const response = await fetch(api.eventPhotoImageUrl(p.id), { headers: authHeaders() });
        if (!response.ok) continue;
        const blob = await response.blob();
        updates[p.id] = URL.createObjectURL(blob);
      } catch {
        // ignore
      }
    }
    if (Object.keys(updates).length > 0) setPhotoImages((prev) => ({ ...prev, ...updates }));
  }

  async function saveNetwork() {
    if (!id || !network) return;
    setBusy(true);
    setError(null);
    try {
      await api.updateNetwork(id, network);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao salvar.");
    } finally {
      setBusy(false);
    }
  }

  async function syncClock() {
    if (!id) return;
    setBusy(true);
    setError(null);
    try {
      await api.syncClock(id);
      setClock(await api.getClock(id));
      setController(await api.getController(id));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao sincronizar.");
    } finally {
      setBusy(false);
    }
  }

  async function saveAlarmSettings() {
    if (!id || !alarmSettings) return;
    setBusy(true);
    setError(null);
    try {
      await api.updateAlarmSettings(id, alarmSettings);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao salvar.");
    } finally {
      setBusy(false);
    }
  }

  async function clearAlarm() {
    if (!id) return;
    setBusy(true);
    setError(null);
    try {
      await api.clearAlarm(id);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao silenciar.");
    } finally {
      setBusy(false);
    }
  }

  async function saveKiosk() {
    if (!id || !kiosk) return;
    setBusy(true);
    setError(null);
    try {
      await api.updateKioskSettings(id, kiosk);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao salvar.");
    } finally {
      setBusy(false);
    }
  }

  async function runAudit() {
    if (!id) return;
    setBusy(true);
    setError(null);
    try {
      setAudit(await api.getPersonnelAudit(id));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao comparar.");
    } finally {
      setBusy(false);
    }
  }

  async function downloadPhotos() {
    if (!id) return;
    setBusy(true);
    setError(null);
    try {
      await api.downloadEventPhotos(id, 20);
      const list = await api.getEventPhotos(id);
      setPhotos(list);
      await loadPhotoImages(list);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao baixar fotos.");
    } finally {
      setBusy(false);
    }
  }

  async function deleteExtraFromDevice(code: number) {
    if (!id) return;
    if (!window.confirm(`Excluir o usuário ${code} diretamente deste controlador?`)) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await api.deletePersonFromDevice(id, code);
      setNotice(`Usuário ${code} excluído do dispositivo.`);
      setAudit(await api.getPersonnelAudit(id));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao excluir.");
    } finally {
      setBusy(false);
    }
  }

  async function triggerFire() {
    if (!id) return;
    if (!window.confirm("Disparar o alarme de INCÊNDIO neste controlador?")) return;
    setBusy(true);
    setMaintResult(null);
    try {
      await api.triggerFireAlarm(id);
      setMaintResult({ ok: true, message: "✓ Alarme de incêndio disparado no controlador." });
    } catch (err) {
      setMaintResult({ ok: false, message: `✗ Falha ao disparar: ${err instanceof ApiError ? err.message : "erro inesperado"}` });
    } finally {
      setBusy(false);
    }
  }

  async function resyncAll() {
    if (!id) return;
    if (!window.confirm("Resincronizar FORÇADO: apaga TODAS as pessoas deste controlador e reenvia os cadastros do sistema. Confirmar?")) return;
    setBusy(true);
    setMaintResult(null);
    try {
      const r = await api.resyncAllController(id);
      setMaintResult({ ok: true, message: `✓ ${r.message ?? "Resincronização iniciada em segundo plano."}` });
    } catch (err) {
      setMaintResult({ ok: false, message: `✗ Falha ao resincronizar: ${err instanceof ApiError ? err.message : "erro inesperado"}` });
    } finally {
      setBusy(false);
    }
  }

  if (!controller) return <p className="text-muted">Carregando...</p>;

  return (
    <div>
      <h2>{controller.name}</h2>
      <p className="text-muted">
        {controller.ipAddress}:{controller.port} — SN <code>{controller.serialNumber}</code>
      </p>
      <Link to="/controllers" className="btn btn-outline btn-sm" style={{ marginBottom: "1rem", display: "inline-block" }}>
        ← Voltar para controladores
      </Link>

      <div className="btn-group" style={{ borderBottom: "1px solid var(--border-color)", marginBottom: "1rem", paddingBottom: "0.5rem" }}>
        {TABS.map((tab) => (
          <button
            key={tab}
            className="btn btn-sm"
            style={
              activeTab === tab
                ? { background: "var(--brand)", color: "#fff" }
                : { background: "transparent", border: "1px solid transparent", color: "var(--text-muted)" }
            }
            onClick={() => selectTab(tab)}
          >
            {tab}
          </button>
        ))}
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {notice && <div className="alert alert-success">{notice}</div>}

      {activeTab === "Rede" &&
        (network === null ? (
          <p className="text-muted">Carregando configuração de rede...</p>
        ) : (
          <div className="card" style={{ maxWidth: 640 }}>
            <div className="form-row" style={{ marginBottom: "0.75rem" }}>
              <div className="form-field">
                <label>IP</label>
                <input value={network.ip} onChange={(e) => setNetwork({ ...network, ip: e.target.value })} />
              </div>
              <div className="form-field">
                <label>Máscara</label>
                <input value={network.ipMask} onChange={(e) => setNetwork({ ...network, ipMask: e.target.value })} />
              </div>
              <div className="form-field">
                <label>Gateway</label>
                <input value={network.ipGateway} onChange={(e) => setNetwork({ ...network, ipGateway: e.target.value })} />
              </div>
              <div className="form-field">
                <label>MAC</label>
                <input value={network.mac} onChange={(e) => setNetwork({ ...network, mac: e.target.value })} />
              </div>
              <div className="form-field">
                <label>DNS</label>
                <input value={network.dns} onChange={(e) => setNetwork({ ...network, dns: e.target.value })} />
              </div>
              <div className="form-field">
                <label>DNS secundário</label>
                <input value={network.dnsBackup} onChange={(e) => setNetwork({ ...network, dnsBackup: e.target.value })} />
              </div>
              <div className="form-field">
                <label>Porta UDP</label>
                <input type="number" value={network.udpPort} onChange={(e) => setNetwork({ ...network, udpPort: Number(e.target.value) })} />
              </div>
              <div className="form-field">
                <label>IP do servidor (phone-home)</label>
                <input value={network.serverIp} onChange={(e) => setNetwork({ ...network, serverIp: e.target.value })} />
              </div>
              <div className="form-field">
                <label>Porta do servidor</label>
                <input
                  type="number"
                  value={network.serverPort}
                  onChange={(e) => setNetwork({ ...network, serverPort: Number(e.target.value) })}
                />
              </div>
              <div className="form-field" style={{ flexDirection: "row", alignItems: "center", gap: "0.4rem" }}>
                <input type="checkbox" id="autoIp" checked={network.autoIp} onChange={(e) => setNetwork({ ...network, autoIp: e.target.checked })} />
                <label htmlFor="autoIp" style={{ margin: 0 }}>
                  Obter IP automaticamente (DHCP)
                </label>
              </div>
            </div>
            <div className="alert" style={{ background: "#fffbeb", border: "1px solid #fde68a", color: "#92400e" }}>
              Cuidado: um IP incorreto torna o controlador inacessível pela rede até acesso físico.
            </div>
            <button className="btn btn-primary" onClick={saveNetwork} disabled={busy}>
              Salvar configuração de rede
            </button>
          </div>
        ))}

      {activeTab === "Relógio" && (
        <div className="card" style={{ maxWidth: 480 }}>
          <p>
            Horário do controlador: <strong>{clock ? new Date(clock).toLocaleString() : "—"}</strong>
          </p>
          <p>
            Horário deste servidor: <strong>{new Date().toLocaleString()}</strong>
          </p>
          <p className="text-muted">
            Última sincronização registrada: {controller.lastClockSyncAtUtc ? new Date(controller.lastClockSyncAtUtc).toLocaleString() : "nunca"}
          </p>
          <button className="btn btn-primary" onClick={syncClock} disabled={busy}>
            Sincronizar com o horário deste servidor
          </button>
        </div>
      )}

      {activeTab === "Alarmes" &&
        (alarmSettings === null ? (
          <p className="text-muted">Carregando configuração de alarmes...</p>
        ) : (
          <div className="card" style={{ maxWidth: 640 }}>
            <div className="form-row" style={{ marginBottom: "0.75rem" }}>
              <label style={{ display: "flex", alignItems: "center", gap: "0.4rem", fontWeight: 400 }}>
                <input
                  type="checkbox"
                  checked={alarmSettings.fireAlarmEnabled}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, fireAlarmEnabled: e.target.checked })}
                />
                Alarme de incêndio
              </label>
              <label style={{ display: "flex", alignItems: "center", gap: "0.4rem", fontWeight: 400 }}>
                <input
                  type="checkbox"
                  checked={alarmSettings.blacklistAlarmEnabled}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, blacklistAlarmEnabled: e.target.checked })}
                />
                Alarme de lista negra
              </label>
              <label style={{ display: "flex", alignItems: "center", gap: "0.4rem", fontWeight: 400 }}>
                <input
                  type="checkbox"
                  checked={alarmSettings.tamperAlarmEnabled}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, tamperAlarmEnabled: e.target.checked })}
                />
                Alarme de sabotagem (tamper)
              </label>
              <label style={{ display: "flex", alignItems: "center", gap: "0.4rem", fontWeight: 400 }}>
                <input
                  type="checkbox"
                  checked={alarmSettings.legalVerificationCloseAlarmEnabled}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, legalVerificationCloseAlarmEnabled: e.target.checked })}
                />
                Alarme em abertura por credencial válida
              </label>
              <label style={{ display: "flex", alignItems: "center", gap: "0.4rem", fontWeight: 400 }}>
                <input
                  type="checkbox"
                  checked={alarmSettings.illegalVerificationAlarmEnabled}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, illegalVerificationAlarmEnabled: e.target.checked })}
                />
                Alarme de credencial inválida
              </label>
              <div className="form-field" style={{ minWidth: 90 }}>
                <label>Tentativas</label>
                <input
                  type="number"
                  value={alarmSettings.illegalVerificationTimes}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, illegalVerificationTimes: Number(e.target.value) })}
                />
              </div>
              <label style={{ display: "flex", alignItems: "center", gap: "0.4rem", fontWeight: 400 }}>
                <input
                  type="checkbox"
                  checked={alarmSettings.duressAlarmEnabled}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, duressAlarmEnabled: e.target.checked })}
                />
                Alarme de coação (senha de pânico)
              </label>
              <div className="form-field">
                <label>Senha</label>
                <input
                  value={alarmSettings.duressPassword ?? ""}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, duressPassword: e.target.value })}
                />
              </div>
              <label style={{ display: "flex", alignItems: "center", gap: "0.4rem", fontWeight: 400 }}>
                <input
                  type="checkbox"
                  checked={alarmSettings.openDoorTimeoutAlarmEnabled}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, openDoorTimeoutAlarmEnabled: e.target.checked })}
                />
                Porta aberta além do tempo
              </label>
              <div className="form-field" style={{ minWidth: 90 }}>
                <label>Segundos</label>
                <input
                  type="number"
                  value={alarmSettings.openDoorTimeoutSeconds}
                  onChange={(e) => setAlarmSettings({ ...alarmSettings, openDoorTimeoutSeconds: Number(e.target.value) })}
                />
              </div>
            </div>
            <div className="btn-group">
              <button className="btn btn-primary" onClick={saveAlarmSettings} disabled={busy}>
                Salvar configuração de alarmes
              </button>
              <button className="btn btn-danger-outline" onClick={clearAlarm} disabled={busy}>
                Silenciar alarmes ativos
              </button>
              <Link to="/alarmevents" className="btn btn-outline">
                Ver log de alarmes
              </Link>
            </div>
          </div>
        ))}

      {activeTab === "Ajustes Locais" &&
        (kiosk === null ? (
          <p className="text-muted">Carregando ajustes locais...</p>
        ) : (
          <div className="card" style={{ maxWidth: 640 }}>
            <div className="form-row" style={{ marginBottom: "0.75rem" }}>
              {(
                [
                  ["language", "Idioma (código)"],
                  ["volume", "Volume (0-10)"],
                  ["fillLightMode", "Luz de preenchimento (modo)"],
                  ["maskDetectionMode", "Detecção de máscara (modo)"],
                  ["temperatureDetectionMode", "Detecção de temperatura (modo)"],
                  ["temperatureAlarmThresholdX10", "Limiar de alarme de temperatura (°C x10)"],
                  ["temperatureDisplayMode", "Exibição de temperatura (modo)"],
                  ["faceIdentifyRange", "Distância de reconhecimento facial"],
                  ["livenessDetectionMode", "Detecção de vida (modo)"],
                  ["livenessSimilarity", "Limiar de detecção de vida"],
                ] as const
              ).map(([key, label]) => (
                <div className="form-field" key={key}>
                  <label>{label}</label>
                  <input
                    type="number"
                    value={kiosk[key]}
                    onChange={(e) => setKiosk({ ...kiosk, [key]: Number(e.target.value) })}
                  />
                </div>
              ))}
            </div>
            <button className="btn btn-primary" onClick={saveKiosk} disabled={busy}>
              Salvar ajustes locais
            </button>
          </div>
        ))}

      {activeTab === "Auditoria" && (
        <div>
          <p className="text-muted">Compara os usuários efetivamente cadastrados no controlador com as permissões no nosso banco.</p>
          <button className="btn btn-primary" style={{ marginBottom: "1rem" }} onClick={runAudit} disabled={busy}>
            Comparar agora
          </button>
          {audit && (
            <>
              <p>
                No dispositivo: <strong>{audit.deviceCount}</strong> — Esperado no banco: <strong>{audit.expectedCount}</strong>
              </p>
              <div style={{ display: "flex", gap: "2rem" }}>
                <div>
                  <h4 style={{ fontSize: "0.85rem", textTransform: "uppercase", color: "var(--text-muted)" }}>Faltando no dispositivo</h4>
                  {audit.missingOnDevice.length === 0 ? (
                    <p className="text-muted">Nenhum.</p>
                  ) : (
                    <ul>
                      {audit.missingOnDevice.map((code) => (
                        <li key={code}>{code}</li>
                      ))}
                    </ul>
                  )}
                </div>
                <div>
                  <h4 style={{ fontSize: "0.85rem", textTransform: "uppercase", color: "var(--text-muted)" }}>Extra no dispositivo (não esperado)</h4>
                  {audit.extraOnDevice.length === 0 ? (
                    <p className="text-muted">Nenhum.</p>
                  ) : (
                    <ul style={{ listStyle: "none", paddingLeft: 0 }}>
                      {audit.extraOnDevice.map((code) => (
                        <li key={code} style={{ display: "flex", alignItems: "center", gap: "0.5rem", marginBottom: "0.3rem" }}>
                          <span>{code}</span>
                          <button className="btn btn-danger-outline btn-sm" disabled={busy} onClick={() => deleteExtraFromDevice(code)}>
                            Excluir do dispositivo
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              </div>
            </>
          )}
        </div>
      )}

      {activeTab === "Fotos de Evento" && (
        <div>
          <p className="text-muted">Baixa as fotos capturadas pelo controlador nos eventos de acesso mais recentes.</p>
          <button className="btn btn-primary" style={{ marginBottom: "1rem" }} onClick={downloadPhotos} disabled={busy}>
            Baixar fotos recentes
          </button>
          {photos.length === 0 ? (
            <p className="text-muted">Nenhuma foto baixada ainda.</p>
          ) : (
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(160px, 1fr))", gap: "0.75rem" }}>
              {photos.map((p) => (
                <div className="card" key={p.id} style={{ padding: "0.5rem" }}>
                  {photoImages[p.id] && (
                    <img src={photoImages[p.id]} alt="Foto do evento" style={{ width: "100%", borderRadius: "0.3rem" }} />
                  )}
                  <p style={{ fontSize: "0.8rem", margin: "0.4rem 0 0" }}>Usuário: {p.userCode ?? "?"}</p>
                  <p className="text-muted" style={{ fontSize: "0.75rem", margin: 0 }}>
                    {new Date(p.capturedAtUtc).toLocaleString()}
                  </p>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {activeTab === "Log de Acessos" && (
        <div>
          <p className="text-muted">Acessos mais recentes registrados NESTE controlador.</p>
          {accessLog.length === 0 ? (
            <p className="text-muted">Nenhum acesso registrado ainda.</p>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Data/hora</th>
                  <th>Usuário</th>
                  <th>Código</th>
                  <th>Método</th>
                  <th>Resultado</th>
                </tr>
              </thead>
              <tbody>
                {accessLog.map((l) => (
                  <tr key={l.id}>
                    <td>{new Date(l.timestampUtc).toLocaleString()}</td>
                    <td>{l.userName ?? "—"}</td>
                    <td>{l.userCode ?? "—"}</td>
                    <td>{l.method}</td>
                    <td>
                      <span className={l.granted ? "pill pill-success" : "pill pill-danger"}>
                        {l.granted ? "Concedido" : "Negado"}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <Link to={`/accesslog`} className="btn btn-outline btn-sm" style={{ marginTop: "0.75rem", display: "inline-block" }}>
            Ver log completo (com filtros)
          </Link>
        </div>
      )}

      {activeTab === "Manutenção" && (
        <div className="card" style={{ maxWidth: 640 }}>
          {maintResult && (
            <div className={maintResult.ok ? "alert alert-success" : "alert alert-danger"}>{maintResult.message}</div>
          )}

          <h4 style={{ marginTop: 0 }}>Resincronização forçada</h4>
          <p className="text-muted" style={{ marginTop: 0 }}>
            Apaga <strong>todas</strong> as pessoas deste controlador e reenvia os usuários cadastrados no
            sistema com permissão nele. Útil para corrigir divergências ou faces duplicadas.
          </p>
          <button className="btn btn-danger-outline" onClick={resyncAll} disabled={busy}>
            Resincronizar (forçar)
          </button>

          <hr className="divider" />

          <h4>Alarme de incêndio</h4>
          <p className="text-muted" style={{ marginTop: 0 }}>Dispara o alarme de incêndio neste controlador.</p>
          <button className="btn btn-danger" onClick={triggerFire} disabled={busy}>
            Disparar alarme de incêndio
          </button>
        </div>
      )}
    </div>
  );
}
