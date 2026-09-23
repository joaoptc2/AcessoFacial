namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Uma linha por comando enviado a um aparelho — o material bruto para descobrir ONDE o tempo
/// é gasto. Existe porque "os aparelhos estão lentos" não é diagnosticável: sem separar espera
/// na fila de tempo do aparelho, e sem saber qual operação e qual porta, qualquer otimização é
/// chute.
///
/// DESLIGADO por padrão (Configurações → Diagnóstico). Liga-se por alguns dias, exporta-se o
/// CSV e desliga-se. O expurgo automático impede que uma coleta esquecida encha o disco.
///
/// PRIVACIDADE: nunca guarda nome de pessoa. O código numérico do usuário basta para cruzar
/// com o cadastro quando preciso, e sozinho não identifica ninguém fora do sistema.
/// </summary>
public class DeviceCommandTrace
{
    public long Id { get; set; }

    /// <summary>Início do comando (UTC). É a chave de correlação: dois comandos no mesmo instante disputam a rede.</summary>
    public DateTime StartedAtUtc { get; set; }

    public Guid ControllerId { get; set; }

    /// <summary>
    /// Nome e IP DESNORMALIZADOS de propósito: o CSV vai ser lido fora do sistema, e uma
    /// planilha com 30 GUIDs não diz nada a ninguém.
    /// </summary>
    public string ControllerName { get; set; } = string.Empty;
    public string ControllerIp { get; set; } = string.Empty;

    /// <summary>"SDK" (protocolo binário, TCP 8000) ou "HTTP" (painel web do aparelho).</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Comando do protocolo: AddPerson, AddPersonAndImage, OpenDoor, ReadSerialNumber…</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Quem pediu: fila de sincronização, comando manual da tela, gestão de leitos, health-check…
    /// Sem isto não dá para saber se a lentidão que o operador sente vem do que ELE fez ou da
    /// rotina de fundo que estava passando na mesma hora.
    /// </summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// Milissegundos ESPERANDO A VEZ neste controlador (os comandos são serializados por
    /// aparelho). É a separação que decide tudo: fila alta = concorrência nossa; fila zero com
    /// duração alta = o aparelho (ou a rede) é que é lento.
    /// </summary>
    public int QueueWaitMs { get; set; }

    /// <summary>Milissegundos do comando em si, já com a vez garantida.</summary>
    public int DurationMs { get; set; }

    /// <summary>Quantos comandos esperavam este mesmo controlador quando este entrou na fila.</summary>
    public int QueueDepth { get; set; }

    /// <summary>Success, Failed, Timeout, CircuitOpen, GateBusy ou Canceled.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Motivo resumido da falha (truncado). Null em sucesso.</summary>
    public string? Error { get; set; }

    /// <summary>Bytes enviados ao aparelho. A face pesa ~120 KB; os demais comandos, dezenas de bytes.</summary>
    public int PayloadBytes { get; set; }

    /// <summary>Configuração VIGENTE no momento — o mesmo comando com timeout diferente não é comparável.</summary>
    public int TimeoutMs { get; set; }
    public int RestartCount { get; set; }

    /// <summary>Código numérico do usuário envolvido, quando o comando é sobre uma pessoa. Nunca o nome.</summary>
    public long? UserCode { get; set; }
}
