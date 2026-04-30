import type { Observable } from 'rxjs';
import type { SseFrame } from './sse-stream';
import { streamSse } from './sse-stream';

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
  return streamSse(
    '/api/agent-test',
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
      accessToken,
      handleErrorResponse: async response => {
        const text = await response.text().catch(() => '');
        return {
          type: 'error',
          message: text || `HTTP ${response.status} ${response.statusText}`
        } satisfies AgentTestErrorEvent;
      },
    },
    parseSseFrame,
  );
}

function parseSseFrame({ eventName, dataLines }: SseFrame): AgentTestEvent | null {
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
