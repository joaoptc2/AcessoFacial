// Utilitários de imagem do lado do navegador.

interface DownscaleOptions {
  /** Maior lado (px) da imagem resultante. O servidor ainda converte para 480x640 no cadastro. */
  maxDim?: number;
  /** Qualidade JPEG (0-1) na recompressão. */
  quality?: number;
  /** Não mexe em arquivos já menores que isto (evita recomprimir foto pequena à toa). */
  skipUnderBytes?: number;
}

/**
 * Reduz uma imagem no navegador ANTES do upload: reamostra para no máximo `maxDim` px no maior
 * lado e recomprime como JPEG. Uma foto crua de celular tem vários MB e era barrada pelo Nginx
 * ("413 Request Entity Too Large") antes de chegar na API; assim o upload cai para algumas
 * centenas de KB, sem perder qualidade útil (o servidor de qualquer forma converte a face para
 * 480x640 / <=120 KB no cadastro).
 *
 * Degradação graciosa: devolve o arquivo ORIGINAL se ele já for pequeno, se o navegador não
 * suportar as APIs usadas, ou se a recompressão não reduzir o tamanho — nunca lança.
 */
export async function downscaleImageForUpload(
  file: File,
  { maxDim = 1024, quality = 0.85, skipUnderBytes = 512 * 1024 }: DownscaleOptions = {},
): Promise<File> {
  if (!file.type.startsWith("image/")) return file;
  if (file.size <= skipUnderBytes) return file;
  if (typeof createImageBitmap !== "function" || typeof document === "undefined") return file;

  try {
    // imageOrientation "from-image" respeita o EXIF (evita rosto rotacionado em foto de celular).
    const bitmap = await createImageBitmap(file, { imageOrientation: "from-image" });
    const scale = Math.min(1, maxDim / Math.max(bitmap.width, bitmap.height));
    const width = Math.max(1, Math.round(bitmap.width * scale));
    const height = Math.max(1, Math.round(bitmap.height * scale));

    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext("2d");
    if (!ctx) {
      bitmap.close?.();
      return file;
    }
    ctx.drawImage(bitmap, 0, 0, width, height);
    bitmap.close?.();

    const blob = await new Promise<Blob | null>((resolve) =>
      canvas.toBlob(resolve, "image/jpeg", quality),
    );
    if (!blob || blob.size >= file.size) return file;

    const name = file.name.replace(/\.[^.]+$/, "") + ".jpg";
    return new File([blob], name, { type: "image/jpeg", lastModified: file.lastModified });
  } catch {
    return file;
  }
}
