import { useEffect, useState, type FormEvent } from "react";
import { useAuth } from "../lib/AuthContext";
import { api, ApiError, downloadBlob, type BackupListDto, type SystemSettingsDto } from "../lib/api";

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

  // Segredos digitados nesta sessão (nunca vêm do GET — write-only) + flags de limpeza.
  const [commPassword, setCommPassword] = useState("");
  const [clearCommPassword, setClearCommPassword] = useState(false);
  const [apiPassword, setApiPassword] = useState("");
  const [clearApiPassword, setClearApiPassword] = useState(false);
  const [haToken, setHaToken] = useState("");
  const [clearHaToken, setClearHaToken] = useState(false);
  const [haTest, setHaTest] = useState<{ ok: boolean; message: string } | null>(null);
  const [testingHa, setTestingHa] = useState(false);

  // Upload/prévia da imagem de boas-vindas.
  const [uploadFile, setUploadFile] = useState<File | null>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadInfo, setUploadInfo] = useState<string | null>(null);
  const [fontFile, setFontFile] = useState<File | null>(null);
  const [uploadingFont, setUploadingFont] = useState(false);
  const [fontInfo, setFontInfo] = useState<string | null>(null);
  const [previewName, setPreviewName] = useState("Maria da Silva");
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [previewing, setPreviewing] = useState(false);

  // Cópias de segurança.
  const [backups, setBackups] = useState<BackupListDto | null>(null);
  const [backupBusy, setBackupBusy] = useState(false);
  const [backupError, setBackupError] = useState<string | null>(null);
  const [backupInfo, setBackupInfo] = useState<string | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);

  useEffect(() => {
    api.getSettings().then(setSettings).catch((err) => setError(err instanceof ApiError ? err.message : "Falha ao carregar."));
    api.getBackups().then(setBackups).catch(() => setBackups(null));
  }, []);

  if (role !== "Admin") {
    return <div className="alert alert-danger">Apenas administradores podem acessar as configurações do sistema.</div>;
  }
  if (error && !settings) return <div className="alert alert-danger">{error}</div>;
  if (!settings) return <p>Carregando...</p>;

  function patch(partial: Partial<SystemSettingsDto>) {
    setSettings((prev) => (prev ? { ...prev, ...partial } : prev));
  }

  async function refreshBackups() {
    try {
      setBackups(await api.getBackups());
    } catch (err) {
      setBackupError(err instanceof ApiError ? err.message : "Falha ao listar as cópias.");
    }
  }

  async function handleCreateBackup() {
    setBackupBusy(true);
    setBackupError(null);
    setBackupInfo(null);
    try {
      const file = await api.createBackup();
      setBackupInfo(`Cópia "${file.fileName}" gerada (${formatBytes(file.sizeBytes)}).`);
      await refreshBackups();
    } catch (err) {
      setBackupError(err instanceof ApiError ? err.message : "Falha ao gerar a cópia de segurança.");
    } finally {
      setBackupBusy(false);
    }
  }

  async function handleDownloadBackup(fileName: string) {
    setBackupError(null);
    try {
      downloadBlob(await api.downloadBackup(fileName), fileName);
    } catch (err) {
      setBackupError(err instanceof ApiError ? err.message : "Falha ao baixar a cópia.");
    }
  }

  async function handleDeleteBackup(fileName: string) {
    if (!confirm(`Remover a cópia "${fileName}" definitivamente?`)) return;
    setBackupError(null);
    setBackupInfo(null);
    try {
      await api.deleteBackup(fileName);
      await refreshBackups();
    } catch (err) {
      setBackupError(err instanceof ApiError ? err.message : "Falha ao remover a cópia.");
    }
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
        deviceDefaultCommunicationPassword: commPassword || undefined,
        clearDeviceDefaultCommunicationPassword: clearCommPassword,
        deviceDefaultApiPassword: apiPassword || undefined,
        clearDeviceDefaultApiPassword: clearApiPassword,
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
        welcomeFontPath: settings.welcomeFontPath,
      });
      setCommPassword("");
      setClearCommPassword(false);
      setApiPassword("");
      setClearApiPassword(false);
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

  async function uploadWelcome() {
    if (!uploadFile) return;
    setUploading(true);
    setUploadInfo(null);
    setPreviewError(null);
    try {
      const result = await api.uploadWelcomeImage(uploadFile);
      setUploadInfo(`Imagem base atualizada (${result.width}×${result.height}) — já vale para as próximas boas-vindas.`);
      setUploadFile(null);
      setSettings(await api.getSettings()); // o caminho da base foi gravado no servidor
    } catch (err) {
      setPreviewError(err instanceof ApiError ? err.message : "Falha ao enviar a imagem.");
    } finally {
      setUploading(false);
    }
  }

  async function uploadFont() {
    if (!fontFile) return;
    setUploadingFont(true);
    setFontInfo(null);
    setPreviewError(null);
    try {
      const result = await api.uploadWelcomeFont(fontFile);
      setFontInfo(`Fonte "${result.familyName}" instalada — já vale para as próximas boas-vindas e prévias.`);
      setFontFile(null);
      setSettings(await api.getSettings());
    } catch (err) {
      setPreviewError(err instanceof ApiError ? err.message : "Falha ao enviar a fonte.");
    } finally {
      setUploadingFont(false);
    }
  }

  async function generatePreview() {
    setPreviewing(true);
    setPreviewError(null);
    try {
      const blob = await api.previewWelcomeImage(previewName.trim() || "Maria da Silva");
      setPreviewUrl((prev) => {
        if (prev) URL.revokeObjectURL(prev);
        return URL.createObjectURL(blob);
      });
    } catch (err) {
      setPreviewError(err instanceof ApiError ? err.message : "Falha ao gerar a prévia.");
    } finally {
      setPreviewing(false);
    }
  }

  return (
    <div>
      <h2>Configurações do sistema</h2>

      <form onSubmit={handleSave}>
        <div className="card" style={{ maxWidth: 620, marginBottom: "1.25rem" }}>
          <h3 style={{ marginTop: 0 }}>Dispositivos — senhas padrão</h3>
          <p className="text-muted" style={{ marginTop: 0, fontSize: "0.85rem" }}>
            Valem para todos os controladores sem senha própria no cadastro. Por segurança os
            valores <strong>não são reexibidos</strong> — o selo "definida" confirma que estão
            salvos. Sem nada definido, valem os padrões de fábrica.
          </p>
          <div className="form-row">
            <div className="form-field" style={{ minWidth: 250 }}>
              <label>
                Senha de comunicação (fábrica: FFFFFFFF){" "}
                {settings.hasDeviceDefaultCommunicationPassword && <span className="pill pill-success">definida</span>}
              </label>
              <input
                type="password"
                value={commPassword}
                disabled={clearCommPassword}
                onChange={(e) => setCommPassword(e.target.value)}
                placeholder={settings.hasDeviceDefaultCommunicationPassword ? "(manter a atual)" : "(usar padrão de fábrica)"}
              />
              {settings.hasDeviceDefaultCommunicationPassword && (
                <label style={{ display: "flex", alignItems: "center", gap: "0.35rem", fontWeight: "normal" }}>
                  <input type="checkbox" checked={clearCommPassword} onChange={(e) => setClearCommPassword(e.target.checked)} />
                  Limpar (volta ao padrão de fábrica)
                </label>
              )}
            </div>
            <div className="form-field" style={{ minWidth: 250 }}>
              <label>
                Senha do painel web (fábrica: 1409){" "}
                {settings.hasDeviceDefaultApiPassword && <span className="pill pill-success">definida</span>}
              </label>
              <input
                type="password"
                value={apiPassword}
                disabled={clearApiPassword}
                onChange={(e) => setApiPassword(e.target.value)}
                placeholder={settings.hasDeviceDefaultApiPassword ? "(manter a atual)" : "(usar padrão de fábrica)"}
              />
              {settings.hasDeviceDefaultApiPassword && (
                <label style={{ display: "flex", alignItems: "center", gap: "0.35rem", fontWeight: "normal" }}>
                  <input type="checkbox" checked={clearApiPassword} onChange={(e) => setClearApiPassword(e.target.checked)} />
                  Limpar (volta ao padrão de fábrica)
                </label>
              )}
            </div>
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

          <hr className="divider" />
          <h4 style={{ marginTop: 0 }}>Imagem base</h4>
          <div className="form-row">
            <div className="form-field" style={{ minWidth: 260 }}>
              <label>Enviar nova imagem (JPG/PNG, até 10 MB)</label>
              <input
                type="file"
                accept="image/jpeg,image/png"
                onChange={(e) => setUploadFile(e.target.files?.[0] ?? null)}
              />
            </div>
            <div className="form-field">
              <button type="button" className="btn btn-primary btn-sm" onClick={uploadWelcome} disabled={!uploadFile || uploading}>
                {uploading ? "Enviando…" : "Enviar imagem"}
              </button>
            </div>
          </div>
          {uploadInfo && <div className="alert alert-success">{uploadInfo}</div>}

          <h4>
            Fonte do nome{" "}
            {settings.welcomeFontPath ? (
              <span className="pill pill-success">personalizada</span>
            ) : (
              <span className="pill">fonte do sistema</span>
            )}
          </h4>
          <div className="form-row">
            <div className="form-field" style={{ minWidth: 260 }}>
              <label>Enviar fonte (.ttf ou .otf, até 5 MB)</label>
              <input
                type="file"
                accept=".ttf,.otf,font/ttf,font/otf"
                onChange={(e) => setFontFile(e.target.files?.[0] ?? null)}
              />
            </div>
            <div className="form-field">
              <button type="button" className="btn btn-primary btn-sm" onClick={uploadFont} disabled={!fontFile || uploadingFont}>
                {uploadingFont ? "Enviando…" : "Enviar fonte"}
              </button>
            </div>
            {settings.welcomeFontPath && (
              <div className="form-field">
                <button
                  type="button"
                  className="btn btn-outline btn-sm"
                  onClick={() => patch({ welcomeFontPath: "" })}
                  title="Volta à fonte do sistema — clique em Salvar configurações para aplicar."
                >
                  Voltar à fonte do sistema
                </button>
              </div>
            )}
          </div>
          {fontInfo && <div className="alert alert-success">{fontInfo}</div>}

          <h4>Prévia (como a TV vai mostrar)</h4>
          <div className="form-row">
            <div className="form-field" style={{ minWidth: 220 }}>
              <label>Nome de exemplo</label>
              <input value={previewName} onChange={(e) => setPreviewName(e.target.value)} />
            </div>
            <div className="form-field">
              <button type="button" className="btn btn-outline btn-sm" onClick={generatePreview} disabled={previewing}>
                {previewing ? "Gerando…" : "Gerar prévia"}
              </button>
            </div>
          </div>
          <p className="text-muted" style={{ fontSize: "0.8rem", margin: "0 0 0.5rem" }}>
            A prévia usa a configuração SALVA — clique em "Salvar configurações" antes se mudou
            posição/fonte/cor.
          </p>
          {previewError && <div className="alert alert-danger">{previewError}</div>}
          {previewUrl && (
            <img
              src={previewUrl}
              alt="Prévia da tela de boas-vindas"
              style={{ maxWidth: "100%", borderRadius: 8, border: "1px solid var(--border, #e2e8f0)" }}
            />
          )}
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

      {/* FORA do formulário de propósito: estes botões agem na hora e não podem submeter o form. */}
      <div className="card" style={{ maxWidth: 620, marginTop: "1.25rem" }}>
        <h3 style={{ marginTop: 0 }}>Cópias de segurança</h3>
        <p className="text-muted" style={{ marginTop: 0 }}>
          Cada cópia traz o banco completo <strong>e</strong> o chaveiro que decifra as senhas dos
          aparelhos — restaurar só o banco deixaria todos os controladores sem senha válida. A
          rotina automática roda sozinha; o botão abaixo gera uma na hora.
        </p>
        <p className="text-muted" style={{ marginTop: 0, fontSize: "0.85rem" }}>
          ⚠️ O arquivo contém dados pessoais (nomes, documentos e <strong>fotos de rosto</strong>) e
          as chaves de criptografia. Baixe apenas para um destino controlado.
        </p>

        {backups && !backups.writable && (
          <div className="alert alert-danger">
            O diretório <code>{backups.directory}</code> não é gravável pelo serviço —{" "}
            <strong>nenhuma cópia está sendo gerada</strong>. Ajuste as permissões ou aponte
            <code> Backup:Directory</code> para outro caminho.
          </div>
        )}

        <div style={{ display: "flex", gap: "0.5rem", marginBottom: "0.75rem" }}>
          <button type="button" className="btn btn-primary btn-sm" onClick={handleCreateBackup} disabled={backupBusy}>
            {backupBusy ? "Gerando cópia…" : "Gerar cópia agora"}
          </button>
          <button type="button" className="btn btn-outline btn-sm" onClick={refreshBackups} disabled={backupBusy}>
            Atualizar lista
          </button>
        </div>

        {backupError && <div className="alert alert-danger">{backupError}</div>}
        {backupInfo && <div className="alert alert-success">{backupInfo}</div>}

        {backups && backups.files.length === 0 && (
          <p className="text-muted" style={{ fontSize: "0.9rem" }}>
            Nenhuma cópia ainda. A primeira é gerada na subida do serviço; use o botão acima para
            não esperar.
          </p>
        )}

        {backups && backups.files.length > 0 && (
          <div style={{ overflowX: "auto" }}>
            <table className="table">
              <thead>
                <tr>
                  <th>Arquivo</th>
                  <th>Gerada em</th>
                  <th>Tamanho</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {backups.files.map((f) => (
                  <tr key={f.fileName}>
                    <td style={{ fontFamily: "monospace", fontSize: "0.85rem" }}>{f.fileName}</td>
                    <td>{new Date(f.createdAtUtc).toLocaleString()}</td>
                    <td style={{ whiteSpace: "nowrap" }}>{formatBytes(f.sizeBytes)}</td>
                    <td style={{ whiteSpace: "nowrap" }}>
                      <button type="button" className="btn btn-outline btn-sm" onClick={() => handleDownloadBackup(f.fileName)}>
                        Baixar
                      </button>{" "}
                      <button type="button" className="btn btn-danger btn-sm" onClick={() => handleDeleteBackup(f.fileName)}>
                        Remover
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(1)} ${units[unit]}`;
}
