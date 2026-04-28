import { Observable } from 'rxjs';

export interface AgentTestTokenUsage {
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
}

export interface AgentTestStartedEvent {
  type: 'started';
  agentKey: string;
  agentVersion: number;
  provider: string;
  model: string;
  timestampUtc: string;
}

export interface AgentTestModelCallStartedEvent {
  type: 'model-call-started';
  roundNumber: number;
  timestampUtc: string;
}

export interface AgentTestModelCallCompletedEvent {
  type: 'model-call-completed';
  roundNumber: number;
  assistantText: string;
  toolCallCount: number;
  callTokenUsage?: AgentTestTokenUsage | null;
  cumulativeTokenUsage?: AgentTestTokenUsage | null;
  timestampUtc: string;
}

export interface AgentTestToolCallStartedEvent {
  type: 'tool-call-started';
  callId: string;
  name: string;
  arguments?: unknown;
  timestampUtc: string;
}

export interface AgentTestToolCallCompletedEvent {
  type: 'tool-call-completed';
  callId: string;
  name: string;
  isError: boolean;
  resultPreview?: string | null;
  resultTruncated: boolean;
  timestampUtc: string;
}

export interface AgentTestCompletedEvent {
  type: 'completed';
  output: string;
  decisionKind: string;
  decisionPayload?: unknown;
  toolCallsExecuted: number;
  tokenUsage?: AgentTestTokenUsage | null;
  durationMs: number;
  timestampUtc: string;
}

export interface AgentTestErrorEvent {
  type: 'error';
  message: string;
  durationMs?: number;
}

export type AgentTestEvent =
  | AgentTestStartedEvent
  | AgentTestModelCallStartedEvent
  | AgentTestModelCallCompletedEvent
  | AgentTestToolCallStartedEvent
  | AgentTestToolCallCompletedEvent
  | AgentTestCompletedEvent
  | AgentTestErrorEvent;

export interface AgentTestRequest {
  agentKey: string;
  agentVersion?: number | null;
  input: string;
  variables?: Record<string, string | null> | null;
}

export function streamAgentTest(
  request: AgentTestRequest,
  accessToken: string | null | Promise<string | null>
): Observable<AgentTestEvent> {
  return new Observable<AgentTestEvent>(subscriber => {
    const controller = new AbortController();

    const run = async () => {
      try {
        const headers: Record<string, string> = {
          'Content-Type': 'application/json',
          Accept: 'text/event-stream'
        };
        const token = await Promise.resolve(accessToken);
        if (token) {
          headers['Authorization'] = `Bearer ${token}`;
        }

        const response = await fetch('/api/agent-test', {
          method: 'POST',
          headers,
          body: JSON.stringify(request),
          signal: controller.signal
        });

        if (!response.ok || !response.body) {
          const text = await response.text().catch(() => '');
          subscriber.next({
            type: 'error',
            message: text || `HTTP ${response.status} ${response.statusText}`
          });
          subscriber.complete();
          return;
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
          const { value, done } = await reader.read();
          if (done) { break; }
          buffer += decoder.decode(value, { stream: true });

          let delimiterIndex = buffer.indexOf('\n\n');
          while (delimiterIndex !== -1) {
            const raw = buffer.slice(0, delimiterIndex);
            buffer = buffer.slice(delimiterIndex + 2);

            const parsed = parseSseFrame(raw);
            if (parsed) {
              subscriber.next(parsed);
            }

            delimiterIndex = buffer.indexOf('\n\n');
          }
        }

        subscriber.complete();
      } catch (err) {
        if ((err as Error)?.name === 'AbortError') {
          subscriber.complete();
          return;
        }
        subscriber.error(err);
      }
    };

    void run();

    return () => controller.abort();
  });
}

function parseSseFrame(raw: string): AgentTestEvent | null {
  let eventName = '';
  const dataLines: string[] = [];

  for (const line of raw.split('\n')) {
    if (line.startsWith(':')) { continue; }
    if (line.startsWith('event:')) {
      eventName = line.slice(6).trim();
    } else if (line.startsWith('data:')) {
      dataLines.push(line.slice(5).trim());
    }
  }

  if (!eventName || dataLines.length === 0) {
    return null;
  }

  try {
    const data = JSON.parse(dataLines.join('\n'));
    return { type: eventName, ...data } as AgentTestEvent;
  } catch {
    return null;
  }
}
