import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  NotificationDeliveryAttemptListQuery,
  NotificationDeliveryAttemptListResponse,
  NotificationDiagnosticsResponse,
  NotificationProviderResponse,
  NotificationProviderValidationResponse,
  NotificationProviderWriteRequest,
  NotificationRouteResponse,
  NotificationRouteWriteRequest,
  NotificationTemplateResponse,
  NotificationTestSendRequest,
  NotificationTestSendResponse,
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

  /**
   * sc-58 — credential check. Calls the provider's ValidateAsync without sending a message.
   * Useful as a "did I paste the right token" sanity check before saving a config.
   */
  validateProvider(id: string): Observable<NotificationProviderValidationResponse> {
    return this.http.post<NotificationProviderValidationResponse>(
      `/api/admin/notification-providers/${encodeURIComponent(id)}/validate`, {});
  }

  /**
   * sc-58 — sends a synthetic notification through the provider so admins can verify
   * credentials, destinations, rendered copy, and the canonical action URL. Bypasses the
   * dispatcher (no audit row, no dedupe).
   */
  testSendProvider(id: string, request: NotificationTestSendRequest): Observable<NotificationTestSendResponse> {
    return this.http.post<NotificationTestSendResponse>(
      `/api/admin/notification-providers/${encodeURIComponent(id)}/test-send`, request);
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

  /**
   * sc-59 — admin audit listing of provider delivery attempts. Cursor-paginated; pass
   * `nextBeforeId` from the previous page back as `beforeId` to fetch the next.
   */
  listDeliveryAttempts(query: NotificationDeliveryAttemptListQuery = {}): Observable<NotificationDeliveryAttemptListResponse> {
    let params = new HttpParams();
    if (query.eventId) params = params.set('eventId', query.eventId);
    if (query.providerId) params = params.set('providerId', query.providerId);
    if (query.routeId) params = params.set('routeId', query.routeId);
    if (query.status) params = params.set('status', query.status);
    if (query.sinceUtc) params = params.set('sinceUtc', query.sinceUtc);
    if (query.beforeId !== undefined && query.beforeId !== null) {
      params = params.set('beforeId', String(query.beforeId));
    }
    if (query.limit !== undefined && query.limit !== null) {
      params = params.set('limit', String(query.limit));
    }
    return this.http.get<NotificationDeliveryAttemptListResponse>(
      '/api/admin/notification-delivery-attempts', { params });
  }
}
