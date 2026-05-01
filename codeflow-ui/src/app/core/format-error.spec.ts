import { formatHttpError } from './format-error';

describe('formatHttpError', () => {
  it('formats ASP.NET validation problem details with field-level messages', () => {
    expect(formatHttpError({
      error: {
        title: 'One or more validation errors occurred.',
        errors: {
          token: ['Token is required.'],
          baseUrl: ['Base URL is invalid.', 'Base URL must use HTTPS.'],
        },
      },
    })).toBe('Token is required.; Base URL is invalid.; Base URL must use HTTPS.');
  });

  it('prefers specific problem fields over generic object stringification', () => {
    expect(formatHttpError({
      error: {
        title: 'Bad request',
        error: 'Provider is not configured.',
      },
    })).toBe('Provider is not configured.');
  });

  it('falls back to HTTP or caller-provided messages when no structured reason exists', () => {
    expect(formatHttpError({ status: 404, statusText: 'Not Found' })).toBe('HTTP 404 Not Found');
    expect(formatHttpError(null, 'Save failed')).toBe('Save failed');
  });

  it('parses validation problem details when Angular receives the body as text', () => {
    expect(formatHttpError({
      error: JSON.stringify({
        title: 'One or more validation errors occurred.',
        errors: {
          package: ['Workflow package import failed validation.'],
          'workflows.draft-save': ['Workflow must contain exactly one Start node.'],
        },
      }),
    })).toBe(
      'Workflow package import failed validation.; Workflow must contain exactly one Start node.'
    );
  });

  it('does not show Angular transport wording for empty 400 responses', () => {
    expect(formatHttpError({
      status: 400,
      statusText: 'OK',
      message: 'Http failure response for /api/workflows/package/apply-from-draft: 400 OK',
    })).toBe(
      'Bad request (HTTP 400). The server rejected the request but did not return validation details.'
    );
  });
});
