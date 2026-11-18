namespace HospitalAccess.Gateway.Imaging;

/// <summary>
/// Validação da foto de rosto NO MOMENTO DO UPLOAD, para o erro chegar na tela de cadastro em
/// vez de virar uma linha na lista de falhas minutos depois.
///
/// <para>
/// LIMITE HONESTO: não fazemos detecção facial — isso exigiria um modelo de visão computacional
/// e é justamente o que o firmware do 8190H faz ao receber a imagem. O que dá para afirmar aqui
/// é o que não depende de modelo: o arquivo é mesmo uma imagem, ela é grande o bastante para ter
/// alguma chance de extrair um rosto, e ela sobrevive à conversão para o formato do aparelho.
/// Foto nítida de um poste passa nesta validação e é recusada pelo aparelho depois — a diferença
/// é que os casos GROSSEIROS (PDF renomeado, ícone de 32x32) param aqui.
/// </para>
/// <para>
/// Upload interrompido (JPEG truncado) é recusado aqui desde a troca do ImageSharp 2.x pelo
/// SkiaSharp: o decodificador antigo completava o arquivo e deixava a foto seguir até o
/// aparelho, que respondia "sem rosto". Fixado em teste (<c>JpegTruncado_EhRecusado</c>).
/// </para>
/// </summary>
public static class FacePhotoValidator
{
    /// <summary>
    /// Piso de resolução. Abaixo disso não há pixels suficientes para o aparelho extrair um
    /// feature code — o upload até acontece, e o retorno é sempre "sem rosto". Escolhido como
    /// um quarto do alvo de 480x640 do dispositivo.
    /// </summary>
    public const int MinWidth = 120;
    public const int MinHeight = 160;

    /// <summary>Alvo do aparelho, replicado do <see cref="FaceImageConverter"/> (Classe 11 do protocolo).</summary>
    private const int TargetWidth = 480;
    private const int TargetHeight = 640;
    private const int MaxBytes = 122880;

    /// <summary>
    /// Valida os bytes recebidos. Devolve <c>true</c> quando a foto pode seguir para o cadastro;
    /// em <c>false</c>, <paramref name="error"/> traz uma mensagem pronta para a tela — escrita
    /// para quem está na recepção, dizendo o que fazer, não o que falhou internamente.
    /// </summary>
    public static bool TryValidate(byte[]? bytes, out string? error)
    {
        if (bytes is null || bytes.Length == 0)
        {
            error = "A foto está vazia. Selecione um arquivo de imagem.";
            return false;
        }

        // Só o cabeçalho: formato e dimensões, sem decodificar. Null = não é imagem, ou é de um
        // formato fora da lista aceita (TIFF, BMP, GIF...).
        var info = ImageDecoding.Identify(bytes);
        if (info is null)
        {
            error = "O arquivo enviado não é uma imagem que o sistema consiga abrir. " +
                    "Use JPG ou PNG — se o arquivo veio de um celular, tente exportar novamente.";
            return false;
        }

        if (info.Value.Width < MinWidth || info.Value.Height < MinHeight)
        {
            error = $"A foto tem {info.Value.Width}x{info.Value.Height} pixels, pequena demais para o leitor " +
                    $"reconhecer um rosto (mínimo {MinWidth}x{MinHeight}). Envie uma foto maior.";
            return false;
        }

        // Última prova: a conversão real para o formato do aparelho. Pega o que só aparece ao
        // decodificar de fato — arquivo truncado ou corrompido, imagem grande demais, imagem que
        // não comprime até o teto — e evita gravar no banco uma foto que só falharia na
        // sincronização.
        try
        {
            FaceImageConverter.ConvertImage(bytes, TargetWidth, TargetHeight, MaxBytes);
        }
        catch (ImageDecodeException ex)
        {
            error = $"Não foi possível usar esta foto: {ex.Message}.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Não foi possível preparar esta foto para o leitor facial ({ex.Message}) " +
                    "Tente outra foto.";
            return false;
        }

        error = null;
        return true;
    }
}
