import { AgentConfig, AgentOutputDeclaration } from '../../core/models';

export type FormPresetKey = 'passthrough-approval' | 'edit-then-approve' | 'multi-action';

export interface FormPreset {
  key: FormPresetKey;
  label: string;
  summary: string;
  /**
   * Build the partial AgentConfig fragment the preset contributes. Caller merges into the
   * full config; preset only fills hitl-relevant fields (outputTemplate, outputs,
   * decisionOutputTemplates).
   */
  build(options?: FormPresetOptions): FormPresetResult;
}

export interface FormPresetOptions {
  /** For edit-then-approve: the workflow-variable key to pre-fill from. */
  sourceVariableKey?: string;
  /** For multi-action: the port names to include. Defaults to ['Approved', 'Rejected']. */
  portNames?: string[];
}

export interface FormPresetResult {
  outputTemplate: string;
  outputs: AgentOutputDeclaration[];
  decisionOutputTemplates?: Record<string, string>;
}

const PASSTHROUGH: FormPreset = {
  key: 'passthrough-approval',
  label: 'Passthrough approval',
  summary: 'Single Approved port. Reviewer sees the upstream artifact and approves it as-is — no editing.',
  build: () => ({
    outputTemplate: '{{ input }}',
    outputs: [
      { kind: 'Approved', description: 'Operator approves the upstream artifact unchanged.', payloadExample: null },
    ],
  }),
};

const EDIT_THEN_APPROVE: FormPreset = {
  key: 'edit-then-approve',
  label: 'Edit-then-approve',
  summary: 'Reviewer can edit the artifact in a text area pre-filled from a workflow variable, then approve.',
  build: (opts) => {
    const sourceKey = (opts?.sourceVariableKey?.trim()) || 'draft';
    const safeKey = isSimpleIdentifier(sourceKey) ? `workflow.${sourceKey}` : `workflow["${sourceKey.replace(/"/g, '\\"')}"]`;
    return {
      outputTemplate: `{{ editedText:textarea }}\n\n_(Pre-filled from \`workflow.${sourceKey}\`. Edit to override; submit blank to use the original.)_`,
      outputs: [
        { kind: 'Approved', description: 'Operator approves; uses edited text when supplied, else the original.', payloadExample: null },
      ],
      decisionOutputTemplates: {
        // Scriban quirk: empty strings are truthy under `if`, and the field may be missing
        // entirely if the reviewer doesn't expand the textarea. `(input.editedText ?? "").size > 0`
        // covers both "absent" and "empty" with the same branch.
        Approved: `{{ if (input.editedText ?? "").size > 0 }}{{ input.editedText }}{{ else }}{{ ${safeKey} }}{{ end }}`,
      },
    };
  },
};

const MULTI_ACTION: FormPreset = {
  key: 'multi-action',
  label: 'Multi-action',
  summary: 'Multiple ports (e.g. Approved / Rejected / Cancelled). Reviewer picks the action; the upstream artifact passes through.',
  build: (opts) => {
    const ports = (opts?.portNames && opts.portNames.length > 0)
      ? opts.portNames.map(p => p.trim()).filter(p => p.length > 0)
      : ['Approved', 'Rejected'];
    const seen = new Set<string>();
    const dedupedPorts = ports.filter(p => {
      if (seen.has(p)) return false;
      seen.add(p);
      return true;
    });
    return {
      outputTemplate: '{{ input }}',
      outputs: dedupedPorts.map(kind => ({
        kind,
        description: `Operator chose ${kind}.`,
        payloadExample: null,
      })),
    };
  },
};

export const FORM_PRESETS: ReadonlyArray<FormPreset> = [PASSTHROUGH, EDIT_THEN_APPROVE, MULTI_ACTION];

export function getFormPreset(key: FormPresetKey): FormPreset | undefined {
  return FORM_PRESETS.find(p => p.key === key);
}

function isSimpleIdentifier(name: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(name);
}
