import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  NotificationDiagnosticsResponse,
  NotificationProviderResponse,
  NotificationProviderWriteRequest,
  NotificationRouteResponse,
  NotificationRouteWriteRequest,
  NotificationTemplateResponse,
} from './models';

/**
 * Admin API client for the HITL notification subsystem (epic 48 / sc-57). All endpoints
 * require the NotificationsRead/Write policies (Admin role only).
 */
@Injectable({ providedIn: 'root' })
export class NotificationsAdminApi {
  private readonly http = inject(HttpClient);

  listProviders(includeArchived = false): Observable<NotificationProviderResponse[]> {
    let params = new HttpParams();
    if (includeArchived) {
      params = params.set('includeArchived', 'true');
    }
    return this.http.get<NotificationProviderResponse[]>('/api/admin/notification-providers', { params });
  }

  putProvider(id: string, request: NotificationProviderWriteRequest): Observable<NotificationProviderResponse> {
    return this.http.put<NotificationProviderResponse>(
      `/api/admin/notification-providers/${encodeURIComponent(id)}`, request);
  }

  archiveProvider(id: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/notification-providers/${encodeURIComponent(id)}`);
  }

  listRoutes(): Observable<NotificationRouteResponse[]> {
    return this.http.get<NotificationRouteResponse[]>('/api/admin/notification-routes');
  }

  putRoute(routeId: string, request: NotificationRouteWriteRequest): Observable<NotificationRouteResponse> {
    return this.http.put<NotificationRouteResponse>(
      `/api/admin/notification-routes/${encodeURIComponent(routeId)}`, request);
  }

  deleteRoute(routeId: string): Observable<void> {
    return this.http.delete<void>(`/api/admin/notification-routes/${encodeURIComponent(routeId)}`);
  }

  /** sc-57 only supports per-templateId history listings — full inventory listing lands in sc-63. */
  listTemplateVersions(templateId: string): Observable<NotificationTemplateResponse[]> {
    const params = new HttpParams().set('templateId', templateId);
    return this.http.get<NotificationTemplateResponse[]>('/api/admin/notification-templates', { params });
  }

  getDiagnostics(): Observable<NotificationDiagnosticsResponse> {
    return this.http.get<NotificationDiagnosticsResponse>('/api/admin/notifications/diagnostics');
  }
}
