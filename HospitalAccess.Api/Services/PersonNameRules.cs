namespace HospitalAccess.Api.Services;

/// <summary>
/// Validação do nome de uma pessoa que vai para o aparelho (usuário permanente, visitante ou
/// paciente internado). O nome viaja no <c>PersonData.PName</c> do protocolo.
///
/// <para>
/// SOBRE O LIMITE: o material de referência disponível no repositório não documenta o tamanho
/// exato do campo no firmware, e não há hardware aqui para medir. <see cref="MaxLength"/> é uma
/// GUARDA DE APLICAÇÃO deliberadamente folgada — cobre nome completo brasileiro com sobra e
/// impede o caso patológico (colar um texto inteiro no campo) de virar falha de sincronização
/// obscura. Se um dia o limite real do aparelho for medido e for MENOR, é aqui que se ajusta.
/// </para>
/// <para>
/// A coluna no banco continua <c>text</c> de propósito: apertar o tipo exigiria uma migration que
/// falha se algum registro existente for maior, e o ganho para o usuário (mensagem clara na hora)
/// vem da validação, não do tipo da coluna.
/// </para>
/// </summary>
public static class PersonNameRules
{
    public const int MaxLength = 100;
    public const int MinLength = 2;

    /// <summary>
    /// Normaliza e valida. Em sucesso, <paramref name="normalized"/> traz o nome sem espaços nas
    /// pontas e sem espaços internos repetidos; em falha, <paramref name="error"/> traz a
    /// mensagem para a tela.
    /// </summary>
    public static bool TryNormalize(string? raw, string fieldLabel, out string normalized, out string? error)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{fieldLabel} é obrigatório.";
            return false;
        }

        // Espaços internos repetidos viram um só: o mesmo nome digitado de dois jeitos não pode
        // virar dois cadastros visualmente idênticos na lista.
        normalized = string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (normalized.Length < MinLength)
        {
            error = $"{fieldLabel} deve ter pelo menos {MinLength} caracteres.";
            return false;
        }

        if (normalized.Length > MaxLength)
        {
            error = $"{fieldLabel} deve ter no máximo {MaxLength} caracteres (recebido: {normalized.Length}). " +
                    "Nomes longos demais são recusados pelo leitor facial na sincronização.";
            return false;
        }

        // Caracteres de controle quebram a serialização do protocolo e não têm uso legítimo num nome.
        if (normalized.Any(char.IsControl))
        {
            error = $"{fieldLabel} contém caracteres inválidos (quebra de linha ou controle).";
            return false;
        }

        error = null;
        return true;
    }
}
