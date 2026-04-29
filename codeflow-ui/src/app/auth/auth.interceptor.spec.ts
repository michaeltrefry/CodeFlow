import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from './auth.service';
import { authInterceptor } from './auth.interceptor';

describe('authInterceptor', () => {
  let httpMock: HttpTestingController;
  let http: HttpClient;
  let auth: { getValidAccessToken: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    auth = { getValidAccessToken: vi.fn().mockResolvedValue('api-token') };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: auth },
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('attaches bearer tokens to relative API requests', async () => {
    http.get('/api/traces').subscribe();
    await Promise.resolve();

    const req = httpMock.expectOne('/api/traces');
    expect(req.request.headers.get('Authorization')).toBe('Bearer api-token');
    req.flush([]);
  });

  it('attaches bearer tokens to same-origin absolute API requests', async () => {
    http.get(`${window.location.origin}/api/me`).subscribe();
    await Promise.resolve();

    const req = httpMock.expectOne(`${window.location.origin}/api/me`);
    expect(req.request.headers.get('Authorization')).toBe('Bearer api-token');
    req.flush({});
  });

  it('does not attach bearer tokens to external or non-API requests', async () => {
    http.get('https://id.example.test/realms/codeflow/.well-known/openid-configuration').subscribe();
    http.get('/assets/runtime-config.json').subscribe();
    await Promise.resolve();

    const oidcReq = httpMock.expectOne('https://id.example.test/realms/codeflow/.well-known/openid-configuration');
    const assetReq = httpMock.expectOne('/assets/runtime-config.json');
    expect(oidcReq.request.headers.has('Authorization')).toBe(false);
    expect(assetReq.request.headers.has('Authorization')).toBe(false);
    expect(auth.getValidAccessToken).not.toHaveBeenCalled();
    oidcReq.flush({});
    assetReq.flush({});
  });

  it('passes API requests through unchanged when no token is available', async () => {
    auth.getValidAccessToken.mockResolvedValueOnce(null);

    http.get('/api/me').subscribe();
    await Promise.resolve();

    const req = httpMock.expectOne('/api/me');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });
});
