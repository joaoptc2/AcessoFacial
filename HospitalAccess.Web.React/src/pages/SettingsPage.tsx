import { useEffect, useState, type FormEvent } from "react";
import { useAuth } from "../lib/AuthContext";
import { api, ApiError, type SystemSettingsDto } from "../lib/api";

const RETENTION_FIELDS: { key: keyof Pick<SystemSettingsDto,
  "eventPhotoRetentionDays" | "accessLogRetentionDays" | "alarmLogRetentionDays" | "controllerAuditRetentionDays">;
  label: string; help: string }[] = [
  { key: "eventPhotoRetentionDays", label: "Fotos de evento (dias)", help: "Fotos capturadas pelos controladores nos acessos." },
  { key: "accessLogRetentionDays", label: "Log de acessos (dias)", help: "Registros de entrada/saída. Auditoria hospitalar costuma exigir guarda longa." },
  { key: "alarmLogRetentionDays", label: "Log de alarmes (dias)", help: "Incêndio, coação, sabotagem, arrombamento etc." },
  { key: "controllerAuditRetentionDays", label: "Auditoria de comandos de porta (dias)", help: "Quem abriu/trancou cada porta." },
];

/**
 * Configurações do sistema — tudo administrável pela tela, sem linha de comando. Campo vazio
 * herda o valor do appsettings do servidor; segredos (senha dos aparelhos, token do HA) nunca
 * voltam do servidor: em branco = manter o atual.
 */
