import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  EventEmitter,
  Input,
  NgZone,
  OnChanges,
  OnDestroy,
  Output,
  SimpleChanges,
  ViewChild,
  effect,
  inject
} from '@angular/core';
import { ThemeService } from '../../../core/theme.service';
import { LoggerService } from '../../../core/logger.service';
import { ensureMonacoEditorStyles, ensureMonacoEnvironment, loadMonacoEditor } from './monaco-environment';
import { getSnippetsForContext, ScriptSnippet, SnippetContext, SnippetKind } from './script-snippets';
import { buildTemplateSuggestions, isInsideScribanTag } from './template-completion';

export interface MonacoMarker {
  startLineNumber: number;
  startColumn: number;
  endLineNumber: number;
  endColumn: number;
  message: string;
  severity?: 'error' | 'warning' | 'info';
}

/** E1: ambient TS declarations Monaco merges into the JS language service so authors get
 *  IntelliSense + inline errors against the script sandbox API. Globally scoped (Monaco's
 *  TS service is process-wide), so the active set is replaced on editor focus — only one
 *  editor is focused at a time, which matches the typical authoring workflow. */
export interface MonacoAmbientLib {
  filePath: string;
  content: string;
}

interface TypescriptDefaults {
  setCompilerOptions(options: Record<string, unknown>): void;
  setDiagnosticsOptions(options: { noSemanticValidation: boolean; noSyntaxValidation: boolean; diagnosticCodesToIgnore?: number[] }): void;
  setExtraLibs(libs: Array<{ content: string; filePath: string }>): void;
}

interface TypescriptRuntime {
  javascriptDefaults: TypescriptDefaults;
  ScriptTarget: { ES2020: number };
  ModuleResolutionKind: { NodeJs: number };
}

/**
 * Monaco wrapper for editing scripts and templates anywhere in the authoring
 * surface. Monaco is lazy-loaded the first time the component mounts so route
 * bundles stay small; the fallback path (loader failure) still renders a
 * working textarea so authors are never locked out.
 */
