import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  api,
  ApiError,
  authHeaders,
  type AlarmSettings,
  type ControllerDetailDto,
  type ControllerNetworkInfo,
  type EventPhotoListItem,
  type KioskSettings,
  type PersonnelAudit,
} from "../lib/api";

const TABS = ["Rede", "Relógio", "Alarmes", "Ajustes Locais", "Auditoria", "Fotos de Evento"] as const;
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
      else if (tab === "Fotos de Evento") {
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
                    <ul>
                      {audit.extraOnDevice.map((code) => (
                        <li key={code}>{code}</li>
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
    </div>
  );
}
