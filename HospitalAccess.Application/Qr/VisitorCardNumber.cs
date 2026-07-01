namespace HospitalAccess.Application.Qr;

/// <summary>
/// Convenção do NOSSO sistema (não do protocolo do fabricante) para os 9 bytes de
/// "número de cartão" do Appendix 8: os 4 bytes mais significativos guardam o mesmo
/// UserCode (uint) usado para identificar a pessoa/visitante, e os 5 bytes restantes
/// ficam zerados.
///
/// Motivo: o protocolo não exige (nem permite, "validação offline") que o visitante
/// esteja cadastrado como "Person" no controlador — o token do QR é validado localmente
/// pelo firmware (RC4 + CRC8 + expiração). Só que o "authentication record" em tempo
/// real (Classe 9) que o controlador empurra de volta não tem um campo de card number
/// próprio para esse tipo de evento — o comentário do fabricante em frmRecord.cs
/// documenta que, em eventos de cartão, "o número de usuário É o número do cartão".
/// Ao embutir o UserCode do visitante nos 4 bytes altos do "cartão" do QR, o evento de
/// acesso que volta em tempo real (CardTransaction.UserCode) já correlaciona
/// diretamente com o User.UserCode do nosso banco, sem precisar de um campo separado.
/// </summary>
public static class VisitorCardNumber
{
    public const int Length = 9;

    public static byte[] FromUserCode(uint userCode)
    {
        var card = new byte[Length];
        card[0] = (byte)(userCode >> 24);
        card[1] = (byte)(userCode >> 16);
        card[2] = (byte)(userCode >> 8);
        card[3] = (byte)userCode;
        // card[4..8] permanecem 0.
        return card;
    }

    public static uint ToUserCode(ReadOnlySpan<byte> card)
    {
        if (card.Length != Length)
            throw new ArgumentException($"Card number must be exactly {Length} bytes.", nameof(card));

        return (uint)(card[0] << 24 | card[1] << 16 | card[2] << 8 | card[3]);
    }
}
