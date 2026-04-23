import { ClassicPreset, GetSchemes } from 'rete';
import { AreaExtra } from './workflow-area-extra';
import { WorkflowNodeKind } from '../../../core/models';

export type WorkflowNodeTraceState = 'active' | 'dimmed' | null;

export class WorkflowEditorNode extends ClassicPreset.Node {
  readonly nodeId: string;
  readonly kind: WorkflowNodeKind;
  agentKey: string | null;
  agentVersion: number | null;
  script: string | null;
  subflowKey: string | null;
  subflowVersion: number | null;
  traceState: WorkflowNodeTraceState = null;

  constructor(params: {
    nodeId: string;
    kind: WorkflowNodeKind;
    label: string;
    agentKey?: string | null;
    agentVersion?: number | null;
    script?: string | null;
    outputPorts: string[];
    subflowKey?: string | null;
    subflowVersion?: number | null;
  }) {
    super(params.label);
    this.nodeId = params.nodeId;
    this.kind = params.kind;
    this.agentKey = params.agentKey ?? null;
    this.agentVersion = params.agentVersion ?? null;
    this.script = params.script ?? null;
    this.subflowKey = params.subflowKey ?? null;
    this.subflowVersion = params.subflowVersion ?? null;

    if (params.kind !== 'Start') {
      this.addInput('in', new ClassicPreset.Input(new ClassicPreset.Socket('port'), 'in', true));
    }

    if (params.kind !== 'Escalation') {
      for (const port of params.outputPorts) {
        this.addOutput(port, new ClassicPreset.Output(new ClassicPreset.Socket('port'), port));
      }
    }
  }

  get outputPortNames(): string[] {
    return Object.keys(this.outputs);
  }
}

export class WorkflowEditorConnection
  extends ClassicPreset.Connection<WorkflowEditorNode, WorkflowEditorNode> {
  rotatesRound = false;
  sortOrder = 0;
  isSelected = false;
  onPick: (() => void) | null = null;
}

export type WorkflowSchemes = GetSchemes<WorkflowEditorNode, WorkflowEditorConnection>;
export type WorkflowAreaExtra = AreaExtra<WorkflowSchemes>;
