export function formatHttpError(err: unknown, fallback = 'Request failed.'): string {
  if (typeof err === 'string' && err.trim().length > 0) {
    return err;
  }

  if (err && typeof err === 'object') {
    const body = 'error' in err ? (err as { error?: unknown }).error : undefined;
    const bodyMessage = formatErrorBody(body);
    if (bodyMessage) {
      return bodyMessage;
    }

    const message = (err as { message?: unknown }).message;
    if (typeof message === 'string' && message.trim().length > 0) {
      const trimmedMessage = message.trim();
      if (!isAngularTransportMessage(trimmedMessage)) {
        return trimmedMessage;
      }
    }

    const status = (err as { status?: unknown }).status;
    if (typeof status === 'number') {
      const statusText = (err as { statusText?: unknown }).statusText;
      const statusTextStr = typeof statusText === 'string' && statusText.trim().length > 0
        && statusText.trim().toUpperCase() !== 'OK'
        ? statusText.trim()
        : null;
      const url = (err as { url?: unknown }).url;
      const urlStr = typeof url === 'string' && url.trim().length > 0 ? url.trim() : null;

      // Log the raw error to the console so a developer (or a curious user with DevTools open)
      // can inspect the response body even if the formatter can't extract a message from it.
      // This is a deliberate net for the case where `body` is null/undefined or arrives as an
      // unexpected shape — the dev console becomes the diagnostic surface of last resort.
      try {
        // eslint-disable-next-line no-console
        console.error('[formatHttpError] No message extractable from response body:', err);
      } catch {
        // Swallow — defensive for old browsers / restricted contexts.
      }

      if (status === 400) {
        const where = urlStr ? ` Endpoint: ${urlStr}.` : '';
        return `Bad request (HTTP 400${statusTextStr ? ' ' + statusTextStr : ''}). `
          + `The server rejected the request and did not return a parseable message body. `
          + `Open the browser devtools (Network tab) to inspect the response.${where}`;
      }

      return statusTextStr
        ? `HTTP ${status} ${statusTextStr}`
        : `HTTP ${status}`;
    }
  }

  if (err instanceof Error && err.message.trim().length > 0) {
    return err.message;
  }

  return fallback;
}

function isAngularTransportMessage(message: string): boolean {
  return message.startsWith('Http failure response for ');
}

function formatErrorBody(body: unknown): string | null {
  if (typeof body === 'string') {
    const trimmed = body.trim();
    if (trimmed.length === 0) {
      return null;
    }

    const parsed = parseJsonObject(trimmed);
    if (parsed) {
      return formatErrorBody(parsed);
    }

    return trimmed;
  }

  if (!body || typeof body !== 'object') {
    return null;
  }

  const validationMessages = validationErrors(body);
  if (validationMessages.length > 0) {
    return validationMessages.join('; ');
  }

  for (const key of ['error', 'detail', 'message', 'title'] as const) {
    const value = (body as Record<string, unknown>)[key];
    const valueMessage = fieldMessage(value);
    if (valueMessage) {
      return valueMessage;
    }
  }

  try {
    return JSON.stringify(body);
  } catch {
    return null;
  }
}

function validationErrors(body: object): string[] {
  const errors = (body as { errors?: unknown }).errors;
  if (!errors || typeof errors !== 'object') {
    return [];
  }

  const messages: string[] = [];
  for (const value of Object.values(errors as Record<string, unknown>)) {
    if (Array.isArray(value)) {
      for (const item of value) {
        if (typeof item === 'string' && item.trim().length > 0) {
          messages.push(item);
        }
      }
    } else if (typeof value === 'string' && value.trim().length > 0) {
      messages.push(value);
    }
  }
  return messages;
}

function fieldMessage(value: unknown): string | null {
  if (typeof value === 'string') {
    return value.trim().length > 0 ? value : null;
  }

  if (!value || typeof value !== 'object') {
    return null;
  }

  for (const key of ['error', 'detail', 'message', 'title'] as const) {
    const nested = (value as Record<string, unknown>)[key];
    if (typeof nested === 'string' && nested.trim().length > 0) {
      return nested;
    }
  }

  return null;
}

function parseJsonObject(value: string): object | null {
  if (!value.startsWith('{')) {
    return null;
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : null;
  } catch {
    return null;
  }
}
