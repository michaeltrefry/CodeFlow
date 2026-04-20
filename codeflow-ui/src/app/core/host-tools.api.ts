import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { HostTool } from './models';

@Injectable({ providedIn: 'root' })
export class HostToolsApi {
  private readonly http = inject(HttpClient);

  list(): Observable<HostTool[]> {
    return this.http.get<HostTool[]>('/api/host-tools');
  }
}