export function SettingsPage() {
  const { role } = useAuth();
  const [settings, setSettings] = useState<SystemSettingsDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Segredos digitados nesta sessão (nunca vêm do GET) + flags de limpeza explícita.
  const [devicePassword, setDevicePassword] = useState("");
  const [clearDevicePassword, setClearDevicePassword] = useState(false);
  const [haToken, setHaToken] = useState("");
  const [clearHaToken, setClearHaToken] = useState(false);
  const [haTest, setHaTest] = useState<{ ok: boolean; message: string } | null>(null);
  const [testingHa, setTestingHa] = useState(false);

  useEffect(() => {
    api.getSettings().then(setSettings).catch((err) => setError(err instanceof ApiError ? err.message : "Falha ao carregar."));
  }, []);

  if (role !== "Admin") {
    return <div className="alert alert-danger">Apenas administradores podem acessar as configurações do sistema.</div>;
  }
  if (error && !settings) return <div className="alert alert-danger">{error}</div>;
  if (!settings) return <p>Carregando...</p>;

  function patch(partial: Partial<SystemSettingsDto>) {
    setSettings((prev) => (prev ? { ...prev, ...partial } : prev));
  }

  async function handleSave(e: FormEvent) {
    e.preventDefault();
    if (!settings) return;
    setError(null);
    setSavedAt(null);
    setBusy(true);
    try {
      await api.updateSettings({
        eventPhotoRetentionDays: settings.eventPhotoRetentionDays,
        accessLogRetentionDays: settings.accessLogRetentionDays,
        alarmLogRetentionDays: settings.alarmLogRetentionDays,
        controllerAuditRetentionDays: settings.controllerAuditRetentionDays,
        qrFormat: settings.qrFormat,
        deviceDefaultPassword: devicePassword || undefined,
        clearDeviceDefaultPassword: clearDevicePassword,
        // "on"/"off" = decisão explícita; "" = herdar do appsettings do servidor.
        homeAssistantEnabled: settings.homeAssistantEnabled === null ? "" : settings.homeAssistantEnabled ? "on" : "off",
        homeAssistantBaseUrl: settings.homeAssistantBaseUrl,
        homeAssistantToken: haToken || undefined,
        clearHomeAssistantToken: clearHaToken,
        homeAssistantWelcomeService: settings.homeAssistantWelcomeService,
        homeAssistantClearService: settings.homeAssistantClearService,
        welcomeBaseImagePath: settings.welcomeBaseImagePath,
        welcomePublicBaseUrl: settings.welcomePublicBaseUrl,
        welcomeTextY: settings.welcomeTextY === null ? "" : String(settings.welcomeTextY),
        welcomeFontSize: settings.welcomeFontSize === null ? "" : String(settings.welcomeFontSize),
        welcomeFontColorHex: settings.welcomeFontColorHex,
      });
      setDevicePassword("");
      setClearDevicePassword(false);
      setHaToken("");
      setClearHaToken(false);
      setSavedAt(new Date().toLocaleString());
      // Recarrega os indicadores (hasToken/hasPassword/efetivo).
      setSettings(await api.getSettings());
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao salvar.");
    } finally {
      setBusy(false);
    }
  }

  async function testHomeAssistant() {
    setTestingHa(true);
    setHaTest(null);
    try {
      setHaTest(await api.testHomeAssistant());
    } catch (err) {
      setHaTest({ ok: false, message: err instanceof ApiError ? err.message : "Falha ao testar." });
    } finally {
      setTestingHa(false);
    }
  }

  return (
    <div>
      <h2>Configurações do sistema</h2>

      <form onSubmit={handleSave}>
        <div className="card" style={{ maxWidth: 620, marginBottom: "1.25rem" }}>
          <h3 style={{ marginTop: 0 }}>Dispositivos</h3>
          <div className="form-field" style={{ marginBottom: "0.5rem" }}>
            <label>Senha padrão dos aparelhos {settings.hasDeviceDefaultPassword && <span className="pill pill-success">definida</span>}</label>
            <input
              type="password"
              value={devicePassword}
              disabled={clearDevicePassword}
              onChange={(e) => setDevicePassword(e.target.value)}
              placeholder={settings.hasDeviceDefaultPassword ? "(manter a atual)" : "senha usada por todos os controladores"}
            />
            {settings.hasDeviceDefaultPassword && (
              <label style={{ display: "flex", alignItems: "center", gap: "0.35rem", fontWeight: "normal" }}>
                <input type="checkbox" checked={clearDevicePassword} onChange={(e) => setClearDevicePassword(e.target.checked)} />
                Limpar (volta a valer o appsettings do servidor)
              </label>
            )}
            <span className="text-muted" style={{ fontSize: "0.85rem" }}>
              Usada como senha de comunicação E do painel web de todos os controladores. Um aparelho
              com senha própria no cadastro (avançado) é exceção e tem precedência.
            </span>
          </div>
        </div>

        <div className="card" style={{ maxWidth: 620, marginBottom: "1.25rem" }}>
          <h3 style={{ marginTop: 0 }}>Home Assistant (gestão de leitos)</h3>
          <div className="form-row">
            <div className="form-field" style={{ minWidth: 170 }}>
              <label>Integração</label>
              <select
                value={settings.homeAssistantEnabled === null ? "" : settings.homeAssistantEnabled ? "on" : "off"}
                onChange={(e) =>
                  patch({ homeAssistantEnabled: e.target.value === "" ? null : e.target.value === "on" })
                }
              >
                <option value="on">Ligada</option>
                <option value="off">Desligada</option>
                <option value="">(herdar do servidor)</option>
              </select>
            </div>
            <div className="form-field" style={{ minWidth: 240 }}>
              <label>URL do Home Assistant</label>
              <input
                value={settings.homeAssistantBaseUrl}
                onChange={(e) => patch({ homeAssistantBaseUrl: e.target.value })}
                placeholder="http://homeassistant.local:8123"
              />
            </div>
            <div className="form-field" style={{ minWidth: 240 }}>
              <label>Token de longa duração {settings.hasHomeAssistantToken && <span className="pill pill-success">definido</span>}</label>
              <input
                type="password"
                value={haToken}
                disabled={clearHaToken}
                onChange={(e) => setHaToken(e.target.value)}
                placeholder={settings.hasHomeAssistantToken ? "(manter o atual)" : "perfil do HA → Segurança → criar token"}
              />
              {settings.hasHomeAssistantToken && (
                <label style={{ display: "flex", alignItems: "center", gap: "0.35rem", fontWeight: "normal" }}>
                  <input type="checkbox" checked={clearHaToken} onChange={(e) => setClearHaToken(e.target.checked)} />
                  Limpar token
                </label>
              )}
            </div>
            <div className="form-field" style={{ minWidth: 220 }}>
              <label>Serviço de boas-vindas</label>
              <input
                value={settings.homeAssistantWelcomeService}
                onChange={(e) => patch({ homeAssistantWelcomeService: e.target.value })}
                placeholder="script.boas_vindas_leito"
              />
            </div>
            <div className="form-field" style={{ minWidth: 220 }}>
              <label>Serviço de limpar TV (opcional)</label>
              <input
                value={settings.homeAssistantClearService}
                onChange={(e) => patch({ homeAssistantClearService: e.target.value })}
                placeholder="script.limpar_tv_leito"
              />
            </div>
          </div>
          <div className="btn-group" style={{ marginTop: "0.5rem" }}>
            <button type="button" className="btn btn-outline btn-sm" onClick={testHomeAssistant} disabled={testingHa}>
              {testingHa ? "Testando…" : "Testar conexão"}
            </button>
            {haTest && (
              <span className={haTest.ok ? "pill pill-success" : "pill pill-danger"}>{haTest.message}</span>
            )}
          </div>
          <p className="text-muted" style={{ fontSize: "0.85rem", marginBottom: 0 }}>
            Salve antes de testar — o teste usa a configuração efetiva (o que vale de fato:{" "}
            {settings.homeAssistantEffective.enabled ? "ligada" : "desligada"}
            {settings.homeAssistantEffective.baseUrl ? `, ${settings.homeAssistantEffective.baseUrl}` : ", sem URL"}).
          </p>
        </div>

        <div className="card" style={{ maxWidth: 620, marginBottom: "1.25rem" }}>
          <h3 style={{ marginTop: 0 }}>Tela de boas-vindas (leitos)</h3>
          <div className="form-row">
            <div className="form-field" style={{ minWidth: 280 }}>
              <label>Imagem base (caminho no servidor)</label>
              <input
                value={settings.welcomeBaseImagePath}
                onChange={(e) => patch({ welcomeBaseImagePath: e.target.value })}
                placeholder="/var/lib/hospitalaccess/welcome-base.jpg"
              />
            </div>
            <div className="form-field" style={{ minWidth: 240 }}>
              <label>URL pública deste servidor (para o HA)</label>
              <input
                value={settings.welcomePublicBaseUrl}
                onChange={(e) => patch({ welcomePublicBaseUrl: e.target.value })}
                placeholder="http://IP_DESTE_SERVIDOR"
              />
            </div>
            <div className="form-field" style={{ minWidth: 130 }}>
              <label>Posição Y do nome (px)</label>
              <input
                type="number"
                min={0}
                value={settings.welcomeTextY ?? ""}
                onChange={(e) => patch({ welcomeTextY: e.target.value === "" ? null : Number(e.target.value) })}
                placeholder="(padrão)"
              />
            </div>
            <div className="form-field" style={{ minWidth: 130 }}>
              <label>Tamanho da fonte</label>
              <input
                type="number"
                min={1}
                value={settings.welcomeFontSize ?? ""}
                onChange={(e) => patch({ welcomeFontSize: e.target.value === "" ? null : Number(e.target.value) })}
                placeholder="(padrão)"
              />
            </div>
            <div className="form-field" style={{ minWidth: 130 }}>
              <label>Cor do texto (hex)</label>
              <input
                value={settings.welcomeFontColorHex}
                onChange={(e) => patch({ welcomeFontColorHex: e.target.value })}
                placeholder="#FFFFFF"
              />
            </div>
          </div>
          <p className="text-muted" style={{ fontSize: "0.85rem", marginBottom: 0 }}>
            O nome do paciente é centralizado na horizontal na altura Y escolhida. Campos vazios
            herdam o appsettings do servidor. A mudança vale já na próxima internação/reexibição.
          </p>
        </div>

        <div className="card" style={{ maxWidth: 620, marginBottom: "1.25rem" }}>
          <h3 style={{ marginTop: 0 }}>Retenção de dados (LGPD)</h3>
          <p className="text-muted" style={{ marginTop: 0 }}>
            Expurgo automático diário. <strong>0 = reter indefinidamente</strong>. Fotos de face de
            usuários são removidas ao excluir o usuário, não por prazo.
          </p>
          {RETENTION_FIELDS.map((f) => (
            <div className="form-field" key={f.key} style={{ marginBottom: "0.9rem" }}>
              <label>{f.label}</label>
              <input
                type="number"
                min={0}
                value={settings[f.key]}
                onChange={(e) => patch({ [f.key]: Math.max(0, Number(e.target.value)) } as Partial<SystemSettingsDto>)}
              />
              <span className="text-muted" style={{ fontSize: "0.85rem" }}>{f.help}</span>
            </div>
          ))}
        </div>

        {error && <div className="alert alert-danger">{error}</div>}
        {savedAt && <div className="alert alert-success">Configurações salvas em {savedAt} — já valem para todo o sistema, sem reiniciar.</div>}
        <button type="submit" className="btn btn-primary" disabled={busy}>
          Salvar configurações
        </button>
      </form>
    </div>
  );
}
