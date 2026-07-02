using System.Text;

namespace HospitalAccess.Application.Qr;

/// <summary>
/// Gera o payload do QR Code de abertura de porta.
///
/// Formato confirmado contra um QR real e funcional do sistema oficial do fabricante
/// (fornecido pelo cliente em 2026-07-02): o texto codificado no QR é o Base64 de
///   "user_id={UserCode}_time={microssegundos desde a época Unix (UTC)}"
/// Ex.: o QR de referência decodifica (Base64 -> ASCII) para
///   "user_id=1_time=1782921297138761"
/// — bem diferente do formato binário cifrado com RC4 (Appendix 8 do documento de
/// protocolo) que a implementação original deste serviço assumia. O documento de
/// protocolo aparentemente não reflete o que o produto realmente usa (ou descreve um
/// modo alternativo não utilizado) — o QR real do fabricante é a fonte de verdade aqui.
///
/// ⚠️ Não há criptografia nem checksum neste formato — qualquer um que veja o texto
/// decodificado pode forjar um QR com outro user_id. Isso só é seguro se o controlador
/// validar o acesso consultando um servidor (user_id ainda está autorizado agora?) em vez
/// de confiar cegamente no QR offline. Não confirmado: se "time" é o instante de geração
/// (nossa leitura mais provável, já que o valor de referência bate com "agora" no momento
/// em que o QR foi gerado) ou algum tipo de janela de validade/replay — sem um segundo QR
/// de referência (gerado em outro momento, ou com validade diferente) não dá pra confirmar.
/// </summary>
public sealed class QrAccessTokenService
{
    /// <summary>
    /// Monta o texto do QR: Base64 de "user_id={userCode}_time={microssegundos desde a época Unix}".
    /// </summary>
    /// <param name="userCode">Código numérico do usuário/visitante (User.UserCode).</param>
    /// <param name="timestampUtc">Instante gravado no QR (nossa leitura: momento de geração).</param>
    public string BuildAccessToken(uint userCode, DateTime timestampUtc)
    {
        var microsecondsSinceEpoch = (long)(timestampUtc.ToUniversalTime() - DateTime.UnixEpoch).TotalMicroseconds;
        var plainText = $"user_id={userCode}_time={microsecondsSinceEpoch}";
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(plainText));
    }
}
