import { useEffect, useState, type FormEvent } from "react";
import { useAuth } from "../lib/AuthContext";
import { api, ApiError, type SystemSettingsDto } from "../lib/api";

const FIELDS: { key: keyof Pick<SystemSettingsDto,
  "eventPhotoRetentionDays" | "accessLogRetentionDays" | "alarmLogRetentionDays" | "controllerAuditRetentionDays">;
  label: string; help: string }[] = [
  { key: "eventPhotoRetentionDays", label: "Fotos de evento (dias)", help: "Fotos capturadas pelos controladores nos acessos." },
  { key: "accessLogRetentionDays", label: "Log de acessos (dias)", help: "Registros de entrada/saída. Auditoria hospitalar costuma exigir guarda longa." },
  { key: "alarmLogRetentionDays", label: "Log de alarmes (dias)", help: "Incêndio, coação, sabotagem, arrombamento etc." },
  { key: "controllerAuditRetentionDays", label: "Auditoria de comandos de porta (dias)", help: "Quem abriu/trancou cada porta." },
];

export function SettingsPage() {
  const { role } = useAuth();
  const [settings, setSettings] = useState<SystemSettingsDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.getSettings().then(setSettings).catch((err) => setError(err instanceof ApiError ? err.message : "Falha ao carregar."));
  }, []);

  if (role !== "Admin") {
    return <div className="alert alert-danger">Apenas administradores podem acessar as configurações do sistema.</div>;
  }
  if (error && !settings) return <div className="alert alert-danger">{error}</div>;
  if (!settings) return <p>Carregando...</p>;

  function setField(key: (typeof FIELDS)[number]["key"], value: number) {
    setSettings((prev) => (prev ? { ...prev, [key]: value } : prev));
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
      });
      setSavedAt(new Date().toLocaleString());
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao salvar.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <h2>Configurações do sistema</h2>
      <h3>Retenção de dados (LGPD)</h3>
      <p className="text-muted">
        Defina por quanto tempo cada tipo de dado é mantido. Um expurgo automático roda diariamente.{" "}
        <strong>0 = reter indefinidamente</strong> (sem expurgo). Fotos de face de usuários são removidas ao
        excluir o usuário, não por prazo.
      </p>

      <form className="card" style={{ maxWidth: 560 }} onSubmit={handleSave}>
        {FIELDS.map((f) => (
          <div className="form-field" key={f.key} style={{ marginBottom: "0.9rem" }}>
            <label>{f.label}</label>
            <input
              type="number"
              min={0}
              value={settings[f.key]}
              onChange={(e) => setField(f.key, Math.max(0, Number(e.target.value)))}
            />
            <span className="text-muted" style={{ fontSize: "0.85rem" }}>{f.help}</span>
          </div>
        ))}
        {error && <div className="alert alert-danger">{error}</div>}
        {savedAt && <div className="alert alert-success">Configurações salvas em {savedAt}.</div>}
        <button type="submit" className="btn btn-primary" disabled={busy}>
          Salvar
        </button>
      </form>
    </div>
  );
}
