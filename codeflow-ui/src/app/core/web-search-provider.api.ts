import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { WebSearchProviderResponse, WebSearchProviderWriteRequest } from './models';

@Injectable({ providedIn: 'root' })
export class WebSearchProviderApi {
  private readonly http = inject(HttpClient);

  get(): Observable<WebSearchProviderResponse> {
    return this.http.get<WebSearchProviderResponse>('/api/admin/web-search-provider');
  }

  set(request: WebSearchProviderWriteRequest): Observable<WebSearchProviderResponse> {
    return this.http.put<WebSearchProviderResponse>('/api/admin/web-search-provider', request);
  }
}