@Component({
  selector: 'cf-monaco-script-editor',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div #host class="monaco-host"></div>
    @if (fallback) {
      <textarea
        #fallbackArea
        class="monaco-fallback"
        [value]="value"
        (input)="onFallbackInput($event)"
        spellcheck="false"></textarea>
    }
  `,
  styles: [`
    :host {
      display: block;
      position: relative;
      min-height: 240px;
      border: 1px solid var(--border);
      border-radius: 4px;
      overflow: hidden;
    }
    .monaco-host { position: absolute; inset: 0; }
    .monaco-fallback {
      width: 100%;
      min-height: 240px;
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 13px;
      padding: 0.5rem;
      background: var(--bg);
      color: var(--text);
      border: 0;
    }
  `]
})
export class MonacoScriptEditorComponent implements AfterViewInit, OnChanges, OnDestroy {
  private readonly ngZone = inject(NgZone);
  private readonly themeService = inject(ThemeService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly logger = inject(LoggerService);

  @ViewChild('host', { static: true }) hostRef!: ElementRef<HTMLDivElement>;

  @Input() value = '';
  @Input() language = 'javascript';
  @Input() markers: MonacoMarker[] = [];
  @Input() readOnly = false;
  /** E1: per-script ambient declarations. Replaces Monaco's global extra-lib set on focus.
   *  Only meaningful when language === 'javascript'. */
  @Input() ambientLibs: MonacoAmbientLib[] = [];
  /** E2: which CodeFlow script slot this editor backs, used to pick the snippet subset
   *  whose generated code compiles in this slot's ambient typings. Unset => no snippets. */
  @Input() snippetKind?: SnippetKind;
  /** E2: pairs with `snippetKind`. When the slot is inside a ReviewLoop child (so
   *  `round` / `maxRounds` / `isLastRound` are bound), additional snippets become eligible. */
  @Input() snippetInLoop = false;
  /** E3: enable Scriban template autocomplete inside `{{ ... }}`. Suggests stock partial
   *  includes, loop bindings, and the `input` variable. Outside template tags no
   *  completions fire. Set on prompt-template / Hitl-outputTemplate editors. */
  @Input() templateCompletion = false;

  @Output() valueChange = new EventEmitter<string>();

  fallback = false;

  private editor?: unknown;
  private monacoApi?: typeof import('monaco-editor');
  private model?: unknown;
  private disposeListeners: Array<() => void> = [];
  private readonly ownerId = `cf-monaco-${Math.random().toString(36).slice(2)}`;

  constructor() {
    effect(() => {
      const mode = this.themeService.theme();
      this.applyTheme(mode === 'light' ? 'vs' : 'vs-dark');
    });
  }

  async ngAfterViewInit(): Promise<void> {
    try {
      await ensureMonacoEditorStyles();
      ensureMonacoEnvironment();
      const monaco = await loadMonacoEditor();
      this.monacoApi = monaco;

      // E1: enable TS-style checking on JS so ambient .d.ts is surfaced as autocomplete
      // and unknown-symbol errors. Idempotent — Monaco's defaults are process-wide.
      this.configureJavascriptDefaults(monaco);
      // E2: register the snippet completion provider once. Idempotent.
      MonacoScriptEditorComponent.ensureSnippetProvider(monaco);
      // E3: register the Scriban-template completion provider once. Idempotent.
      MonacoScriptEditorComponent.ensureTemplateProvider(monaco);

      this.ngZone.runOutsideAngular(() => {
        const editor = monaco.editor.create(this.hostRef.nativeElement, {
          value: this.value,
          language: this.language,
          theme: this.themeService.theme() === 'light' ? 'vs' : 'vs-dark',
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          automaticLayout: true,
          fontSize: 13,
          tabSize: 2,
          wordWrap: 'on',
          fixedOverflowWidgets: true,
          readOnly: this.readOnly
        });

        const changeDisposable = editor.onDidChangeModelContent(() => {
          const current = editor.getValue();
          if (current !== this.value) {
            this.value = current;
            this.ngZone.run(() => this.valueChange.emit(current));
          }
        });
        this.disposeListeners.push(() => changeDisposable.dispose());

        // E1: when this editor takes focus, swap its ambient libs into Monaco's global
        // TS service. Other editors lose their IntelliSense until they regain focus —
        // acceptable since only one editor is interactive at a time.
        const focusDisposable = editor.onDidFocusEditorWidget(() => {
          this.applyAmbientLibs();
        });
        this.disposeListeners.push(() => focusDisposable.dispose());

        this.editor = editor;
        this.model = editor.getModel();
        this.applyMarkers();
        // E2: register this editor's snippet context against its model URI so the global
        // completion provider returns the right subset for the focused editor.
        this.registerSnippetContextForModel();
        // E3: opt this editor's model into Scriban-template autocomplete if enabled.
        this.registerTemplateCompletionForModel();
      });
    } catch (err) {
      this.logger.warn('Monaco editor failed to load; falling back to plain textarea.', err);
      this.fallback = true;
    }
  }

  private static javascriptDefaultsConfigured = false;

  /** Runtime accessor for Monaco's TS language service. The 0.55 typings deprecate the
   *  surface (typed as `{ deprecated: true }`), but the runtime exports `javascriptDefaults`,
   *  `ScriptTarget`, and friends. The Vite/esbuild build resolves correctly without the
   *  `monaco.contribution` import (which has only `export {}` types). */
  private static getTsRuntime(monaco: typeof import('monaco-editor')): TypescriptRuntime {
    return (monaco.languages as unknown as { typescript: TypescriptRuntime }).typescript;
  }

  private configureJavascriptDefaults(monaco: typeof import('monaco-editor')): void {
    if (MonacoScriptEditorComponent.javascriptDefaultsConfigured) return;
    const ts = MonacoScriptEditorComponent.getTsRuntime(monaco);
    ts.javascriptDefaults.setCompilerOptions({
      allowJs: true,
      checkJs: true,
      noLib: false,
      target: ts.ScriptTarget.ES2020,
      moduleResolution: ts.ModuleResolutionKind.NodeJs,
      allowNonTsExtensions: true,
      noEmit: true,
      strict: false
    });
    ts.javascriptDefaults.setDiagnosticsOptions({
      noSemanticValidation: false,
      noSyntaxValidation: false,
      // Suppress "Parameter 'x' implicitly has an 'any' type" — script authors don't write
      // function defs but do use callbacks freely. 1375 = "await' expressions are only
      // allowed at the top level of a file when that file is a module" — we run scripts
      // outside module scope, so allow.
      diagnosticCodesToIgnore: [7006, 7044, 1375]
    });
    MonacoScriptEditorComponent.javascriptDefaultsConfigured = true;
  }

  private applyAmbientLibs(): void {
    if (!this.monacoApi) return;
    const ts = MonacoScriptEditorComponent.getTsRuntime(this.monacoApi);
    ts.javascriptDefaults.setExtraLibs(this.ambientLibs.map(lib => ({
      content: lib.content,
      filePath: lib.filePath
    })));
  }

  /** E2: model URI → snippet context so the global completion provider can return the
   *  right subset for the editor whose model is being completed against. */
  private static snippetContextByModelUri = new Map<string, SnippetContext>();
  private static snippetProviderRegistered = false;

  private static ensureSnippetProvider(monaco: typeof import('monaco-editor')): void {
    if (MonacoScriptEditorComponent.snippetProviderRegistered) return;
    MonacoScriptEditorComponent.snippetProviderRegistered = true;
    monaco.languages.registerCompletionItemProvider('javascript', {
      // The snippet labels begin with "cf:"; offer suggestions when authors type any prefix
      // (no trigger chars) so Monaco's word-based fuzzy match handles ranking.
      provideCompletionItems(model, position): import('monaco-editor').languages.ProviderResult<import('monaco-editor').languages.CompletionList> {
        const ctx = MonacoScriptEditorComponent.snippetContextByModelUri.get(model.uri.toString());
        if (!ctx) return { suggestions: [] };
        const word = model.getWordUntilPosition(position);
        const range: import('monaco-editor').IRange = {
          startLineNumber: position.lineNumber,
          endLineNumber: position.lineNumber,
          startColumn: word.startColumn,
          endColumn: word.endColumn,
        };
        const suggestions = getSnippetsForContext(ctx)
          .map<import('monaco-editor').languages.CompletionItem>((s: ScriptSnippet) => ({
            label: s.legacy ? `${s.label}  (legacy)` : s.label,
            kind: monaco.languages.CompletionItemKind.Snippet,
            insertText: s.insertText,
            insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
            documentation: { value: s.documentation, isTrusted: false },
            detail: s.detail,
            range,
            sortText: (s.legacy ? '1' : '0') + s.label,
          }));
        return { suggestions };
      },
    });
  }

  private registerSnippetContextForModel(): void {
    if (!this.snippetKind || !this.model) return;
    const model = this.model as import('monaco-editor').editor.ITextModel;
    MonacoScriptEditorComponent.snippetContextByModelUri.set(model.uri.toString(), {
      kind: this.snippetKind,
      inLoop: this.snippetInLoop,
    });
  }

  private unregisterSnippetContextForModel(): void {
    if (!this.model) return;
    const model = this.model as import('monaco-editor').editor.ITextModel;
    MonacoScriptEditorComponent.snippetContextByModelUri.delete(model.uri.toString());
  }

  /** E3: model URIs whose editor has opted into Scriban-template autocomplete. The
   *  global provider checks membership and gates on cursor-inside-`{{...}}` before firing. */
  private static templateCompletionModelUris = new Set<string>();
  private static templateProviderRegistered = false;

  private static ensureTemplateProvider(monaco: typeof import('monaco-editor')): void {
    if (MonacoScriptEditorComponent.templateProviderRegistered) return;
    MonacoScriptEditorComponent.templateProviderRegistered = true;
    monaco.languages.registerCompletionItemProvider('plaintext', {
      // Trigger on word characters and on '"' (used inside `include "..."`).
      triggerCharacters: ['{', '"', '@', '/'],
      provideCompletionItems(model, position): import('monaco-editor').languages.ProviderResult<import('monaco-editor').languages.CompletionList> {
        const uri = model.uri.toString();
        if (!MonacoScriptEditorComponent.templateCompletionModelUris.has(uri)) {
          return { suggestions: [] };
        }
        const offset = model.getOffsetAt(position);
        const text = model.getValue();
        if (!isInsideScribanTag(text, offset)) {
          return { suggestions: [] };
        }
        const word = model.getWordUntilPosition(position);
        const range: import('monaco-editor').IRange = {
          startLineNumber: position.lineNumber,
          endLineNumber: position.lineNumber,
          startColumn: word.startColumn,
          endColumn: word.endColumn,
        };
        const suggestions = buildTemplateSuggestions().map<import('monaco-editor').languages.CompletionItem>(s => ({
          label: s.label,
          kind: monaco.languages.CompletionItemKind.Variable,
          insertText: s.insertText,
          insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
          documentation: { value: s.documentation, isTrusted: false },
          detail: s.detail,
          range,
          sortText: s.sortKey ?? s.label,
          filterText: s.filterText,
        }));
        return { suggestions };
      },
    });
  }

  private registerTemplateCompletionForModel(): void {
    if (!this.model) return;
    const model = this.model as import('monaco-editor').editor.ITextModel;
    const uri = model.uri.toString();
    if (this.templateCompletion) {
      MonacoScriptEditorComponent.templateCompletionModelUris.add(uri);
    } else {
      MonacoScriptEditorComponent.templateCompletionModelUris.delete(uri);
    }
  }

  private unregisterTemplateCompletionForModel(): void {
    if (!this.model) return;
    const model = this.model as import('monaco-editor').editor.ITextModel;
    MonacoScriptEditorComponent.templateCompletionModelUris.delete(model.uri.toString());
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (!this.editor || !this.monacoApi) return;

    if (changes['value']) {
      const editor = this.editor as {
        getValue(): string;
        setValue(value: string): void;
      };
      if (editor.getValue() !== this.value) {
        editor.setValue(this.value ?? '');
      }
    }

    if (changes['language'] && this.model) {
      this.monacoApi.editor.setModelLanguage(
        this.model as import('monaco-editor').editor.ITextModel,
        this.language
      );
    }

    if (changes['readOnly']) {
      const editor = this.editor as { updateOptions(opts: { readOnly: boolean }): void };
      editor.updateOptions({ readOnly: this.readOnly });
    }

    if (changes['markers']) {
      this.applyMarkers();
    }

    if (changes['ambientLibs'] && this.editor) {
      const editor = this.editor as { hasTextFocus(): boolean };
      if (editor.hasTextFocus?.()) {
        this.applyAmbientLibs();
      }
    }

    if (changes['snippetKind'] || changes['snippetInLoop']) {
      this.registerSnippetContextForModel();
    }

    if (changes['templateCompletion']) {
      this.registerTemplateCompletionForModel();
    }
  }

  ngOnDestroy(): void {
    for (const dispose of this.disposeListeners) {
      try { dispose(); } catch { /* ignore */ }
    }
    this.disposeListeners = [];
    this.unregisterSnippetContextForModel();
    this.unregisterTemplateCompletionForModel();
    const editor = this.editor as { dispose?: () => void } | undefined;
    editor?.dispose?.();
    const model = this.model as { dispose?: () => void } | undefined;
    model?.dispose?.();
  }

  onFallbackInput(event: Event): void {
    const value = (event.target as HTMLTextAreaElement).value;
    this.value = value;
    this.valueChange.emit(value);
  }

  private applyTheme(theme: 'vs' | 'vs-dark'): void {
    if (!this.monacoApi) return;
    this.monacoApi.editor.setTheme(theme);
  }

  private applyMarkers(): void {
    if (!this.monacoApi || !this.model) return;
    const severityLookup = {
      error: this.monacoApi.MarkerSeverity.Error,
      warning: this.monacoApi.MarkerSeverity.Warning,
      info: this.monacoApi.MarkerSeverity.Info
    } as const;
    const markers = this.markers.map(m => ({
      startLineNumber: m.startLineNumber,
      startColumn: m.startColumn,
      endLineNumber: m.endLineNumber,
      endColumn: m.endColumn,
      message: m.message,
      severity: severityLookup[m.severity ?? 'error']
    }));
    this.monacoApi.editor.setModelMarkers(this.model as import('monaco-editor').editor.ITextModel, this.ownerId, markers);
  }
}
