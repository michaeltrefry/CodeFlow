import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  GitHostSettingsRequest,
  GitHostSettingsResponse,
  GitHostVerifyResponse,
} from './models';

@Injectable({ providedIn: 'root' })
export class GitHostApi {
  private readonly http = inject(HttpClient);

  get(): Observable<GitHostSettingsResponse> {
    return this.http.get<GitHostSettingsResponse>('/api/admin/git-host');
  }

  set(request: GitHostSettingsRequest): Observable<GitHostSettingsResponse> {
    return this.http.put<GitHostSettingsResponse>('/api/admin/git-host', request);
  }

  verify(): Observable<GitHostVerifyResponse> {
    return this.http.post<GitHostVerifyResponse>('/api/admin/git-host/verify', {});
  }
}
