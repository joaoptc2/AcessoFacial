# Material de referência do fabricante (fora do build)

Esta pasta guarda a documentação e o SDK originais do fabricante das controladoras
faciais **8190H (A33_Face)**. Nada aqui é compilado ou empacotado — é material de
consulta/base para o desenvolvimento do gateway.

| Arquivo | Conteúdo |
|---------|----------|
| `8190H Fingerprint&Face Communication Protocol.doc` | Protocolo de comunicação (124 págs). Resumo técnico em `../docs` / na revisão. |
| `8190H Software Development Kit.rar` | SDK multi-linguagem (C#, Java, C++, VB) com exemplos. |
| `DoNetDrive.Protocol.Fingerprint.part1.rar` / `part2.rar` | Código do projeto de exemplo oficial (`DoNetDrive.Protocol.Fingerprint.Test`) e os pacotes/DLLs do SDK. |

As DLLs de runtime realmente usadas pelo `HospitalAccess.Gateway` estão versionadas
separadamente em `../HospitalAccess.Gateway/lib/` (ver README principal). Estes arquivos
são grandes (~51 MB somados); se o repositório crescer, considere movê-los para Git LFS
ou um artefato de release.
