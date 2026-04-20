import { Observable } from 'rxjs';
import { TraceStreamEvent } from './models';

export function streamTrace(traceId: string): Observable<TraceStreamEvent> {
  return new Observable<TraceStreamEvent>(subscriber => {
    const source = new EventSource(`/api/traces/${traceId}/stream`);

    const handle = (kind: 'requested' | 'completed') => (event: MessageEvent<string>) => {
      try {
        const payload = JSON.parse(event.data) as TraceStreamEvent;
        subscriber.next(payload);
      } catch (err) {
        subscriber.error(err);
      }
      void kind;
    };

    source.addEventListener('requested', handle('requested') as EventListener);
    source.addEventListener('completed', handle('completed') as EventListener);
    source.onerror = () => {
      // EventSource auto-retries on transient errors; only complete on permanent close.
      if (source.readyState === EventSource.CLOSED) {
        subscriber.complete();
      }
    };

    return () => source.close();
  });
}
