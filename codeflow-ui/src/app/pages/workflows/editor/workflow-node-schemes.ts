import { AngularArea2D } from 'rete-angular-plugin/20';
import { ClassicPreset, GetSchemes } from 'rete';
import { WorkflowNodeKind, WorkflowSwarmProtocol, WorkflowTransformOutputType } from '../../../core/models';

type ClassicSchemes = GetSchemes<
  ClassicPreset.Node,
  ClassicPreset.Connection<ClassicPreset.Node, ClassicPreset.Node>
>;

export type AreaExtra<Schemes extends ClassicSchemes = ClassicSchemes> = AngularArea2D<Schemes>;

export type WorkflowNodeTraceState = 'active' | 'dimmed' | null;

/**
 * Compact per-node token overlay shape (Token Usage Tracking [Slice 7]). Carries
 * just the numbers the node-head badge needs — full breakdowns live in the
 * trace-inspector panel + timeline, not on the graph node.
 */
export interface WorkflowNodeTokenOverlay {
  /** Number of LLM calls captured for this node (direct or rolled up from a
   *  child saga the Subflow/ReviewLoop/Swarm spawned). */
  callCount: number;
  /** Sum of `input_tokens` (or `prompt_tokens` if that's what the provider
   *  reports) across every captured call attributed to this node. */
  inputTokens: number;
  /** Sum of `output_tokens` (or `completion_tokens`) across the same. */
  outputTokens: number;
  /** When true, the totals come from descendant child sagas (Subflow /
   *  ReviewLoop / Swarm) rather than this node directly issuing LLM calls. The
   *  badge styling differs slightly so authors can tell the two apart at a
   *  glance. */
  rolledUp: boolean;
}

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
  // Transform nodes only: Scriban template body and output-type mode. Default 'string'
  // when the node is a Transform; null on every other kind.
  template: string | null = null;
  outputType: WorkflowTransformOutputType = 'string';
  // Swarm nodes only. Validator (CodeFlow.Api/Validation/WorkflowValidator.cs) requires
  // protocol + n + contributor + synthesizer; coordinator is exclusive to the Coordinator
  // protocol; token budget is optional (>0 when set). Null on every other kind.
  swarmProtocol: WorkflowSwarmProtocol | null = null;
  swarmN: number | null = null;
  contributorAgentKey: string | null = null;
  contributorAgentVersion: number | null = null;
  synthesizerAgentKey: string | null = null;
  synthesizerAgentVersion: number | null = null;
  coordinatorAgentKey: string | null = null;
  coordinatorAgentVersion: number | null = null;
  swarmTokenBudget: number | null = null;
  traceState: WorkflowNodeTraceState = null;
  /**
   * Token Usage Tracking [Slice 7]: optional per-node overlay populated by the
   * trace-detail page when this canvas is rendering an executed trace. When non-null,
   * the node component renders a compact badge in the head showing input/output
   * tokens consumed by this node (or — for Subflow / ReviewLoop / Swarm nodes —
   * rolled up from the descendant child saga that ran inside it). Always null in
   * the editor surface; only the readonly canvas hydrates this from
   * `[tokenUsageByNodeId]`.
   */
  tokenUsageOverlay: WorkflowNodeTokenOverlay | null = null;

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
    template?: string | null;
    outputType?: WorkflowTransformOutputType;
    swarmProtocol?: WorkflowSwarmProtocol | null;
    swarmN?: number | null;
    contributorAgentKey?: string | null;
    contributorAgentVersion?: number | null;
    synthesizerAgentKey?: string | null;
    synthesizerAgentVersion?: number | null;
    coordinatorAgentKey?: string | null;
    coordinatorAgentVersion?: number | null;
    swarmTokenBudget?: number | null;
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
    this.template = params.template ?? null;
    this.outputType = params.outputType ?? 'string';
    this.swarmProtocol = params.swarmProtocol ?? null;
    this.swarmN = params.swarmN ?? null;
    this.contributorAgentKey = params.contributorAgentKey ?? null;
    this.contributorAgentVersion = params.contributorAgentVersion ?? null;
    this.synthesizerAgentKey = params.synthesizerAgentKey ?? null;
    this.synthesizerAgentVersion = params.synthesizerAgentVersion ?? null;
    this.coordinatorAgentKey = params.coordinatorAgentKey ?? null;
    this.coordinatorAgentVersion = params.coordinatorAgentVersion ?? null;
    this.swarmTokenBudget = params.swarmTokenBudget ?? null;

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
