import { ClassicPreset, GetSchemes } from 'rete';
import { AreaExtra } from './workflow-area-extra';
import { WorkflowNodeKind } from '../../../core/models';

export type WorkflowNodeTraceState = 'active' | 'dimmed' | null;

/** The implicit error-sink port present on every node. Authors never declare it; the editor
 *  always exposes it as a wirable handle so a recovery edge can be attached. Excluded from
 *  serialized `outputPorts` (the API rejects "Failed" in declarations as of the port-model
 *  redesign). */
export const IMPLICIT_FAILED_PORT = 'Failed';

export class WorkflowEditorNode extends ClassicPreset.Node {
  readonly nodeId: string;
  readonly kind: WorkflowNodeKind;
  agentKey: string | null;
  agentVersion: number | null;
  outputScript: string | null;
  inputScript: string | null;
  subflowKey: string | null;
  subflowVersion: number | null;
  reviewMaxRounds: number | null;
  loopDecision: string | null;
  traceState: WorkflowNodeTraceState = null;

  // VZ5: tracks whether the implicit Failed port is wired and whether the canvas-level
  // "Show implicit Failed ports" toggle is on. Both are populated by the canvas after node
  // creation and on every connection event; the node component reads them from the template
  // to decide whether to render or hide the Failed row.
  failedHasConnection: boolean = false;
  showImplicitFailed: () => boolean = () => false;

  constructor(params: {
    nodeId: string;
    kind: WorkflowNodeKind;
    label: string;
    agentKey?: string | null;
    agentVersion?: number | null;
    outputScript?: string | null;
    inputScript?: string | null;
    outputPorts: string[];
    subflowKey?: string | null;
    subflowVersion?: number | null;
    reviewMaxRounds?: number | null;
    loopDecision?: string | null;
  }) {
    super(params.label);
    this.nodeId = params.nodeId;
    this.kind = params.kind;
    this.agentKey = params.agentKey ?? null;
    this.agentVersion = params.agentVersion ?? null;
    this.outputScript = params.outputScript ?? null;
    this.inputScript = params.inputScript ?? null;
    this.subflowKey = params.subflowKey ?? null;
    this.subflowVersion = params.subflowVersion ?? null;
    this.reviewMaxRounds = params.reviewMaxRounds ?? null;
    this.loopDecision = params.loopDecision ?? null;

    if (params.kind !== 'Start') {
      this.addInput('in', new ClassicPreset.Input(new ClassicPreset.Socket('port'), 'in', true));
    }

    for (const port of params.outputPorts) {
      if (port === IMPLICIT_FAILED_PORT) continue; // implicit, added below
      this.addOutput(port, new ClassicPreset.Output(new ClassicPreset.Socket('port'), port));
    }

    // Failed is implicit on every node — always render as a handle so authors can attach a
    // recovery edge without declaring it. The 'failed' socket kind is what the renderer can
    // pick up to style the handle distinctly (e.g., dashed red).
    this.addOutput(
      IMPLICIT_FAILED_PORT,
      new ClassicPreset.Output(new ClassicPreset.Socket('failed'), IMPLICIT_FAILED_PORT));
  }

  /** Author-declared output port names. Excludes the implicit Failed handle. */
  get outputPortNames(): string[] {
    return Object.keys(this.outputs).filter(name => name !== IMPLICIT_FAILED_PORT);
  }

  /** All output port names including the implicit Failed handle. */
  get allOutputPortNames(): string[] {
    return Object.keys(this.outputs);
  }
}

export class WorkflowEditorConnection
  extends ClassicPreset.Connection<WorkflowEditorNode, WorkflowEditorNode> {
  rotatesRound = false;
  sortOrder = 0;
  isSelected = false;
  /** Author-marked intentional backedge — suppresses VZ7 dashed-amber and V6 save warnings. */
  intentionalBackedge = false;
  onPick: (() => void) | null = null;
}

export type WorkflowSchemes = GetSchemes<WorkflowEditorNode, WorkflowEditorConnection>;
export type WorkflowAreaExtra = AreaExtra<WorkflowSchemes>;
