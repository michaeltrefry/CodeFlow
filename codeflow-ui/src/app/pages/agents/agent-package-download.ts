/** AP-6 (sc-837): browser download helpers shared by the Agents list (per-row Export
 *  button) and the Agent detail page (header Export button). Module-level so component
 *  tests can patch `document.createElement` / `URL.createObjectURL` without owning the
 *  components themselves. */

/** Trigger the browser's save-as flow for `blob` under `fileName`. */
export function saveBlobToDisk(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
  URL.revokeObjectURL(url);
}

/** Parse a `Content-Disposition` header for the suggested filename. Handles both the
 *  bare `filename=name.json` form and the quoted `filename="name with spaces.json"`
 *  form; returns null when the header is missing or unparseable. */
export function fileNameFromContentDisposition(header: string | null): string | null {
  if (!header) return null;
  const match = /filename\*?=(?:UTF-8'')?(?:"([^"]+)"|([^;]+))/i.exec(header);
  if (!match) return null;
  return (match[1] ?? match[2] ?? '').trim() || null;
}
