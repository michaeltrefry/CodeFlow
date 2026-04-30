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
      return message;
    }

    const status = (err as { status?: unknown }).status;
    if (typeof status === 'number') {
      const statusText = (err as { statusText?: unknown }).statusText;
      return typeof statusText === 'string' && statusText.trim().length > 0
        ? `HTTP ${status} ${statusText}`
        : `HTTP ${status}`;
    }
  }

  if (err instanceof Error && err.message.trim().length > 0) {
    return err.message;
  }

  return fallback;
}

function formatErrorBody(body: unknown): string | null {
  if (typeof body === 'string') {
    return body.trim().length > 0 ? body : null;
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
